using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using HotspotMeter.App.Models;
using HotspotMeter.Core.Formatting;
using HotspotMeter.Core.Layout;
using HotspotMeter.Core.Models;

namespace HotspotMeter.App.ViewModels;

/// <summary>
/// 悬浮状态栏的唯一视图模型。
/// <para>
/// 数据来源严格限定为 <see cref="MonitorSnapshot"/> 与引擎的会话结束事件：
/// 这里不推算、不补零、不构造任何模拟流量。没有有效会话时显示 "--"，
/// 只有确实产生过会话（包括 0 字节的真实会话）才显示数字。
/// </para>
/// <para>
/// 4C-8 起胶囊只保留两类信息：左侧的本次会话流量、右侧的上下行速度。
/// Wi-Fi 图标、连接状态文字和"已使用流量"说明都已移除；连接状态仍通过
/// 绿色/红色底色表达，不占用布局空间。
/// </para>
/// </summary>
public sealed class CapsuleViewModel : INotifyPropertyChanged
{
    private static readonly Brush ConnectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E)));
    private static readonly Brush DisconnectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC5, 0x22, 0x1F)));

    private string _sessionText = "--";
    private string _downloadText = "0 B/s";
    private string _uploadText = "0 B/s";
    private string _warning = string.Empty;
    private bool _isConnected;
    private bool _hasSession;
    private bool _isPinned;
    private bool _showSpeedLabels = true;
    private bool _showCaption = true;
    private CapsuleSize _size = CapsuleSize.Default;
    private CapsuleDensity _density = CapsuleDensity.Normal;

    // 最近一次已结束会话：断开后仍要显示"上次会话"的累计值，
    // 而断开后的快照本身不再携带该会话的累计字节。
    private long? _lastClosedDownload;
    private long? _lastClosedUpload;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SessionText
    {
        get => _sessionText;
        private set => Set(ref _sessionText, value);
    }

    public string DownloadText
    {
        get => _downloadText;
        private set => Set(ref _downloadText, value);
    }

    public string UploadText
    {
        get => _uploadText;
        private set => Set(ref _uploadText, value);
    }

    /// <summary>采集层给出的可读警告（如接口暂时缺失），仅在提示气泡里展示，不新增胶囊元素。</summary>
    public string Warning
    {
        get => _warning;
        private set
        {
            if (Set(ref _warning, value))
            {
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    /// <summary>空警告返回 null，避免 WPF 弹出空提示框。</summary>
    public string? TooltipText => string.IsNullOrWhiteSpace(Warning) ? null : Warning;

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    public bool HasSession
    {
        get => _hasSession;
        private set => Set(ref _hasSession, value);
    }

    public Brush Background => IsConnected ? ConnectedBrush : DisconnectedBrush;

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (Set(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinMenuText));
            }
        }
    }

    public string PinMenuText => IsPinned ? "解除固定位置" : "固定位置";

    // ------- 尺寸与派生布局 -------
    // 所有数值都以 DIP 计算；XAML 只绑定这些派生值，窗口本体不做像素换算。
    public CapsuleSize Size => _size;

    public double CapsuleWidth => _size.Width;

    public double CapsuleHeight => _size.Height;

    public CapsuleDensity Density => _density;

    public CornerRadius CapsuleCornerRadius { get; private set; }

    public Thickness CapsuleContentMargin { get; private set; }

    public double TrafficFontSize { get; private set; }

    public double TrafficLineHeight { get; private set; }

    /// <summary>流量数字上方的说明标签字号。</summary>
    public double CaptionFontSize { get; private set; }

    /// <summary>说明标签与数字之间的垂直间距（标签下边距）。</summary>
    public Thickness CaptionMargin { get; private set; }

    public double SpeedFontSize { get; private set; }

    /// <summary>速度区域与左侧流量数字之间的间距。</summary>
    public Thickness SpeedBlockMargin { get; private set; }

    /// <summary>方向箭头与"下载/上传"文字之间的间距。</summary>
    public Thickness SpeedArrowMargin { get; private set; }

    /// <summary>"下载/上传"文字与速度数值之间的间距。</summary>
    public Thickness SpeedLabelMargin { get; private set; }

    /// <summary>下载行与上传行之间的垂直间距。</summary>
    public Thickness SpeedRowSpacing { get; private set; }

    /// <summary>任务栏极矮时只保留箭头和数值，避免"下载/上传"文字挤爆布局。</summary>
    public bool ShowSpeedLabels
    {
        get => _showSpeedLabels;
        private set => Set(ref _showSpeedLabels, value);
    }

    /// <summary>
    /// 是否显示流量数字上方的"已使用流量"。
    /// 自动适配的 Compact/Minimal 密度高度不足以再放一行标签，隐藏它是为了避免重叠/裁切；
    /// 普通四档与自定义尺寸始终显示。
    /// </summary>
    public bool ShowCaption
    {
        get => _showCaption;
        private set => Set(ref _showCaption, value);
    }

    public CapsuleViewModel() => ApplyNormalMetrics();

    /// <summary>
    /// 应用普通四档/自定义尺寸。窗口宽高、圆角、字号、内边距都由同一个缩放系数派生，
    /// 避免"窗口变大但文字没变"或某一方向被裁切。
    /// </summary>
    public void SetSize(CapsuleSize size)
    {
        if (_density == CapsuleDensity.Normal && size.IsNearly(_size))
        {
            return;
        }

        _size = size;
        _density = CapsuleDensity.Normal;
        ShowSpeedLabels = true;
        ShowCaption = true;
        ApplyNormalMetrics();

        // 空属性名表示"所有属性都可能变化"，一次性刷新尺寸与字号绑定。
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// 应用任务栏自动适配结果。这里使用独立于 320×104 比例的密度字号，
    /// 因为 48 DIP 高的任务栏无法容纳按 0.42 倍缩小的辅助文字。
    /// </summary>
    public void ApplyAutoFit(TaskbarCapsuleFit fit)
    {
        _size = new CapsuleSize(fit.Width, fit.Height);
        _density = fit.Density;
        ShowSpeedLabels = fit.ShowSpeedLabels;
        // 44/56 DIP 的任务栏胶囊放不下"标签 + 数字 + 两行速度"，只保留核心数字与速度。
        ShowCaption = fit.Density == CapsuleDensity.Normal;
        ApplyDensityMetrics(fit.Density);
        OnPropertyChanged(string.Empty);
    }

    private void ApplyNormalMetrics()
    {
        var s = _size.Scale;
        CapsuleCornerRadius = new CornerRadius(20 * s);
        CapsuleContentMargin = new Thickness(18 * s, 14 * s, 18 * s, 14 * s);
        TrafficFontSize = 46 * s;
        TrafficLineHeight = 54 * s;
        CaptionFontSize = 11 * s;
        CaptionMargin = new Thickness(0, 0, 0, 2 * s);
        SpeedFontSize = 14 * s;
        SpeedBlockMargin = new Thickness(14 * s, 0, 0, 0);
        SpeedArrowMargin = new Thickness(0, 0, 3 * s, 0);
        SpeedLabelMargin = new Thickness(0, 0, 5 * s, 0);
        SpeedRowSpacing = new Thickness(0, 4 * s, 0, 0);
    }

    private void ApplyDensityMetrics(CapsuleDensity density)
    {
        switch (density)
        {
            case CapsuleDensity.Normal:
                var s = Math.Clamp(_size.Height / CapsuleSize.DefaultHeight, 0.8, 1.0);
                CapsuleCornerRadius = new CornerRadius(20 * s);
                CapsuleContentMargin = new Thickness(18 * s, 14 * s, 18 * s, 14 * s);
                TrafficFontSize = 46 * s;
                TrafficLineHeight = 54 * s;
                CaptionFontSize = 11 * s;
                CaptionMargin = new Thickness(0, 0, 0, 2 * s);
                SpeedFontSize = 14 * s;
                SpeedBlockMargin = new Thickness(14 * s, 0, 0, 0);
                SpeedArrowMargin = new Thickness(0, 0, 3 * s, 0);
                SpeedLabelMargin = new Thickness(0, 0, 5 * s, 0);
                SpeedRowSpacing = new Thickness(0, 4 * s, 0, 0);
                break;

            case CapsuleDensity.Compact:
                CapsuleCornerRadius = new CornerRadius(16);
                CapsuleContentMargin = new Thickness(12, 9, 12, 9);
                TrafficFontSize = 30;
                TrafficLineHeight = 34;
                SpeedFontSize = 12;
                SpeedBlockMargin = new Thickness(10, 0, 0, 0);
                SpeedArrowMargin = new Thickness(0, 0, 3, 0);
                SpeedLabelMargin = new Thickness(0, 0, 5, 0);
                SpeedRowSpacing = new Thickness(0, 3, 0, 0);
                break;

            default:
                // 最小可读档：40–55 DIP 高。字号不再随高度线性缩小，避免低于 8 DIP。
                CapsuleCornerRadius = new CornerRadius(12);
                CapsuleContentMargin = new Thickness(7, 5, 7, 5);
                TrafficFontSize = 16;
                TrafficLineHeight = 19;
                SpeedFontSize = 8.5;
                SpeedBlockMargin = new Thickness(6, 0, 0, 0);
                SpeedArrowMargin = new Thickness(0, 0, 2, 0);
                SpeedLabelMargin = new Thickness(0, 0, 3, 0);
                SpeedRowSpacing = new Thickness(0, 2, 0, 0);
                break;
        }
    }

    /// <summary>会话开始：清掉"上次会话"占位，等待快照里的累计值。</summary>
    public void ApplySessionStarted()
    {
        _lastClosedDownload = null;
        _lastClosedUpload = null;
        HasSession = true;
        SessionText = "0 B";
    }

    /// <summary>会话结束：记住最终累计，断开后用于"上次会话"显示。</summary>
    public void ApplySessionEnded(MonitorSessionEndedInfo info)
    {
        _lastClosedDownload = info.DownloadBytes;
        _lastClosedUpload = info.UploadBytes;
        HasSession = false;
    }

    public void Apply(MonitorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            ShowDisconnected(null);
            return;
        }

        Warning = snapshot.Warning ?? string.Empty;
        IsConnected = snapshot.IsTargetConnected && snapshot.State == MonitorState.Running;

        if (!IsConnected)
        {
            ShowDisconnected(snapshot);
            return;
        }

        if (snapshot.HasSession)
        {
            HasSession = true;
            SessionText = ByteSizeFormatter.Format(SafeAdd(snapshot.SessionDownloadBytes, snapshot.SessionUploadBytes));
            DownloadText = FormatRate(snapshot.DownloadBytesPerSecond);
            UploadText = FormatRate(snapshot.UploadBytesPerSecond);
            return;
        }

        // 已连接但引擎还没建立会话（例如启动后的第一帧）：没有有效会话，显示 "--"。
        HasSession = false;
        SessionText = "--";
        DownloadText = "0 B/s";
        UploadText = "0 B/s";
    }

    private void ShowDisconnected(MonitorSnapshot? snapshot)
    {
        IsConnected = false;
        HasSession = false;
        DownloadText = "0 B/s";
        UploadText = "0 B/s";

        if (_lastClosedDownload is long down && _lastClosedUpload is long up)
        {
            var formatted = ByteSizeFormatter.Format(SafeAdd(down, up));

            // 最简排版下空间只够一个数字，前缀会与速度块相撞；
            // 其余档位保留"上次"前缀，满足断开后明确标注"上次会话"的产品要求。
            SessionText = _density == CapsuleDensity.Minimal ? formatted : $"上次 {formatted}";
            return;
        }

        // 首次运行、从未建立过会话：明确显示 "--"，不能用 0 B 冒充。
        SessionText = "--";
        _ = snapshot;
    }

    /// <summary>采集异常：保留当前显示，只更新提示气泡里的原因。</summary>
    public void SetWarning(string warning) => Warning = warning;

    /// <summary>引擎无法启动或采集失败时调用：保持红色状态，只把原因放进 Tooltip。</summary>
    public void ShowUnavailable(string reason)
    {
        IsConnected = false;
        SessionText = "--";
        DownloadText = "0 B/s";
        UploadText = "0 B/s";
        Warning = reason;
    }

    /// <summary>开发期自检：注入一段极长文本，验证最简排版也不会把速度块挤出窗口。</summary>
    internal void SetSessionTextForTest(string text) => SessionText = text;

    /// <summary>开发期自检：恢复真实数据文本。</summary>
    internal void SetSpeedTextForTest(string download, string upload)
    {
        DownloadText = download;
        UploadText = upload;
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 1)
        {
            return "0 B/s";
        }

        return ByteSizeFormatter.Format((long)Math.Round(bytesPerSecond), CultureInfo.CurrentCulture) + "/s";
    }

    private static long SafeAdd(long left, long right)
    {
        // 累积器已经做了溢出保护；这里再兜一层，避免只读 UI 因异常数据崩溃。
        if (left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(IsConnected))
        {
            OnPropertyChanged(nameof(Background));
        }

        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}