using System.Runtime.InteropServices;
using HotspotMeter.App.Localization;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace HotspotMeter.App.Services;

/// <summary>
/// 系统托盘入口。使用 WinForms 的 NotifyIcon：它是 Windows 上最稳定、
/// 无额外依赖的托盘实现，避免为一个小功能引入第三方 UI 框架。
/// 菜单使用自绘 Renderer 融入深色主题。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _hideItem;
    private readonly Forms.ToolStripMenuItem _settingsItem;
    private readonly Forms.ToolStripMenuItem _statsItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Func<bool> _isCapsuleVisible;
    private readonly TrayMessageWindow _messageWindow;
    private bool _disposed;

    public TrayIconService(
        Action openStats,
        Action openSettings,
        Action showCapsule,
        Action hideCapsule,
        Action exit,
        Func<bool> isCapsuleVisible)
    {
        _isCapsuleVisible = isCapsuleVisible;

        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = DarkMenuRenderer.MenuBackground,
            ForeColor = DarkMenuRenderer.MenuText,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Forms.Padding(4),
            Font = Drawing.SystemFonts.MenuFont,
        };

        _statsItem = CreateItem(LocalizationService.T("Tray_Stats"), openStats);
        _settingsItem = CreateItem(LocalizationService.T("Tray_Settings"), openSettings);
        _showItem = CreateItem(LocalizationService.T("Tray_Show"), showCapsule);
        _hideItem = CreateItem(LocalizationService.T("Tray_Hide"), hideCapsule);
        _exitItem = CreateItem(LocalizationService.T("Tray_Exit"), exit);

        _menu.Items.Add(_statsItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_hideItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        LocalizationService.Current.LanguageChanged += ApplyLanguage;

        // 打开菜单时再决定可用性，保证与胶囊真实可见状态一致。
        _menu.Opening += (_, _) =>
        {
            var visible = _isCapsuleVisible();
            _showItem.Enabled = !visible;
            _hideItem.Enabled = visible;
        };

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = AppIcons.TrayIcon,
            Text = "HotspotMeter",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        // Explorer 重启会重建任务栏，WinForms 的 NotifyIcon 不会自动回来。
        // 监听 TaskbarCreated 后重新挂载，否则胶囊被隐藏时用户会失去唯一入口。
        _messageWindow = new TrayMessageWindow(() =>
        {
            if (_disposed)
            {
                return;
            }

            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
        });

        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                openSettings();
            }
        };
    }

    private Forms.ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            ForeColor = DarkMenuRenderer.MenuText,
            Padding = new Forms.Padding(8, 3, 8, 3),
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>语言切换后更新已存在的菜单项；不清空菜单，避免丢失点击处理与顺序。</summary>
    private void ApplyLanguage()
    {
        _statsItem.Text = LocalizationService.T("Tray_Stats");
        _settingsItem.Text = LocalizationService.T("Tray_Settings");
        _showItem.Text = LocalizationService.T("Tray_Show");
        _hideItem.Text = LocalizationService.T("Tray_Hide");
        _exitItem.Text = LocalizationService.T("Tray_Exit");
        _menu.Refresh();
    }

    /// <summary>供开发期自检读取托盘状态与菜单结构。</summary>
    public bool IsVisible => _notifyIcon.Visible;

    public IReadOnlyList<string> MenuItemTexts =>
        _menu.Items.OfType<Forms.ToolStripMenuItem>().Select(i => i.Text ?? string.Empty).ToArray();

    /// <summary>开发期自检用：走真实的托盘"设置"菜单点击路径。</summary>
    internal void ClickSettingsForTest() => _settingsItem.PerformClick();

    /// <summary>开发期自检用：走真实的托盘"流量统计"菜单点击路径。</summary>
    internal void ClickStatsForTest() => _statsItem.PerformClick();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationService.Current.LanguageChanged -= ApplyLanguage;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _messageWindow.Dispose();
        _menu.Dispose();
    }

    /// <summary>深色托盘菜单：只做必要的颜色覆盖，保留系统对键盘/高 DPI 的处理。</summary>
    private sealed class DarkMenuRenderer : Forms.ToolStripRenderer
    {
        public static readonly Drawing.Color MenuBackground = Drawing.Color.FromArgb(0x1C, 0x1C, 0x20);
        public static readonly Drawing.Color MenuText = Drawing.Color.FromArgb(0xF5, 0xF5, 0xF7);
        public static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2C, 0x2C, 0x31);
        public static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x3A, 0x3A, 0x3F);
        private static readonly Drawing.Color DisabledText = Drawing.Color.FromArgb(0x6E, 0x6E, 0x73);

        // 系统字体由 SystemFonts 缓存，不随菜单释放；自建 Font 会泄漏 GDI 句柄。
        private static readonly Drawing.Font MenuFont = Drawing.SystemFonts.MenuFont!;

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(MenuBackground);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Border);
            var bounds = new Drawing.Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, bounds);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }

            using var brush = new Drawing.SolidBrush(Hover);
            e.Graphics.FillRectangle(brush, new Drawing.Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2));
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? MenuText : DisabledText;
            e.TextFont = MenuFont;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            // 不显示图标列，避免左侧出现一条空白。
        }
    }

    /// <summary>
    /// 只收 TaskbarCreated 的消息窗口。隐藏窗口不参与 UI、不抢焦点；
    /// 用 HWND_MESSAGE 父窗口可保证它不会出现在 Alt+Tab 或任务栏。
    /// </summary>
    private sealed class TrayMessageWindow : Forms.NativeWindow, IDisposable
    {
        private const int HwndMessage = -3;

        private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private readonly Action _onTaskbarCreated;
        private bool _disposed;

        public TrayMessageWindow(Action onTaskbarCreated)
        {
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new Forms.CreateParams
            {
                Caption = "HotspotMeter.TrayMessageWindow",
                Parent = new IntPtr(HwndMessage),
            });
        }

        protected override void WndProc(ref Forms.Message m)
        {
            if (_taskbarCreated != 0 && m.Msg == _taskbarCreated)
            {
                _onTaskbarCreated();
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DestroyHandle();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);
    }
}