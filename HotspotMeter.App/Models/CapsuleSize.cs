namespace HotspotMeter.App.Models;

/// <summary>
/// 悬浮胶囊的尺寸（DIP）。所有预设与自定义尺寸都保持 320 : 104 的统一比例：
/// 布局是按比例缩放的，不允许单独拉伸某个方向，否则文字行高与速度区域会互相挤压。
/// </summary>
public readonly record struct CapsuleSize(double Width, double Height)
{
    public const double DefaultWidth = 320;
    public const double DefaultHeight = 104;

    /// <summary>迷你尺寸下限；再小则 8–9 DIP 的辅助文字已无法辨认。</summary>
    public const double MinWidth = 220;
    public const double MaxWidth = 480;
    public const double MinHeight = MinWidth * DefaultHeight / DefaultWidth;   // 71.5
    public const double MaxHeight = MaxWidth * DefaultHeight / DefaultWidth;   // 156

    /// <summary>标准比例 3.0769；允许 1.2% 的输入误差（四舍五入到 0.1 DIP 造成）。</summary>
    public const double AspectRatio = DefaultWidth / DefaultHeight;
    public const double AspectTolerance = 0.012;

    public static CapsuleSize Default => new(DefaultWidth, DefaultHeight);

    public static CapsuleSize Mini => new(220, 71.5);

    public static CapsuleSize Small => new(280, 91);

    public static CapsuleSize Large => new(400, 130);

    public double AspectRatioValue => Height <= 0 ? 0 : Width / Height;

    /// <summary>
    /// 相对标准尺寸的缩放系数。比例受 <see cref="TryCreate"/> 约束，正常情况下宽高同比例；
    /// 这里仍取较小值，保证即使从旧状态文件读到轻微失真的尺寸也不会裁切内容。
    /// </summary>
    public double Scale => Math.Clamp(Math.Min(Width / DefaultWidth, Height / DefaultHeight), 0.65, 1.6);

    public static bool TryCreate(double width, double height, out CapsuleSize size, out string? error)
    {
        size = Default;
        error = null;

        if (double.IsNaN(width) || double.IsInfinity(width) || width < MinWidth || width > MaxWidth)
        {
            error = $"宽度需在 {MinWidth:0}–{MaxWidth:0} DIP 之间。";
            return false;
        }

        if (double.IsNaN(height) || double.IsInfinity(height) || height < MinHeight || height > MaxHeight)
        {
            error = $"高度需在 {MinHeight:0.#}–{MaxHeight:0.#} DIP 之间（宽度 {width:0} 对应 {width * DefaultHeight / DefaultWidth:0.#}）。";
            return false;
        }

        var expectedHeight = width * DefaultHeight / DefaultWidth;
        var ratio = width / height;
        if (Math.Abs(ratio - AspectRatio) / AspectRatio > AspectTolerance)
        {
            error = $"宽高比需保持 320 : 104（约 3.08 : 1）。当前 {width:0.#} × {height:0.#}，" +
                    $"请把高度改为 {expectedHeight:0.#}，或把宽度改为 {height * DefaultWidth / DefaultHeight:0.#}。";
            return false;
        }

        size = new CapsuleSize(width, height);
        return true;
    }

    /// <summary>
    /// 从持久化值恢复。旧版 ui-state.json 没有尺寸字段，或旧尺寸不再符合统一比例时，
    /// 静默回退到标准尺寸，而不是让悬浮窗以非法大小启动。
    /// </summary>
    public static CapsuleSize FromPersisted(double? width, double? height)
    {
        if (width is not double w || height is not double h)
        {
            return Default;
        }

        if (TryCreate(w, h, out var size, out _))
        {
            return size;
        }

        // 兼容 4C-2 的旧尺寸（280×92、260×88 等）：按宽度映射到统一比例，
        // 保留用户"大概这么大"的意图，而不是直接跳回标准尺寸。
        var mappedWidth = Math.Clamp(w, MinWidth, MaxWidth);
        return new CapsuleSize(Math.Round(mappedWidth, 1), Math.Round(mappedWidth * DefaultHeight / DefaultWidth, 1));
    }

    public bool IsNearly(CapsuleSize other) =>
        Math.Abs(Width - other.Width) < 0.05 && Math.Abs(Height - other.Height) < 0.05;
}