using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WordLens.Core;
using WordLens.Import;
using WordLens.UI;

namespace WordLens
{
    public partial class App : System.Windows.Application
    {
        private AppSettings _settings;
        private DictionaryManager _dicts;
        private LookupService _lookup;
        private TtsService _tts;
        private HistoryStore _history;
        private SelectionCapture _capture;
        private MouseHook _mouse;
        private KeyboardHook _keyHook;
        private PopupController _controller;
        private PopupWindow _popup;
        private TrayIcon _tray;
        private HotkeyManager _hotkey;
        private HotkeyManager _searchHotkey;
        private SearchWindow _searchWindow;
        private DispatcherTimer _memoryTimer;
        private SettingsWindow _settingsWindow;
        private Mutex _mutex;
        private EventWaitHandle _showSettingsEvent;
        private Thread _singleInstanceThread;
        private string _registeredHotkey;
        private string _registeredSearchHotkey;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppPaths.EnsureCreated();
            Log.Init();
            DispatcherUnhandledException += (s, args) =>
            {
                Log.Error("Unhandled dispatcher exception", args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log.Error("Unhandled domain exception", args.ExceptionObject as Exception);
            };
            _settings = AppSettings.Load();
            try
            {
                if (TryRunCli(e.Args, out int exitCode))
                {
                    Shutdown(exitCode);
                    return;
                }
                StartTrayMode(e.Args);
            }
            catch (Exception ex)
            {
                Log.Error("Startup failed", ex);
                MessageBox.Show("WordLens 启动失败：" + ex.Message + "\n\n日志：" + AppPaths.LogDir,
                    "WordLens", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        // ------------------------------------------------------------------
        // CLI
        // ------------------------------------------------------------------
        private bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;
            if (HasArg(args, "--show-settings")) return false; // handled by tray mode
            string outPath = GetArg(args, "--out");

            try
            {
                if (HasArg(args, "--help") || HasArg(args, "-h"))
                {
                    string help =
                        "WordLens 命令：\n" +
                        "  --show-settings            打开设置窗口\n" +
                        "  --query <word> [--out f]   查询单词并输出 JSON\n" +
                        "  --import <file> [--out f]  导入词典（StarDict/CSV/JSON/.wlcdb）\n" +
                        "  --capture [--out f]        捕获当前前台窗口的选中文本\n" +
                        "  --diag [--out f]           输出诊断信息\n" +
                        "  --autostart-on|--autostart-off\n" +
                        "  --version\n";
                    WriteCli(outPath, new { ok = true, help });
                    return true;
                }
                if (HasArg(args, "--version"))
                {
                    WriteCli(outPath, new { ok = true, name = AppInfo.Name, version = AppInfo.Version });
                    return true;
                }
                if (HasArg(args, "--autostart-on") || HasArg(args, "--autostart-off"))
                {
                    bool enable = HasArg(args, "--autostart-on");
                    bool ok = Autostart.SetEnabled(enable);
                    _settings.Autostart = enable;
                    _settings.Save();
                    WriteCli(outPath, new { ok, autostart = Autostart.IsEnabled(), exe = AppPaths.ExePath });
                    return true;
                }
                if (HasArg(args, "--query"))
                {
                    string query = GetArg(args, "--query");
                    var manager = new DictionaryManager(_settings);
                    manager.Reload();
                    var svc = new LookupService(manager, _settings);
                    LookupResult result = svc.Lookup(query, "cli");
                    WriteCli(outPath, ToJson(result));
                    manager.Dispose();
                    return true;
                }
                if (HasArg(args, "--import"))
                {
                    string file = GetArg(args, "--import");
                    var progress = new Progress<string>(m => Log.Info("import: " + m));
                    ImportOutcome outcome = ImportService.Import(file, progress);
                    WriteCli(outPath, new
                    {
                        ok = outcome.Success,
                        outcome.Error,
                        outcome.Name,
                        outcome.Language,
                        outcome.Entries,
                        outcome.Format,
                        outcome.DbPath
                    });
                    if (outcome.Success && _settings != null)
                    {
                        _settings.GetDictState(Path.GetFileName(outcome.DbPath)).Enabled = true;
                        _settings.Save();
                    }
                    return true;
                }
                if (HasArg(args, "--capture"))
                {
                    Thread.Sleep(120);
                    using (var capture = new SelectionCapture(_settings))
                    {
                        CaptureResult r = capture.CaptureAtCursor();
                        WriteCli(outPath, new
                        {
                            ok = r != null && r.HasText,
                            text = r == null ? null : r.Text,
                            method = r == null ? null : r.Method,
                            process = r == null ? null : r.ProcessName,
                            title = r == null ? null : r.WindowTitle,
                            nearClick = r == null ? false : r.NearClick,
                            textSurface = r == null ? false : r.TextSurfaceDetected,
                            elapsedMs = r == null ? 0 : r.ElapsedMs,
                            detail = r == null ? null : r.Detail
                        });
                    }
                    return true;
                }
                if (HasArg(args, "--diag"))
                {
                    var manager = new DictionaryManager(_settings);
                    manager.Reload();
                    var tts = new TtsService(_settings);
                    var voices = tts.Voices.Select(v => new { v.Description, v.Language }).ToList();
                    WriteCli(outPath, new
                    {
                        ok = true,
                        version = AppInfo.Version,
                        exe = AppPaths.ExePath,
                        dataDir = AppPaths.DataDir,
                        builtinDictDir = AppPaths.BuiltinDictDir,
                        userDictDir = AppPaths.UserDictDir,
                        autostart = Autostart.IsEnabled(),
                        settings = new
                        {
                            _settings.Enabled,
                            _settings.LookupDelayMs,
                            _settings.MaxSelectionChars,
                            _settings.ShowEnglishDefinition,
                            _settings.ShowTags,
                            _settings.CloseTimeoutSec,
                            _settings.HotkeyLookup,
                            _settings.ExcludedProcesses
                        },
                        dictionaries = manager.Infos.Select(d => new
                        {
                            d.Name,
                            d.Language,
                            d.EntryCount,
                            d.Priority,
                            d.Enabled,
                            d.IsBuiltin,
                            d.FilePath
                        }).ToList(),
                        voices
                    });
                    tts.Dispose();
                    manager.Dispose();
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("CLI command failed", ex);
                WriteCli(outPath, new { ok = false, error = ex.Message, detail = ex.ToString() });
                exitCode = 1;
                return true;
            }
        }

        private static object ToJson(LookupResult result)
        {
            if (result == null) return new { ok = false, error = "null result" };
            return new
            {
                ok = true,
                query = result.Query,
                displayQuery = result.DisplayQuery,
                isChinese = result.IsChinese,
                found = result.Found,
                elapsedMs = result.ElapsedMs,
                suggestions = result.Suggestions,
                entries = result.Entries.Select(e => new
                {
                    dict = e.Dict == null ? null : e.Dict.Name,
                    language = e.Dict == null ? null : e.Dict.Language,
                    headword = e.Row.Headword,
                    phoneticRaw = e.Row.Phonetic,
                    phoneticPretty = PhoneticUtil.Pretty(e.Row.Phonetic, e.IsChinese),
                    pinyin = e.Pinyin,
                    trad = e.Trad,
                    simp = e.Simp,
                    translation = e.Row.Translation,
                    translationLines = e.TranslationLines,
                    definition = e.Row.Definition,
                    definitionLines = e.DefinitionLines,
                    pos = e.Row.Pos,
                    tags = e.Row.Tags,
                    collins = e.Row.Collins,
                    oxford = e.Row.Oxford,
                    bnc = e.Row.Bnc,
                    frq = e.Row.Frq,
                    fromInflection = e.FromInflection,
                    matchedForm = e.MatchedForm
                }).ToList()
            };
        }

        private static void WriteCli(string outPath, object payload)
        {
            string json = JsonSerializer.Serialize(payload, JsonOpts);
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                try
                {
                    File.WriteAllText(outPath, json, new UTF8Encoding(false));
                }
                catch { }
            }
            try
            {
                Native.AttachConsole(uint.MaxValue);
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine(json);
            }
            catch { }
        }

        private static bool HasArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        // ------------------------------------------------------------------
        // Tray mode
        // ------------------------------------------------------------------
        private void StartTrayMode(string[] args)
        {
            _mutex = new Mutex(true, "Local\\WordLens.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                SignalExistingInstance();
                Log.Info("another WordLens instance is running; requested settings window");
                Shutdown(0);
                return;
            }

            _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WordLens.ShowSettings");
            _singleInstanceThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (_showSettingsEvent.WaitOne(5000))
                            Dispatcher.BeginInvoke(new Action(ShowSettings));
                    }
                    catch
                    {
                        break;
                    }
                }
            })
            { IsBackground = true, Name = "WordLens.IPC" };
            _singleInstanceThread.Start();

            _dicts = new DictionaryManager(_settings);
            _dicts.Reload();
            if (_dicts.Infos.Count == 0)
                Log.Warn("No dictionaries found. builtin=" + AppPaths.BuiltinDictDir + " user=" + AppPaths.UserDictDir);

            _lookup = new LookupService(_dicts, _settings);
            _tts = new TtsService(_settings);
            _history = new HistoryStore();
            _capture = new SelectionCapture(_settings);
            _popup = new PopupWindow();
            _keyHook = new KeyboardHook();
            _mouse = new MouseHook();
            _controller = new PopupController(_settings, _capture, _lookup, _tts, _history, _mouse, _keyHook, _popup);
            _controller.SettingsRequested += ShowSettings;

            _tray = new TrayIcon(_settings);
            _tray.ToggleEnabledRequested += ToggleEnabled;
            _tray.SettingsRequested += ShowSettings;
            _tray.SearchRequested += ShowSearch;
            _tray.WordbookRequested += () => ShowSettings(2);
            _tray.AutostartToggleRequested += ToggleAutostart;
            _tray.ExitRequested += ExitApp;
            _tray.SetEnabled(_settings.Enabled);
            _tray.SetAutostart(_settings.Autostart);

            _hotkey = new HotkeyManager();
            _hotkey.HotkeyPressed += (s, e) => _controller.LookupSelectionAtCursor();
            _searchHotkey = new HotkeyManager(2578);
            _searchHotkey.HotkeyPressed += (s, e) => ShowSearch();
            ApplyHotkey();

            _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _memoryTimer.Tick += (s, e) => TrimMemoryIfIdle();
            _memoryTimer.Start();

            if (IsInstalledLocation() && _settings.Autostart != Autostart.IsEnabled())
                Autostart.SetEnabled(_settings.Autostart);

            if (!_mouse.Start())
                _tray.ShowBalloon("WordLens", "全局鼠标钩子启动失败，请查看日志：" + AppPaths.LogDir);

            if (!_settings.FirstRunDone)
            {
                _settings.FirstRunDone = true;
                _settings.Save();
                _tray.ShowBalloon("WordLens 已就绪", "在任意程序中双击一个单词，即可查看音标、释义和发音。");
                if (IsInstalledLocation()) ShowSettings();
            }

            Log.Info("WordLens tray mode ready. enabled=" + _settings.Enabled +
                     " dicts=" + _dicts.Infos.Count + " autostart=" + Autostart.IsEnabled());

            if (HasArg(args, "--show-settings")) ShowSettings();
            if (HasArg(args, "--show-search")) ShowSearch();
        }

        /// <summary>Wakes an already running instance so a second launch shows its settings window.</summary>
        private void SignalExistingInstance()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (EventWaitHandle handle = EventWaitHandle.OpenExisting("Local\\WordLens.ShowSettings"))
                    {
                        handle.Set();
                        break;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(80);
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }
        }

        private void ShowSettings()
        {
            ShowSettings(-1);
        }

        private void ShowSettings(int tab)
        {
            try
            {
                try { _controller?.HidePopup(); } catch { }
                try
                {
                    if (_searchWindow != null && _searchWindow.IsVisible) _searchWindow.Close();
                }
                catch { }

                if (_settingsWindow != null && _settingsWindow.IsVisible)
                {
                    _settingsWindow.Activate();
                    BringToFront(_settingsWindow);
                    if (tab >= 0) _settingsWindow.InitialTab = tab;
                    return;
                }

                _settingsWindow = new SettingsWindow(_settings, _dicts, _tts, _history, _capture, ApplySettingsToRuntime)
                {
                    InitialTab = tab
                };
                _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                _settingsWindow.Show();
                _settingsWindow.Activate();
                BringToFront(_settingsWindow);
                _settingsWindow.Dispatcher.BeginInvoke(new Action(() => BringToFront(_settingsWindow)),
                    DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                Log.Error("ShowSettings failed", ex);
            }
        }

        private static void BringToFront(Window window)
        {
            try
            {
                if (window == null) return;
                window.Topmost = true;
                window.Topmost = false;
                IntPtr handle = new WindowInteropHelper(window).Handle;
                Native.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 67u); // SWP_NOMOVE|NOSIZE|NOZORDER|NOACTIVATE... keeps Z as is
                Native.ForceForegroundWindow(handle);
            }
            catch { }
        }

        /// <summary>Called by the settings window whenever a setting changed, to refresh live state.</summary>
        private void ApplySettingsToRuntime()
        {
            try
            {
                if (_tray != null)
                {
                    _tray.SetEnabled(_settings.Enabled);
                    _tray.SetAutostart(_settings.Autostart);
                }
                if (!_settings.Enabled && _controller != null) _controller.HidePopup();
                ApplyHotkey();
            }
            catch (Exception ex)
            {
                Log.Error("ApplySettingsToRuntime failed", ex);
            }
        }

        private void ApplyHotkey()
        {
            if (_hotkey != null)
            {
                string lookup = (_settings.HotkeyLookup ?? "").Trim();
                if (!string.Equals(lookup, _registeredHotkey, StringComparison.OrdinalIgnoreCase))
                {
                    _hotkey.Unregister();
                    _registeredHotkey = null;
                    if (lookup.Length > 0 && _hotkey.Register(lookup)) _registeredHotkey = lookup;
                }
            }
            if (_searchHotkey == null) return;
            string search = (_settings.SearchHotkey ?? "").Trim();
            if (!string.Equals(search, _registeredSearchHotkey, StringComparison.OrdinalIgnoreCase))
            {
                _searchHotkey.Unregister();
                _registeredSearchHotkey = null;
                if (search.Length > 0 && _searchHotkey.Register(search)) _registeredSearchHotkey = search;
            }
        }

        private void ShowSearch()
        {
            try
            {
                try { _controller?.HidePopup(); } catch { }

                if (_searchWindow != null && _searchWindow.IsVisible)
                {
                    _searchWindow.Activate();
                    BringToFront(_searchWindow);
                    _searchWindow.FocusInput();
                    return;
                }

                _searchWindow = new SearchWindow(_settings, _lookup, _tts, _history);
                _searchWindow.SettingsRequested += (s, e) => ShowSettings();
                _searchWindow.Closed += (s, e) => _searchWindow = null;
                _searchWindow.Show();
                _searchWindow.Activate();
                BringToFront(_searchWindow);
                _searchWindow.FocusInput();
            }
            catch (Exception ex)
            {
                Log.Error("ShowSearch failed", ex);
            }
        }

        /// <summary>Idle memory trim: Windows keeps the working set high after a lookup burst.</summary>
        private void TrimMemoryIfIdle()
        {
            try
            {
                bool popupClosed = _popup == null || !_popup.IsShown;
                bool searchClosed = _searchWindow == null || !_searchWindow.IsVisible;
                bool settingsClosed = _settingsWindow == null || !_settingsWindow.IsVisible;
                if (!popupClosed || !searchClosed || !settingsClosed) return;

                long before = Process.GetCurrentProcess().WorkingSet64;
                if (before < 188743680) return; // 180 MB
                GC.Collect(2, GCCollectionMode.Optimized, false, false);
                bool ok = Native.TrimWorkingSet();
                long after = Process.GetCurrentProcess().WorkingSet64;
                Log.Info("memory trim: " + before / 1048576 + " MB -> " + after / 1048576 + " MB, ok=" + ok);
            }
            catch (Exception ex)
            {
                Log.Warn("memory trim failed: " + ex.Message);
            }
        }

        private void ToggleEnabled()
        {
            _settings.Enabled = !_settings.Enabled;
            _settings.Save();
            if (_tray != null) _tray.SetEnabled(_settings.Enabled);
            if (!_settings.Enabled && _controller != null) _controller.HidePopup();
            Log.Info("double-click lookup " + (_settings.Enabled ? "enabled" : "disabled"));
        }

        private void ToggleAutostart()
        {
            _settings.Autostart = !_settings.Autostart;
            Autostart.SetEnabled(_settings.Autostart);
            _settings.Save();
            if (_tray != null) _tray.SetAutostart(_settings.Autostart);
            Log.Info("autostart " + (_settings.Autostart ? "enabled" : "disabled"));
        }

        private static bool IsInstalledLocation()
        {
            try
            {
                string local = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WordLens");
                return AppPaths.ExeDir.StartsWith(local, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void ExitApp()
        {
            Log.Info("Exit requested");
            Shutdown(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Info("WordLens exiting");
            try { _memoryTimer?.Stop(); } catch { }
            try { _controller?.Dispose(); } catch { }
            try { if (_searchWindow != null) _searchWindow.Close(); } catch { }
            try { _mouse?.Dispose(); } catch { }
            try { _keyHook?.Dispose(); } catch { }
            try { _capture?.Dispose(); } catch { }
            try { _hotkey?.Dispose(); } catch { }
            try { _searchHotkey?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _history?.Dispose(); } catch { }
            try { _tts?.Dispose(); } catch { }
            try { _dicts?.Dispose(); } catch { }
            try { _showSettingsEvent?.Dispose(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
