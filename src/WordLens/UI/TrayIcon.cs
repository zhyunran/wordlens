using System;
using System.Drawing;
using System.Windows.Forms;
using WordLens.Core;

namespace WordLens.UI
{
    /// <summary>Tray icon and context menu (WinForms NotifyIcon hosted in the WPF message loop).</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _autostartItem;
        private bool _disposed;

        public event Action ToggleEnabledRequested;
        public event Action SettingsRequested;
        public event Action SearchRequested;
        public event Action WordbookRequested;
        public event Action AutostartToggleRequested;
        public event Action ExitRequested;

        public TrayIcon(AppSettings settings)
        {
            Icon icon;
            try { icon = Icon.ExtractAssociatedIcon(AppPaths.ExePath) ?? SystemIcons.Application; }
            catch { icon = SystemIcons.Application; }

            var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9f) };
            _toggleItem = new ToolStripMenuItem("启用双击查词") { Checked = settings.Enabled, CheckOnClick = false };
            _toggleItem.Click += (s, e) => ToggleEnabledRequested?.Invoke();

            var settingsItem = new ToolStripMenuItem("查词设置…");
            settingsItem.Click += (s, e) => SettingsRequested?.Invoke();

            var searchItem = new ToolStripMenuItem("输入查词…");
            searchItem.Click += (s, e) => SearchRequested?.Invoke();

            var wordbookItem = new ToolStripMenuItem("生词本…");
            wordbookItem.Click += (s, e) => WordbookRequested?.Invoke();

            _autostartItem = new ToolStripMenuItem("开机自动启动") { Checked = settings.Autostart, CheckOnClick = false };
            _autostartItem.Click += (s, e) => AutostartToggleRequested?.Invoke();

            var aboutItem = new ToolStripMenuItem("关于 WordLens…");
            aboutItem.Click += (s, e) => SettingsRequested?.Invoke();

            var exitItem = new ToolStripMenuItem("退出 WordLens");
            exitItem.Click += (s, e) => ExitRequested?.Invoke();

            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(searchItem);
            menu.Items.Add(wordbookItem);
            menu.Items.Add(_autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(aboutItem);
            menu.Items.Add(exitItem);

            _icon = new NotifyIcon
            {
                Icon = icon,
                Text = "WordLens 双击查词",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (s, e) => SettingsRequested?.Invoke();
        }

        public void SetEnabled(bool enabled)
        {
            try
            {
                _toggleItem.Checked = enabled;
                _icon.Text = enabled ? "WordLens 双击查词（已启用）" : "WordLens 双击查词（已暂停）";
            }
            catch { }
        }

        public void SetAutostart(bool enabled)
        {
            try { _autostartItem.Checked = enabled; } catch { }
        }

        public void ShowBalloon(string title, string text)
        {
            try { _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch { }
        }
    }
}
