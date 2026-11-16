using System;
using System.Threading;

namespace WordLens.Core
{
    public sealed class KeyHookEventArgs : EventArgs
    {
        public int VkCode { get; set; }
        public bool IsDown { get; set; }
    }

    /// <summary>
    /// Low-level keyboard hook installed only while the lookup popup is visible, so that the
    /// user can dismiss it with Esc. It never swallows keys.
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        private Native.LowLevelProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private bool _disposed;

        public event EventHandler<KeyHookEventArgs> KeyDown;

        public bool IsRunning => _running && _hook != IntPtr.Zero;

        public bool Start()
        {
            if (_running) return true;
            _running = true;
            _ready.Reset();
            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "WordLens.KeyHook"
            };
            _thread.Start();
            _ready.Wait(2000);
            return _hook != IntPtr.Zero;
        }

        private void ThreadProc()
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = HookCallback;
            try
            {
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            }
            catch (Exception ex)
            {
                Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) failed", ex);
            }
            if (_hook == IntPtr.Zero)
                Log.Warn("Keyboard hook install failed, win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
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
                Log.Error("Keyboard hook loop failed", ex);
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
                int msg = wParam.ToInt32();
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                {
                    try
                    {
                        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                        var handler = KeyDown;
                        if (handler != null)
                            handler(this, new KeyHookEventArgs { VkCode = (int)data.vkCode, IsDown = true });
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Keyboard hook callback error: " + ex.Message);
                    }
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            try { if (_thread != null) _thread.Join(500); } catch { }
            _thread = null;
            _threadId = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _ready.Dispose();
        }
    }
}
