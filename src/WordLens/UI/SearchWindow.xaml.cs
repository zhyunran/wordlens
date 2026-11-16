using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WordLens.Core;

namespace WordLens.UI
{
    /// <summary>
    /// Type-to-search window (Ctrl+Shift+Space). Typing is debounced, then the same offline
    /// dictionary pipeline as the popup is used; results are rendered as WPF elements.
    /// </summary>
    public partial class SearchWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly LookupService _lookup;
        private readonly TtsService _tts;
        private readonly HistoryStore _history;
        private readonly DispatcherTimer _debounce;
        private LookupResult _result;
        private readonly List<string> _suggestions = new List<string>();
        private bool _loaded;
        private bool _activatedOnce;
        private bool _closing;

        public event EventHandler SettingsRequested;

        public SearchWindow(AppSettings settings, LookupService lookup, TtsService tts, HistoryStore history)
        {
            InitializeComponent();
            _settings = settings;
            _lookup = lookup;
            _tts = tts;
            _history = history;

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                RunQuery();
            };

            Loaded += (s, e) =>
            {
                _loaded = true;
                FocusInput();
                FadeIn();
                RunQuery();
            };

            // Close as soon as the window loses focus, but not before it has been activated
            // once (otherwise the initial Show() would close it immediately).
            Activated += (s, e) => _activatedOnce = true;
            Deactivated += (s, e) =>
            {
                if (_activatedOnce && !_closing) Close();
            };

            PreviewKeyDown += OnPreviewKeyDown;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true;
            base.OnClosing(e);
        }

        public void FocusInput()
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.SelectAll();
        }

        private void FadeIn()
        {
            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Return)
            {
                // Enter accepts the first suggestion when the word was not found.
                if (_result != null && !_result.Found && _suggestions.Count > 0)
                    QueryBox.Text = _suggestions[0];
                e.Handled = true;
            }
        }

        private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop();
            _debounce.Start();
        }

        private void RunQuery()
        {
            if (!_loaded) return;

            ResultPanel.Children.Clear();
            _suggestions.Clear();

            string query = (QueryBox.Text ?? "").Trim();
            if (query.Length == 0)
            {
                _result = null;
                ShowRecent();
                StatusText.Text = "";
                return;
            }
            if (query.Length > 80)
            {
                _result = null;
                AddText("输入太长，请输入一个单词或短语。", 14, "#B91C1C", 0, 16, true);
                StatusText.Text = "";
                return;
            }

            LookupResult result = _lookup.Lookup(query, "search");
            _result = result;
            if (result.Found)
            {
                RenderEntry(result);
                StatusText.Text = (result.Primary.Dict != null ? result.Primary.Dict.Name : "") +
                                  "  ·  " + result.ElapsedMs + " ms";
            }
            else
            {
                RenderNotFound(result);
                StatusText.Text = "本地词典未收录  ·  " + result.ElapsedMs + " ms";
            }
        }

        private void ShowRecent()
        {
            AddText("输入一个英文或中文单词开始查词。", 14, "#64748B", 0, 6, false);

            var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
            foreach (string t in new[] { "telescope", "perceive", "国际化", "extraordinary" })
            {
                Button chip = ChipButton(t);
                chip.Click += (s, e) => QueryBox.Text = t;
                chips.Children.Add(chip);
            }
            ResultPanel.Children.Add(chips);

            try
            {
                List<HistoryItem> recent = _history.ListHistory(10);
                if (recent.Count <= 0) return;
                AddText("最近查词", 11, "#94A3B8", 10, 12, true);
                var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                foreach (HistoryItem item in recent)
                {
                    if (string.IsNullOrWhiteSpace(item.Display)) continue;
                    string word = item.Display;
                    Button chip = ChipButton(word);
                    chip.Click += (s, e) => QueryBox.Text = word;
                    wrap.Children.Add(chip);
                }
                ResultPanel.Children.Add(wrap);
            }
            catch { }
        }

        private void RenderEntry(LookupResult result)
        {
            LookupEntry entry = result.Primary;
            double scale = Math.Max(0.75, Math.Min(1.6, _settings.FontScale));
            bool chinese = entry.IsChinese;

            Grid head = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = entry.Row.Headword,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
                FontSize = 30 * scale,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#0F172A"),
                TextWrapping = TextWrapping.Wrap
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Button speak = new Button { Style = (Style)FindResource("IconBtn"), ToolTip = "朗读发音" };
            speak.Content = Glyph("\ue767", 17, "#475569");
            speak.Click += (s, e) => _tts.Speak(entry.Row.Headword, chinese);
            actions.Children.Add(speak);

            Button copy = new Button { Style = (Style)FindResource("IconBtn"), ToolTip = "复制单词" };
            copy.Content = Glyph("\ue8c8", 15, "#64748B");
            copy.Click += (s, e) =>
            {
                try { ClipboardCompat.TrySetText(entry.Row.Headword); } catch { }
            };
            actions.Children.Add(copy);

            Grid.SetColumn(actions, 1);
            head.Children.Add(actions);
            ResultPanel.Children.Add(head);

            string phonetic = entry.Row.Phonetic;
            if (string.IsNullOrWhiteSpace(phonetic) && !string.IsNullOrWhiteSpace(entry.Pinyin))
                phonetic = entry.Pinyin;
            string pretty = PhoneticUtil.Pretty(phonetic, chinese);
            if (!string.IsNullOrEmpty(pretty))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                line.Children.Add(new TextBlock
                {
                    Text = chinese ? pretty : "/" + pretty + "/",
                    FontSize = 14.5 * scale,
                    Foreground = Brush("#64748B"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                line.Children.Add(Badge(!chinese ? "EN" : (string.IsNullOrWhiteSpace(entry.Pinyin) ? "中文" : "拼音"),
                    "#EEF2FF", "#4F46E5"));
                if (entry.FromInflection && !string.IsNullOrWhiteSpace(entry.MatchedForm))
                    line.Children.Add(Badge("原形", "#FFF7ED", "#C2410C"));
                ResultPanel.Children.Add(line);
            }

            var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (_settings.ShowTags && !chinese)
            {
                int shown = 0;
                foreach (string raw in (entry.Row.Tags ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string label = TagLabel(raw);
                    if (label == null) continue;
                    tags.Children.Add(TagChip(label));
                    shown++;
                    if (shown >= 6) break;
                }
                if (entry.Row.Oxford > 0 && shown++ < 6) tags.Children.Add(TagChip("牛津3000"));
                if (entry.Row.Collins > 0 && shown < 6)
                    tags.Children.Add(TagChip("柯林斯 " + new string('★', Math.Min(5, entry.Row.Collins))));
            }
            if (tags.Children.Count > 0) ResultPanel.Children.Add(tags);

            List<string> translations = entry.TranslationLines;
            List<string> definitions = entry.DefinitionLines;

            if (chinese)
            {
                if (translations.Count == 0 && definitions.Count > 0) translations = definitions;
                foreach (string line in translations) ResultPanel.Children.Add(SenseLine(line, scale));
                if (!string.IsNullOrWhiteSpace(entry.Trad) &&
                    !string.Equals(entry.Trad, entry.Row.Headword, StringComparison.Ordinal))
                    ResultPanel.Children.Add(MetaLine("繁体  " + entry.Trad, scale));
                if (!string.IsNullOrWhiteSpace(entry.Simp) &&
                    !string.Equals(entry.Simp, entry.Row.Headword, StringComparison.Ordinal))
                    ResultPanel.Children.Add(MetaLine("简体  " + entry.Simp, scale));
                if (!string.IsNullOrWhiteSpace(entry.ClassifierText))
                    ResultPanel.Children.Add(MetaLine(entry.ClassifierText, scale));
            }
            else
            {
                if (translations.Count == 0 && definitions.Count == 0)
                    ResultPanel.Children.Add(MetaLine("（该词条没有释义内容）", scale));
                foreach (string line in translations) ResultPanel.Children.Add(SenseLine(line, scale));
                if (_settings.ShowEnglishDefinition && definitions.Count > 0 && translations.Count > 0)
                    ResultPanel.Children.Add(SectionLabel("英文释义", scale));
                if (_settings.ShowEnglishDefinition || translations.Count == 0)
                {
                    foreach (string line in definitions) ResultPanel.Children.Add(DefinitionLine(line, scale));
                }
            }

            if (result.Entries.Count > 1)
                ResultPanel.Children.Add(MetaLine("另外 " + (result.Entries.Count - 1) + " 本词典也有收录", scale));
        }

        private void RenderNotFound(LookupResult result)
        {
            double scale = Math.Max(0.75, Math.Min(1.6, _settings.FontScale));
            string query = (QueryBox.Text ?? "").Trim();
            AddText("未找到「" + TextUtil.SafeShort(query, 30) + "」", 22 * scale, "#0F172A", 0, 2, true);
            AddText("本地词典暂未收录这个词条。", 13.5 * scale, "#64748B", 0, 8, false);

            _suggestions.Clear();
            if (result == null || result.Suggestions == null) return;
            foreach (string suggestion in result.Suggestions) _suggestions.Add(suggestion);
            if (_suggestions.Count <= 0) return;

            AddText("也许你想查", 11 * scale, "#94A3B8", 8, 10, true);
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (string suggestion in _suggestions)
            {
                string word = suggestion;
                Button chip = ChipButton(word);
                chip.Click += (s, e) => QueryBox.Text = word;
                wrap.Children.Add(chip);
            }
            ResultPanel.Children.Add(wrap);
            AddText("点击候选词继续查询，或按 Enter 选择第一个。", 11 * scale, "#CBD5E1", 2, 4, false);
        }

        private Button ChipButton(string text)
        {
            return new Button { Content = text, Style = (Style)FindResource("ChipBtn") };
        }

        private static TextBlock Glyph(string glyph, double size, string color)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = size,
                Foreground = Brush(color)
            };
        }

        private Border Badge(string text, string bg, string fg)
        {
            return new Border
            {
                Background = Brush(bg),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(10, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(fg)
                }
            };
        }

        private Border TagChip(string text)
        {
            return new Border
            {
                Background = Brush("#F1F5F9"),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(0, 0, 6, 4),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#64748B")
                }
            };
        }

        private void AddText(string text, double size, string color, double topMargin, double bottomMargin, bool bold)
        {
            ResultPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(color),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, topMargin, 0, bottomMargin)
            });
        }

        private TextBlock SenseLine(string line, double scale)
        {
            TextBlock tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14.5 * scale,
                LineHeight = 23 * scale,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Brush("#1E293B"),
                Margin = new Thickness(0, 0, 0, 3 * scale),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI")
            };
            Match m = Regex.Match(line, "^([A-Za-z]{1,6}\\.)\\s*(.+)$");
            if (m.Success)
            {
                tb.Inlines.Add(new Run(m.Groups[1].Value)
                {
                    FontSize = 12.5 * scale,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#4F46E5")
                });
                tb.Inlines.Add(new Run("  " + m.Groups[2].Value));
            }
            else tb.Text = line;
            return tb;
        }

        private TextBlock DefinitionLine(string line, double scale)
        {
            return new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.8 * scale,
                LineHeight = 19 * scale,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Brush("#64748B"),
                Margin = new Thickness(0, 0, 0, 2 * scale)
            };
        }

        private TextBlock SectionLabel(string text, double scale)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10.5 * scale,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#94A3B8"),
                Margin = new Thickness(0, 10 * scale, 0, 5 * scale)
            };
        }

        private TextBlock MetaLine(string text, double scale)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5 * scale,
                Foreground = Brush("#94A3B8"),
                Margin = new Thickness(0, 8 * scale, 0, 0)
            };
        }

        private static string TagLabel(string tag)
        {
            switch ((tag ?? "").Trim().ToLowerInvariant())
            {
                case "zk": return "中考";
                case "gk": return "高考";
                case "cet4": return "CET-4";
                case "cet6": return "CET-6";
                case "ky": return "考研";
                case "toefl": return "TOEFL";
                case "ielts": return "IELTS";
                case "gre": return "GRE";
                case "tem4": return "专四";
                case "tem8": return "专八";
                default: return null;
            }
        }

        private static SolidColorBrush Brush(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        }

        private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
