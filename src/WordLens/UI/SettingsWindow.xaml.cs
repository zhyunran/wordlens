using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using WordLens.Core;
using WordLens.Import;

namespace WordLens.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly DictionaryManager _dicts;
        private readonly TtsService _tts;
        private readonly HistoryStore _history;
        private readonly SelectionCapture _capture;
        private readonly Action _onChanged;
        private readonly ObservableCollection<DictRow> _dictRows = new ObservableCollection<DictRow>();
        private DispatcherTimer _captureCountdown;
        private int _countdown;
        private bool _loading;
        private bool _refreshing;

        public int InitialTab { get; set; }

        public SettingsWindow(AppSettings settings, DictionaryManager dicts, TtsService tts,
            HistoryStore history, SelectionCapture capture, Action onChanged)
        {
            InitializeComponent();
            _settings = settings;
            _dicts = dicts;
            _tts = tts;
            _history = history;
            _capture = capture;
            _onChanged = onChanged;
            DgDicts.ItemsSource = _dictRows;
            Loaded += OnLoaded;
            Closing += (s, e) => SaveAll();
            WireGeneral();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadGeneral();
            RefreshDictionaries();
            RefreshWordbook();
            TxtAboutPaths.Text = "程序目录：" + AppPaths.ExeDir + "\n" +
                                 "用户数据：" + AppPaths.DataDir + "\n" +
                                 "日志目录：" + AppPaths.LogDir;
            if (InitialTab >= 0 && InitialTab < Tabs.Items.Count)
                Tabs.SelectedIndex = InitialTab;
            ApplyStatus("就绪");
        }

        private void ApplyStatus(string text)
        {
            TxtStatus.Text = text;
        }

        private void WireGeneral()
        {
            ChkEnabled.Click += (s, e) =>
            {
                if (_loading) return;
                _settings.Enabled = ChkEnabled.IsChecked == true;
                _settings.Save();
                _onChanged();
                ApplyStatus(_settings.Enabled ? "已启用双击查词" : "已暂停双击查词");
            };
            ChkAutoSpeak.Click += (s, e) => { if (!_loading) { _settings.AutoSpeak = ChkAutoSpeak.IsChecked == true; _settings.Save(); } };
            ChkShowEnDef.Click += (s, e) => { if (!_loading) { _settings.ShowEnglishDefinition = ChkShowEnDef.IsChecked == true; _settings.Save(); } };
            ChkShowTags.Click += (s, e) => { if (!_loading) { _settings.ShowTags = ChkShowTags.IsChecked == true; _settings.Save(); } };
            ChkCloseOutside.Click += (s, e) => { if (!_loading) { _settings.CloseOnOutsideClick = ChkCloseOutside.IsChecked == true; _settings.Save(); } };
            ChkHistory.Click += (s, e) => { if (!_loading) { _settings.HistoryEnabled = ChkHistory.IsChecked == true; _settings.Save(); } };
            ChkAutostart.Click += (s, e) =>
            {
                if (_loading) return;
                _settings.Autostart = ChkAutostart.IsChecked == true;
                Autostart.SetEnabled(_settings.Autostart);
                _settings.Save();
                ApplyStatus(_settings.Autostart ? "已设置开机启动" : "已取消开机启动");
            };
            SldDelay.ValueChanged += (s, e) =>
            {
                TxtDelay.Text = ((int)SldDelay.Value) + " ms";
                if (!_loading) _settings.LookupDelayMs = (int)SldDelay.Value;
            };
            SldTimeout.ValueChanged += (s, e) =>
            {
                TxtTimeout.Text = ((int)SldTimeout.Value) + " 秒";
                if (!_loading) _settings.CloseTimeoutSec = (int)SldTimeout.Value;
            };
            TxtMaxChars.LostFocus += (s, e) =>
            {
                if (int.TryParse(TxtMaxChars.Text.Trim(), out int v))
                {
                    _settings.MaxSelectionChars = Math.Max(8, Math.Min(400, v));
                    TxtMaxChars.Text = _settings.MaxSelectionChars.ToString();
                    _settings.Save();
                }
            };
        }

        private void LoadGeneral()
        {
            _loading = true;
            try
            {
                ChkEnabled.IsChecked = _settings.Enabled;
                ChkAutoSpeak.IsChecked = _settings.AutoSpeak;
                ChkShowEnDef.IsChecked = _settings.ShowEnglishDefinition;
                ChkShowTags.IsChecked = _settings.ShowTags;
                ChkCloseOutside.IsChecked = _settings.CloseOnOutsideClick;
                ChkHistory.IsChecked = _settings.HistoryEnabled;
                ChkAutostart.IsChecked = _settings.Autostart;
                SldDelay.Value = Math.Max(SldDelay.Minimum, Math.Min(SldDelay.Maximum, _settings.LookupDelayMs));
                SldTimeout.Value = Math.Max(SldTimeout.Minimum, Math.Min(SldTimeout.Maximum, _settings.CloseTimeoutSec));
                TxtDelay.Text = ((int)SldDelay.Value) + " ms";
                TxtTimeout.Text = ((int)SldTimeout.Value) + " 秒";
                TxtMaxChars.Text = _settings.MaxSelectionChars.ToString();
                TxtHotkey.Text = _settings.HotkeyLookup;
                TxtSearchHotkey.Text = _settings.SearchHotkey;
                TxtExcluded.Text = string.Join(Environment.NewLine, _settings.ExcludedProcesses);
                TxtHotkeyStatus.Text = "";
                TxtSearchHotkeyStatus.Text = "";
            }
            finally
            {
                _loading = false;
            }
        }

        private void SaveAll()
        {
            try
            {
                _settings.Enabled = ChkEnabled.IsChecked == true;
                _settings.AutoSpeak = ChkAutoSpeak.IsChecked == true;
                _settings.ShowEnglishDefinition = ChkShowEnDef.IsChecked == true;
                _settings.ShowTags = ChkShowTags.IsChecked == true;
                _settings.CloseOnOutsideClick = ChkCloseOutside.IsChecked == true;
                _settings.HistoryEnabled = ChkHistory.IsChecked == true;
                _settings.Autostart = ChkAutostart.IsChecked == true;
                _settings.LookupDelayMs = (int)SldDelay.Value;
                _settings.CloseTimeoutSec = (int)SldTimeout.Value;
                if (int.TryParse(TxtMaxChars.Text.Trim(), out int v)) _settings.MaxSelectionChars = v;
                _settings.Save();
                Task.Run(() => Autostart.SetEnabled(_settings.Autostart));
                _onChanged();
            }
            catch (Exception ex) { Log.Error("Save settings failed", ex); }
        }

        // ---------------- dictionaries ----------------

        private void RefreshDictionaries()
        {
            _refreshing = true;
            try
            {
                _dictRows.Clear();
                foreach (DictInfo info in _dicts.Infos)
                {
                    var row = new DictRow
                    {
                        FilePath = info.FilePath,
                        Name = info.Name,
                        LanguageText = info.Language != null && info.Language.StartsWith("zh") ? "汉英" : "英汉",
                        CountText = info.EntryCount.ToString("N0"),
                        PriorityText = info.Priority.ToString(),
                        SourceText = info.IsBuiltin ? "内置 · " + info.Source : "用户导入",
                        Enabled = info.Enabled,
                        IsBuiltin = info.IsBuiltin
                    };
                    row.EnabledChanged += r =>
                    {
                        if (_refreshing) return;
                        _dicts.SetEnabled(r.FilePath, r.Enabled);
                        ApplyStatus((r.Enabled ? "已启用 " : "已禁用 ") + r.Name);
                    };
                    _dictRows.Add(row);
                }
            }
            finally
            {
                _refreshing = false;
            }
            ApplyStatus("共 " + _dictRows.Count + " 本词典，启用 " +
                        _dictRows.Count(x => x.Enabled) + " 本");
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要导入的词典",
                Filter = "支持的词典|*.ifo;*.zip;*.csv;*.tsv;*.txt;*.json;*.jsonl;*.wlcdb|StarDict|*.ifo;*.zip|" +
                         "文本词典|*.csv;*.tsv;*.txt|JSON|*.json;*.jsonl|WordLens|*.wlcdb|所有文件|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true) return;
            BtnImport.IsEnabled = false;
            BtnDeleteDict.IsEnabled = false;
            ImportProgress.Visibility = Visibility.Visible;
            var progressReporter = new Progress<string>(msg => { TxtDictStatus.Text = msg; });
            Task.Run(() =>
            {
                var outcomes = new List<ImportOutcome>();
                foreach (string file in dlg.FileNames)
                {
                    outcomes.Add(ImportService.Import(file, progressReporter));
                }
                return outcomes;
            }).ContinueWith(t =>
            {
                ImportProgress.Visibility = Visibility.Collapsed;
                BtnImport.IsEnabled = true;
                BtnDeleteDict.IsEnabled = true;
                if (t.IsFaulted)
                {
                    TxtDictStatus.Text = "导入失败：" + (t.Exception != null ? t.Exception.GetBaseException().Message : "未知错误");
                    return;
                }
                var failed = t.Result.Where(o => !o.Success).ToList();
                var ok = t.Result.Where(o => o.Success).ToList();
                _dicts.Reload();
                RefreshDictionaries();
                if (ok.Count > 0)
                    TxtDictStatus.Text = "成功导入：" + string.Join("、", ok.Select(o => o.Name + "（" + o.Entries.ToString("N0") + " 条）"));
                if (failed.Count > 0)
                    TxtDictStatus.Text += (ok.Count > 0 ? "；" : "") + "失败：" + string.Join("；", failed.Select(o => o.Error));
                _onChanged();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private DictRow SelectedRow()
        {
            return DgDicts.SelectedItem as DictRow;
        }

        private void BtnDeleteDict_Click(object sender, RoutedEventArgs e)
        {
            DictRow row = SelectedRow();
            if (row == null) { ApplyStatus("请先选择一本词典"); return; }
            if (row.IsBuiltin) { ApplyStatus("内置词典不能删除"); return; }
            if (MessageBox.Show(this, "确定要删除词典「" + row.Name + "」吗？", "删除词典",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _dicts.DeleteUserDict(row.FilePath);
            _dicts.Reload();
            RefreshDictionaries();
            _onChanged();
            ApplyStatus("已删除词典 " + row.Name);
        }

        private void BtnUp_Click(object sender, RoutedEventArgs e) => MovePriority(10);
        private void BtnDown_Click(object sender, RoutedEventArgs e) => MovePriority(-10);

        private void MovePriority(int delta)
        {
            DictRow row = SelectedRow();
            if (row == null) { ApplyStatus("请先选择一本词典"); return; }
            DictInfo info = _dicts.FindInfo(row.FilePath);
            if (info == null) return;
            _dicts.SetPriority(row.FilePath, Math.Max(0, info.Priority + delta));
            RefreshDictionaries();
            _onChanged();
        }

        private void BtnOpenDictFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(AppPaths.UserDictDir);
                Process.Start(new ProcessStartInfo { FileName = AppPaths.UserDictDir, UseShellExecute = true });
            }
            catch (Exception ex) { ApplyStatus("打开目录失败：" + ex.Message); }
        }

        // ---------------- wordbook ----------------

        private void RefreshWordbook()
        {
            try
            {
                LstFavorites.ItemsSource = _history.ListFavorites()
                    .Select(f => new { f.Display, f.Dict, f.Translation }).ToList();
                LstHistory.ItemsSource = _history.ListHistory(200)
                    .Select(h => new
                    {
                        h.Display,
                        h.Count,
                        h.Source,
                        TimeText = DateTimeOffset.FromUnixTimeSeconds(h.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    }).ToList();
            }
            catch (Exception ex) { Log.Warn("refresh wordbook failed: " + ex.Message); }
        }

        private void BtnRefreshFav_Click(object sender, RoutedEventArgs e) => RefreshWordbook();

        private void BtnSpeakFav_Click(object sender, RoutedEventArgs e)
        {
            dynamic item = LstFavorites.SelectedItem;
            if (item == null) return;
            string word = item.Display;
            _tts.Speak(word, TextUtil.ContainsCjk(word));
        }

        private void BtnRemoveFav_Click(object sender, RoutedEventArgs e)
        {
            dynamic item = LstFavorites.SelectedItem;
            if (item == null) { ApplyStatus("请先选择一个收藏"); return; }
            string word = item.Display;
            _history.RemoveFavorite(word);
            RefreshWordbook();
            ApplyStatus("已删除收藏 " + word);
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定清空查词历史吗？", "清空历史", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _history.ClearHistory();
            RefreshWordbook();
            ApplyStatus("已清空历史");
        }

        // ---------------- advanced ----------------

        private void BtnApplyHotkey_Click(object sender, RoutedEventArgs e)
        {
            string text = (TxtHotkey.Text ?? "").Trim();
            if (text.Length == 0)
            {
                _settings.HotkeyLookup = "";
                _settings.Save();
                _onChanged();
                TxtHotkeyStatus.Text = "已禁用快捷键";
                return;
            }
            if (!HotkeyManager.TryParse(text, out uint mods, out uint vk, out string error))
            {
                TxtHotkeyStatus.Text = "格式无效：" + error;
                return;
            }
            _settings.HotkeyLookup = text;
            _settings.Save();
            _onChanged();
            TxtHotkeyStatus.Text = "已应用（如有冲突会注册失败，详见日志）";
        }

        private void BtnApplySearchHotkey_Click(object sender, RoutedEventArgs e)
        {
            string text = (TxtSearchHotkey.Text ?? "").Trim();
            if (text.Length == 0)
            {
                _settings.SearchHotkey = "";
                _settings.Save();
                _onChanged();
                TxtSearchHotkeyStatus.Text = "已禁用输入查词快捷键";
                return;
            }
            if (!HotkeyManager.TryParse(text, out uint mods, out uint vk, out string error))
            {
                TxtSearchHotkeyStatus.Text = "格式无效：" + error;
                return;
            }
            _settings.SearchHotkey = text;
            _settings.Save();
            _onChanged();
            TxtSearchHotkeyStatus.Text = "已应用（如有冲突会注册失败，详见日志）";
        }

        private void BtnTestCapture_Click(object sender, RoutedEventArgs e)
        {
            BtnTestCapture.IsEnabled = false;
            _countdown = 3;
            TxtCaptureResult.Text = "请切换到目标程序并选中一段文字，3 秒后开始捕获…";
            _captureCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _captureCountdown.Tick += (s, args) =>
            {
                _countdown--;
                if (_countdown > 0)
                {
                    TxtCaptureResult.Text = "请切换到目标程序并选中文字，" + _countdown + " 秒后捕获…";
                    return;
                }
                _captureCountdown.Stop();
                TxtCaptureResult.Text = "正在捕获…";
                Task.Run(() =>
                {
                    CaptureResult r = _capture.CaptureAtCursor();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BtnTestCapture.IsEnabled = true;
                        if (r == null)
                        {
                            TxtCaptureResult.Text = "捕获失败：没有返回结果";
                            return;
                        }
                        TxtCaptureResult.Text =
                            "方法: " + r.Method + "\n" +
                            "耗时: " + r.ElapsedMs + " ms\n" +
                            "程序: " + r.ProcessName + " · " + r.WindowTitle + "\n" +
                            "近点击: " + r.NearClick + "\n" +
                            "文本: " + (string.IsNullOrEmpty(r.Text) ? "（空）" : r.Text) +
                            (string.IsNullOrEmpty(r.Detail) ? "" : "\n\n详情:\n" + r.Detail);
                    }));
                });
            };
            _captureCountdown.Start();
        }

        private void BtnTestTts_Click(object sender, RoutedEventArgs e)
        {
            _tts.Speak("Hello, this is WordLens. 你好，这是 WordLens 词典。", false);
            ApplyStatus("已发送发音测试");
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e) => Log.OpenLogFolder();

        private void BtnSaveExcluded_Click(object sender, RoutedEventArgs e)
        {
            var list = new List<string>();
            foreach (string line in (TxtExcluded.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            _settings.ExcludedProcesses = list;
            _settings.Save();
            _onChanged();
            ApplyStatus("已保存 " + list.Count + " 个排除程序");
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "恢复所有设置为默认值？（词典与生词本保留）", "重置设置",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppSettings defaults = new AppSettings();
            defaults.Dictionaries = _settings.Dictionaries;
            defaults.HotkeyLookup = _settings.HotkeyLookup;
            defaults.SearchHotkey = _settings.SearchHotkey;
            foreach (var prop in typeof(AppSettings).GetProperties())
            {
                if (!prop.CanWrite) continue;
                if (prop.Name == "Dictionaries") continue;
                try { prop.SetValue(_settings, prop.GetValue(defaults)); } catch { }
            }
            _settings.Save();
            LoadGeneral();
            _onChanged();
            ApplyStatus("已恢复默认设置");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public sealed class DictRow : INotifyPropertyChanged
    {
        private bool _enabled;
        public string FilePath { get; set; }
        public string Name { get; set; }
        public string LanguageText { get; set; }
        public string CountText { get; set; }
        public string PriorityText { get; set; }
        public string SourceText { get; set; }
        public bool IsBuiltin { get; set; }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                EnabledChanged?.Invoke(this);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<DictRow> EnabledChanged;
    }
}
