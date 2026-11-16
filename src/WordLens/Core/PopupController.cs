using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WordLens.UI;

namespace WordLens.Core
{
    /// <summary>
    /// Owns the lookup popup lifecycle: turns a double-click (or the global hotkey) into a
    /// background capture + lookup, then presents the result near the pointer. All UI work is
    /// marshalled back to the dispatcher that created the controller.
    /// </summary>
    public sealed class PopupController : IDisposable
    {
        // Window classes and processes that belong to the shell itself: double-clicking them
        // must never open a lookup (empty titles/desktop icons/taskbar would otherwise fire).
        private static readonly string[] ShellNoiseClasses =
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow", "SysListView32",
            "DirectUIHWND", "XamlExplorerHostIslandWindow", "TaskListThumbnailWnd"
        };

        private static readonly string[] ShellNoiseProcesses =
        {
            "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
            "TextInputHost", "LockApp", "ApplicationFrameHost"
        };

        private readonly AppSettings _settings;
        private readonly SelectionCapture _capture;
        private readonly LookupService _lookup;
        private readonly TtsService _tts;
        private readonly HistoryStore _history;
        private readonly MouseHook _mouse;
        private readonly KeyboardHook _keyHook;
        private readonly PopupWindow _window;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _closeTimer;
        private readonly DispatcherTimer _fgTimer;

        private IntPtr _sourceRoot;
        private int _generation;
        private LookupResult _current;
        private CaptureResult _lastCapture;
        private bool _disposed;

        public PopupWindow Window => _window;

        public event Action SettingsRequested;

        public PopupController(AppSettings settings, SelectionCapture capture, LookupService lookup,
            TtsService tts, HistoryStore history, MouseHook mouse, KeyboardHook keyHook, PopupWindow window)
        {
            _settings = settings;
            _capture = capture;
            _lookup = lookup;
            _tts = tts;
            _history = history;
            _mouse = mouse;
            _keyHook = keyHook;
            _window = window;
            _dispatcher = Dispatcher.CurrentDispatcher;

            _mouse.DoubleClick += OnDoubleClick;
            _mouse.MouseDown += OnMouseDown;
            _keyHook.KeyDown += OnKeyDown;

            _window.SpeakRequested += (s, e) => SpeakCurrent();
            _window.FavoriteRequested += (s, e) => ToggleFavorite();
            _window.CopyRequested += (s, e) => CopyCurrent();
            _window.SearchRequested += (s, e) => SearchCurrent();
            _window.SettingsRequested += (s, e) =>
            {
                HidePopup();
                SettingsRequested?.Invoke();
            };
            _window.CloseRequested += (s, e) => HidePopup();
            _window.SuggestionRequested += (s, word) => LookupSuggestion(word);

            _closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(3, settings.CloseTimeoutSec))
            };
            _closeTimer.Tick += (s, e) =>
            {
                Log.Info("popup auto-closed after timeout");
                HidePopup();
            };

            // The popup is a no-activate window, so it never sees focus changes itself. Watch the
            // foreground window while it is open and close as soon as the user switches away.
            _fgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _fgTimer.Tick += (s, e) =>
            {
                if (!_window.IsShown)
                {
                    _fgTimer.Stop();
                    return;
                }
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && _sourceRoot != IntPtr.Zero && fg != _sourceRoot)
                {
                    Log.Info("popup closed because foreground changed: " + fg + " != " + _sourceRoot);
                    HidePopup();
                }
            };
        }

        private void OnDoubleClick(object sender, MouseHookEventArgs e)
        {
            if (_disposed || !_settings.Enabled || !_settings.LookupOnDoubleClick) return;
            if (Native.AnyModifierDown()) return;
            if (e.Hwnd == IntPtr.Zero || Native.PidOf(e.Hwnd) == Environment.ProcessId) return;

            string process = Native.ProcessNameOf(e.Hwnd);
            if (IsExcluded(process)) return;

            string className = Native.ClassOf(e.Hwnd);
            if (IsShellNoise(className, process)) return;
            if (!IsClientAreaPoint(e.Hwnd, e.X, e.Y)) return;
            if (!_settings.LookupOnDoubleClick) return;
            Log.Info("double-click at " + e.X + "," + e.Y + " hwnd=" + e.Hwnd +
                     " app=" + process + " class=" + className + " injected=" + e.Injected);

            int x = e.X;
            int y = e.Y;
            IntPtr hwnd = e.Hwnd;
            int gen = Interlocked.Increment(ref _generation);

            Task.Run(async () =>
            {
                try
                {
                    int delay = Math.Max(0, _settings.LookupDelayMs);
                    if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
                    if (gen != Volatile.Read(ref _generation)) return;

                    CaptureResult cap = await _capture.CaptureAsync(hwnd, x, y).ConfigureAwait(false);
                    if (gen != Volatile.Read(ref _generation)) return;

                    if (cap == null || !cap.HasText)
                    {
                        Log.Info("no selection captured (method=" + (cap == null ? "null" : cap.Method) + ")");
                        return;
                    }

                    string text = TextUtil.CleanSelection(cap.Text, _settings.MaxSelectionChars);
                    if (text == null)
                    {
                        Log.Info("selection rejected by cleaner: '" + TextUtil.SafeShort(cap.Text, 60) + "'");
                        return;
                    }

                    LookupResult result = _lookup.Lookup(text, cap.ProcessName);
                    if (gen != Volatile.Read(ref _generation)) return;
                    await _dispatcher.InvokeAsync(() => Present(result, cap, x, y, gen));
                }
                catch (Exception ex)
                {
                    Log.Error("double-click lookup failed", ex);
                }
            });
        }

        private void Present(LookupResult result, CaptureResult capture, int x, int y, int gen)
        {
            if (_disposed || result == null || gen != Volatile.Read(ref _generation)) return;

            _current = result;
            _lastCapture = capture;

            string headword = result.Found ? result.Primary.Row.Headword : result.DisplayQuery;
            if (_settings.HistoryEnabled)
                _history.AddHistory(headword, capture == null ? "" : capture.ProcessName);

            bool favorite = _history.IsFavorite(headword);
            _window.Render(result, favorite, _settings);
            _window.ShowNearPhysical(x, y);

            _sourceRoot = Native.GetAncestor(Native.GetForegroundWindow(), 2);
            if (_sourceRoot == IntPtr.Zero) _sourceRoot = Native.GetForegroundWindow();

            _fgTimer.Start();
            StartCloseTimer();
            if (!_keyHook.IsRunning) _keyHook.Start();

            Log.Info("popup shown for '" + TextUtil.SafeShort(headword, 40) + "' found=" + result.Found +
                     " entries=" + result.Entries.Count + " captureMs=" + (capture?.ElapsedMs ?? -1));

            if (_settings.AutoSpeak && result.Found) SpeakWord(result.Primary);
        }

        private void StartCloseTimer()
        {
            _closeTimer.Stop();
            _closeTimer.Interval = TimeSpan.FromSeconds(Math.Max(3, _settings.CloseTimeoutSec));
            _closeTimer.Start();
        }

        private void OnMouseDown(object sender, MouseHookEventArgs e)
        {
            if (_window.IsShown && _settings.CloseOnOutsideClick &&
                Native.PidOf(e.Hwnd) != Environment.ProcessId)
                _dispatcher.BeginInvoke(new Action(HidePopup));
        }

        private void OnKeyDown(object sender, KeyHookEventArgs e)
        {
            if (_window.IsShown && e.VkCode == 27) _dispatcher.BeginInvoke(new Action(HidePopup));
        }

        public void HidePopup()
        {
            _closeTimer.Stop();
            _fgTimer.Stop();
            Log.Info("popup hidden");
            try { _keyHook.Stop(); } catch { }
            _tts.Stop();
            try { _window.HideAnimated(); } catch { }
        }

        private void SpeakCurrent()
        {
            if (_current != null && _current.Found) SpeakWord(_current.Primary);
        }

        private void SpeakWord(LookupEntry entry)
        {
            if (entry == null) return;
            _tts.Speak(entry.Row.Headword, entry.IsChinese);
        }

        private void ToggleFavorite()
        {
            if (_current == null || !_current.Found) return;
            LookupEntry entry = _current.Primary;
            string phonetic = PhoneticUtil.Pretty(entry.Row.Phonetic, entry.IsChinese);
            string translation = string.Join(" / ", entry.TranslationLines);
            bool favorite = _history.ToggleFavorite(entry.Row.Headword,
                entry.Dict != null ? entry.Dict.Name : "", phonetic, TextUtil.SafeShort(translation, 300));
            _window.SetFavorite(favorite);
        }

        private void CopyCurrent()
        {
            if (_current == null) return;
            string text = _current.Found ? _current.Primary.Row.Headword : _current.DisplayQuery;
            try
            {
                ClipboardCompat.TrySetText(text);
                _window.ShowCopyFeedback();
            }
            catch (Exception ex)
            {
                Log.Warn("clipboard copy failed: " + ex.Message);
            }
        }

        private void SearchCurrent()
        {
            if (_current == null) return;
            OpenSearch(_current.Found ? _current.Primary.Row.Headword : _current.DisplayQuery);
        }

        public static void OpenSearch(string query)
        {
            try
            {
                string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query ?? "");
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("open browser failed: " + ex.Message);
            }
        }

        private void LookupSuggestion(string word)
        {
            try
            {
                LookupResult result = _lookup.Lookup(word, _lastCapture == null ? "" : _lastCapture.ProcessName);
                int gen = Volatile.Read(ref _generation);
                Present(result, _lastCapture, (int)_window.Position.X, (int)_window.Position.Y, gen);
            }
            catch (Exception ex)
            {
                Log.Error("suggestion lookup failed", ex);
            }
        }

        /// <summary>Global hotkey path: look up whatever is selected in the foreground window.</summary>
        public void LookupSelectionAtCursor()
        {
            if (_disposed || !_settings.Enabled) return;

            Native.GetCursorPos(out Native.POINT pt);
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            int x = pt.X;
            int y = pt.Y;
            int gen = Interlocked.Increment(ref _generation);

            Task.Run(async () =>
            {
                try
                {
                    CaptureResult cap = await _capture.CaptureAsync(hwnd, x, y).ConfigureAwait(false);
                    if (gen != Volatile.Read(ref _generation)) return;
                    if (cap == null || !cap.HasText)
                    {
                        Log.Info("hotkey: no selection captured");
                        return;
                    }

                    string text = TextUtil.CleanSelection(cap.Text, _settings.MaxSelectionChars);
                    if (text == null) return;

                    LookupResult result = _lookup.Lookup(text, cap.ProcessName);
                    await _dispatcher.InvokeAsync(() => Present(result, cap, x, y, gen));
                }
                catch (Exception ex)
                {
                    Log.Error("hotkey lookup failed", ex);
                }
            });
        }

        public void TestTts(string text, bool chinese)
        {
            _tts.Speak(text, chinese);
        }

        private bool IsExcluded(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            foreach (string raw in _settings.ExcludedProcesses)
            {
                string entry = (raw ?? "").Trim();
                if (entry.Length == 0 || entry.StartsWith("#")) continue;
                if (entry.IndexOf('*') >= 0)
                {
                    string pattern = "^" + Regex.Escape(entry).Replace("\\*", ".*") + "$";
                    if (Regex.IsMatch(processName, pattern, RegexOptions.IgnoreCase)) return true;
                }
                else if (string.Equals(processName, entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsShellNoise(string className, string processName)
        {
            if (!string.IsNullOrEmpty(className))
            {
                foreach (string noise in ShellNoiseClasses)
                    if (string.Equals(className, noise, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (!string.IsNullOrEmpty(processName))
            {
                foreach (string noise in ShellNoiseProcesses)
                    if (string.Equals(processName, noise, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Rejects double-clicks on title bars, borders and buttons.</summary>
        private static bool IsClientAreaPoint(IntPtr hwnd, int x, int y)
        {
            try
            {
                if (!Native.GetClientRect(hwnd, out Native.RECT rc)) return true;
                Native.POINT pt;
                pt.X = x;
                pt.Y = y;
                if (!Native.ScreenToClient(hwnd, ref pt)) return true;
                return pt.X >= rc.Left && pt.Y >= rc.Top && pt.X < rc.Right && pt.Y < rc.Bottom;
            }
            catch
            {
                return true;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _mouse.DoubleClick -= OnDoubleClick;
                _mouse.MouseDown -= OnMouseDown;
                _keyHook.KeyDown -= OnKeyDown;
            }
            catch { }
            try { _closeTimer.Stop(); } catch { }
            try { _fgTimer.Stop(); } catch { }
        }
    }
}
