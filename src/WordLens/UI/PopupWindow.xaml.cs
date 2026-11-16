using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WordLens.Core;

namespace WordLens.UI
{
    public partial class PopupWindow : Window
    {
        private LookupResult _result;
        private AppSettings _settings;
        private bool _favorite;
        private DispatcherTimer _copyTimer;
        private bool _shownAtLeastOnce;
        private int _lastX;
        private int _lastY;

        public event EventHandler SpeakRequested;
        public event EventHandler FavoriteRequested;
        public event EventHandler CopyRequested;
        public event EventHandler SearchRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler CloseRequested;
        public event EventHandler<string> SuggestionRequested;

        public PopupWindow()
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
            SpeakerButton.Click += (s, e) => SpeakRequested?.Invoke(this, EventArgs.Empty);
            StarButton.Click += (s, e) => FavoriteRequested?.Invoke(this, EventArgs.Empty);
            CopyButton.Click += (s, e) => CopyRequested?.Invoke(this, EventArgs.Empty);
            SearchButton.Click += (s, e) => SearchRequested?.Invoke(this, EventArgs.Empty);
            SettingsButton.Click += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            CloseButton.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _copyTimer.Tick += (s, e) => { _copyTimer.Stop(); CopyGlyph.Text = "\uE8C8"; };
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            }
            catch { }
        }

        public void Render(LookupResult result, bool favorite, AppSettings settings)
        {
            _result = result;
            _settings = settings;
            _favorite = favorite;
            double scale = Math.Max(0.75, Math.Min(1.6, settings.FontScale));
            Width = settings.PopupWidth;
            MaxHeight = settings.PopupMaxHeight;
            BodyScroll.MaxHeight = Math.Max(120, settings.PopupMaxHeight - 260);
            WordText.FontSize = 30 * scale;
            WordText.LineHeight = 36 * scale;
            PhoneticText.FontSize = 14.5 * scale;
            FooterDictText.FontSize = 11 * scale;

            BodyPanel.Children.Clear();
            TagsPanel.Children.Clear();
            InflectionBadge.Visibility = Visibility.Collapsed;
            LangBadge.Visibility = Visibility.Collapsed;

            if (result != null && result.Found)
                RenderFound(result, scale);
            else
                RenderNotFound(result, scale);

            UpdateFavoriteVisual();
            SizeToContent = SizeToContent.Height;
            if (IsVisible) UpdateLayout();
        }

        private void RenderFound(LookupResult result, double scale)
        {
            LookupEntry primary = result.Primary;
            WordText.Text = primary.Row.Headword;
            bool chinese = primary.IsChinese;
            string phonetic = primary.Row.Phonetic;
            if (string.IsNullOrWhiteSpace(phonetic) && !string.IsNullOrWhiteSpace(primary.Pinyin))
                phonetic = primary.Pinyin;
            string pretty = PhoneticUtil.Pretty(phonetic, chinese);
            if (!string.IsNullOrEmpty(pretty))
            {
                PhoneticText.Text = chinese ? pretty : "/" + pretty + "/";
                PhoneticText.Visibility = Visibility.Visible;
            }
            else
            {
                PhoneticText.Text = "";
                PhoneticText.Visibility = Visibility.Collapsed;
            }

            if (chinese)
            {
                LangBadge.Visibility = Visibility.Visible;
                LangText.Text = string.IsNullOrWhiteSpace(primary.Pinyin) ? "中文" : "拼音";
            }
            else
            {
                LangBadge.Visibility = Visibility.Visible;
                LangText.Text = "EN";
            }

            if (primary.FromInflection && !string.IsNullOrWhiteSpace(primary.MatchedForm))
            {
                InflectionBadge.Visibility = Visibility.Visible;
                InflectionText.Text = "原形";
            }

            BuildTags(primary);
            BuildFoundBody(result, scale);

            string footer = primary.Dict != null ? primary.Dict.Name : "";
            if (result.Entries.Count > 1) footer += "  ·  共 " + result.Entries.Count + " 本词典";
            footer += "  ·  " + result.ElapsedMs + " ms";
            FooterDictText.Text = footer;
        }

        private void BuildFoundBody(LookupResult result, double scale)
        {
            for (int i = 0; i < result.Entries.Count; i++)
            {
                LookupEntry entry = result.Entries[i];
                if (i > 0)
                {
                    BodyPanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Brush("#EFF2F6"),
                        Margin = new Thickness(0, 10 * scale, 0, 12 * scale)
                    });
                }
                if (result.Entries.Count > 1)
                {
                    BodyPanel.Children.Add(new TextBlock
                    {
                        Text = entry.Dict != null ? entry.Dict.Name : "",
                        FontSize = 11 * scale,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brush("#94A3B8"),
                        Margin = new Thickness(0, 0, 0, 6 * scale)
                    });
                }

                List<string> translations = entry.TranslationLines;
                List<string> definitions = entry.DefinitionLines;

                if (entry.IsChinese)
                {
                    // Chinese headword: pinyin is in the header, body shows English meanings.
                    if (translations.Count == 0 && definitions.Count > 0)
                        translations = definitions;
                    foreach (string line in translations)
                        BodyPanel.Children.Add(MakeSenseLine(line, scale, false));
                    if (!string.IsNullOrWhiteSpace(entry.Trad) &&
                        !string.Equals(entry.Trad, entry.Row.Headword, StringComparison.Ordinal))
                        BodyPanel.Children.Add(MakeMetaLine("繁体  " + entry.Trad, scale));
                    if (!string.IsNullOrWhiteSpace(entry.Simp) &&
                        !string.Equals(entry.Simp, entry.Row.Headword, StringComparison.Ordinal))
                        BodyPanel.Children.Add(MakeMetaLine("简体  " + entry.Simp, scale));
                    if (!string.IsNullOrWhiteSpace(entry.ClassifierText))
                        BodyPanel.Children.Add(MakeMetaLine(entry.ClassifierText, scale));
                }
                else
                {
                    if (translations.Count == 0 && definitions.Count == 0)
                        BodyPanel.Children.Add(MakeMetaLine("（该词条没有释义内容）", scale));
                    foreach (string line in translations)
                        BodyPanel.Children.Add(MakeSenseLine(line, scale, false));
                    if (_settings.ShowEnglishDefinition && definitions.Count > 0 && translations.Count > 0)
                    {
                        BodyPanel.Children.Add(MakeSectionLabel("英文释义", scale));
                    }
                    if (_settings.ShowEnglishDefinition || translations.Count == 0)
                    {
                        foreach (string line in definitions)
                            BodyPanel.Children.Add(MakeDefinitionLine(line, scale));
                    }
                }
            }

            BodyPanel.Children.Add(MakeMetaLine("双击任意单词即可再次查词 · Esc 关闭", scale, true));
        }

        private void RenderNotFound(LookupResult result, double scale)
        {
            string query = result != null ? result.DisplayQuery : "";
            WordText.Text = "未找到「" + TextUtil.SafeShort(query, 28) + "」";
            WordText.FontSize = 24 * scale;
            WordText.LineHeight = 30 * scale;
            PhoneticText.Text = "";

            BodyPanel.Children.Add(new TextBlock
            {
                Text = "本地词典暂未收录这个词条。",
                FontSize = 14 * scale,
                Foreground = Brush("#475569"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8 * scale)
            });

            if (result != null && result.Suggestions != null && result.Suggestions.Count > 0)
            {
                BodyPanel.Children.Add(MakeSectionLabel("也许你想查", scale));
                var wrap = new WrapPanel { Margin = new Thickness(0, 2 * scale, 0, 6 * scale) };
                foreach (string s in result.Suggestions)
                {
                    var btn = new Button { Content = s, Style = (Style)FindResource("ChipBtn") };
                    btn.Click += (o, e) => SuggestionRequested?.Invoke(this, s);
                    wrap.Children.Add(btn);
                }
                BodyPanel.Children.Add(wrap);
            }
            BodyPanel.Children.Add(MakeMetaLine("可以点击右下角的搜索按钮，在浏览器中查询这个词。", scale, true));
            FooterDictText.Text = "ECDICT · CC-CEDICT · " + (result != null ? result.ElapsedMs : 0) + " ms";
        }

        private void BuildTags(LookupEntry entry)
        {
            if (!_settings.ShowTags) return;
            var tags = new List<string>();
            if (entry.Dict != null && entry.Dict.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(entry.Pinyin))
                {
                    // pinyin already shown, nothing else
                }
            }
            else
            {
                string raw = entry.Row.Tags ?? "";
                foreach (string t in raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string label = TagLabel(t);
                    if (label != null) tags.Add(label);
                }
                if (entry.Row.Oxford > 0) tags.Add("牛津3000");
                if (entry.Row.Collins > 0) tags.Add("柯林斯 " + new string('\u2605', Math.Min(5, entry.Row.Collins)));
            }
            int count = 0;
            foreach (string t in tags)
            {
                if (count++ >= 6) break;
                TagsPanel.Children.Add(MakeChip(t));
            }
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

        private Border MakeChip(string text)
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

        private TextBlock MakeSenseLine(string line, double scale, bool chinesePhrase)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 15 * scale,
                LineHeight = 24 * scale,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Brush("#1E293B"),
                Margin = new Thickness(0, 0, 0, 3 * scale),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI")
            };
            Match m = Regex.Match(line, @"^([A-Za-z]{1,6}\.)\s*(.+)$");
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
            else
            {
                tb.Text = line;
            }
            return tb;
        }

        private TextBlock MakeDefinitionLine(string line, double scale)
        {
            return new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13 * scale,
                LineHeight = 20 * scale,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Brush("#64748B"),
                Margin = new Thickness(0, 0, 0, 2 * scale)
            };
        }

        private TextBlock MakeSectionLabel(string text, double scale)
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

        private TextBlock MakeMetaLine(string text, double scale, bool hint = false)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = (hint ? 11 : 12) * scale,
                Foreground = Brush(hint ? "#CBD5E1" : "#94A3B8"),
                Margin = new Thickness(0, 8 * scale, 0, 0)
            };
        }

        private static SolidColorBrush Brush(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        }

        private void UpdateFavoriteVisual()
        {
            if (_favorite)
            {
                StarGlyph.Text = "\uE735";
                StarGlyph.Foreground = Brush("#F59E0B");
            }
            else
            {
                StarGlyph.Text = "\uE734";
                StarGlyph.Foreground = Brush("#94A3B8");
            }
        }

        public void SetFavorite(bool favorite)
        {
            _favorite = favorite;
            UpdateFavoriteVisual();
        }

        public void ShowCopyFeedback()
        {
            CopyGlyph.Text = "\uE73E";
            _copyTimer.Stop();
            _copyTimer.Start();
        }

        /// <summary>Shows the card near a physical screen point, flipping at screen edges.</summary>
        public void ShowNearPhysical(int px, int py)
        {
            _lastX = px;
            _lastY = py;
            if (!_shownAtLeastOnce)
            {
                Opacity = 0;
                Show();
                _shownAtLeastOnce = true;
            }
            else if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }
            UpdateLayout();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Native.GetWindowRect(hwnd, out Native.RECT wr);
            int w = wr.Right - wr.Left;
            int h = wr.Bottom - wr.Top;
            if (w <= 0) w = (int)Width;
            if (h <= 0) h = 300;
            Native.RECT wa = Native.GetWorkArea(px, py, out uint dpiX, out uint dpiY);
            double scale = dpiX / 96.0;
            int gapX = (int)(14 * scale);
            int gapY = (int)(18 * scale);
            int left = px + gapX;
            int top = py + gapY;
            int spaceBelow = wa.Bottom - 4 - (py + gapY);
            int spaceAbove = (py - gapY) - (wa.Top + 4);
            if (w > wa.Right - 4 - (px + gapX)) left = px - w - gapX;
            if (h <= spaceBelow) top = py + gapY;
            else if (h <= spaceAbove) top = py - h - gapY;
            else top = Math.Max(wa.Top + 4, Math.Min(py + gapY, wa.Bottom - 4 - h));
            if (left < wa.Left + 4) left = wa.Left + 4;
            if (left + w > wa.Right - 4) left = wa.Right - 4 - w;
            if (top < wa.Top + 4) top = wa.Top + 4;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            FadeIn();
        }

        public void Reposition()
        {
            if (IsVisible) ShowNearPhysical(_lastX, _lastY);
        }

        private void FadeIn()
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fade);
            var trans = Card.RenderTransform as TranslateTransform;
            if (trans == null)
            {
                trans = new TranslateTransform();
                Card.RenderTransform = trans;
            }
            trans.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        public void HideAnimated()
        {
            if (!IsVisible) return;
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(90));
            fade.Completed += (s, e) =>
            {
                Hide();
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
            };
            BeginAnimation(OpacityProperty, fade);
        }

        public bool IsShown => IsVisible;

        public LookupResult CurrentResult => _result;

        public Point Position => new Point(_lastX, _lastY);
    }
}
