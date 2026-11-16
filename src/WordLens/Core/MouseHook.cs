using System;
using System.Diagnostics;
using System.Threading;

namespace WordLens.Core
{
    public sealed class MouseHookEventArgs : EventArgs
    {
        public int X { get; set; }
        public int Y { get; set; }
        public IntPtr Hwnd { get; set; }
        public bool Injected { get; set; }
        public uint Time { get; set; }
        public bool IsDoubleClick { get; set; }
    }

    /// <summary>
    /// Global low-level mouse hook used to notice double-clicks anywhere (including Edge's PDF
    /// viewer, Word, browsers, terminals). Runs a private message-loop thread.
    /// </summary>
    public sealed class MouseHook : IDisposable
    {
        private Native.LowLevelProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private bool _disposed;

        private uint _lastDownTime;
        private int _lastX;
        private int _lastY;
        private IntPtr _lastHwnd = IntPtr.Zero;
        private uint _lastTriggerTime;
        private int _lastTriggerX;
        private int _lastTriggerY;

        public event EventHandler<MouseHookEventArgs> MouseDown;
        public event EventHandler<MouseHookEventArgs> DoubleClick;

        public bool Start()
        {
            if (_running) return true;
            _running = true;
            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "WordLens.MouseHook"
            };
            _thread.Start();
            if (!_ready.Wait(3000))
            {
                Log.Error("Mouse hook failed to initialize in time");
                return false;
            }
            return _hook != IntPtr.Zero;
        }

        private void ThreadProc()
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = HookCallback;
            try
            {
                _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            }
            catch (Exception ex)
            {
                Log.Error("SetWindowsHookEx(WH_MOUSE_LL) failed", ex);
                _hook = IntPtr.Zero;
            }
            if (_hook == IntPtr.Zero)
                Log.Error("Mouse hook install failed, win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            else
                Log.Info("Mouse hook installed");
            _ready.Set();
            if (_hook == IntPtr.Zero) return;

            try
            {
                while (_running)
                {
                    int ret = Native.GetMessage(out Native.MSG msg, IntPtr.Zero, 0, 0);
                    if (ret <= 0) break;
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mouse hook message loop failed", ex);
            }
            finally
            {
                if (_hook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_LBUTTONDBLCLK)
                    {
                        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                        IntPtr hwnd = Native.WindowFromPoint(data.pt);
                        uint now = data.time != 0 ? data.time : (uint)Environment.TickCount;
                        bool dbl = msg == Native.WM_LBUTTONDBLCLK;
                        if (!dbl && _lastDownTime != 0 && now >= _lastDownTime)
                        {
                            uint tol = Native.GetDoubleClickTime();
                            int cx = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXDOUBLECLK));
                            int cy = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYDOUBLECLK));
                            if (now - _lastDownTime <= tol &&
                                Math.Abs(data.pt.X - _lastX) <= cx &&
                                Math.Abs(data.pt.Y - _lastY) <= cy)
                            {
                                dbl = true;
                            }
                        }
                        _lastDownTime = now;
                        _lastX = data.pt.X;
                        _lastY = data.pt.Y;
                        _lastHwnd = hwnd;

                        bool injected = (data.flags & 0x1) != 0;
                        var args = new MouseHookEventArgs
                        {
                            X = data.pt.X,
                            Y = data.pt.Y,
                            Hwnd = hwnd,
                            Injected = injected,
                            Time = now,
                            IsDoubleClick = dbl
                        };
                        if (dbl)
                        {
                            // de-bounce: don't fire twice for one physical double-click
                            bool tooSoon = _lastTriggerTime != 0 && now >= _lastTriggerTime &&
                                           now - _lastTriggerTime < 350 &&
                                           Math.Abs(data.pt.X - _lastTriggerX) < 40 &&
                                           Math.Abs(data.pt.Y - _lastTriggerY) < 40;
                            if (!tooSoon)
                            {
                                _lastTriggerTime = now;
                                _lastTriggerX = data.pt.X;
                                _lastTriggerY = data.pt.Y;
                                var handler = DoubleClick;
                                if (handler != null) handler(this, args);
                            }
                        }
                        var downHandler = MouseDown;
                        if (downHandler != null) downHandler(this, args);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Mouse hook callback error: " + ex.Message);
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            try
            {
                if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            try { if (_thread != null) _thread.Join(800); } catch { }
            _ready.Dispose();
        }
    }
}
