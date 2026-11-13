using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace HotspotMeter.App.Services;

/// <summary>
/// 仅供开发期验证布局的诊断开关：设置环境变量 HOTSPOTMETER_UI_DUMP=文件路径 后，
/// 窗口加载完成时把 DPI、窗口位置、每个文本块的文字与相对窗口的边界写入该文件。
/// 不写日志、不留后台任务，未设置环境变量时完全不执行。
/// </summary>
internal static class UiDump
{
    public static void DumpIfRequested(Window window)
    {
        var path = Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_DUMP");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // 延迟到引擎完成首帧采样后再抓取，否则只能看到初始的"未连接 / --"。
        var delay = int.TryParse(Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_DUMP_DELAY_MS"), out var parsed)
            ? Math.Max(0, parsed)
            : 2500;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            var builder = new StringBuilder();
            try
            {
                builder.Append(Describe(window));
            }
            catch (Exception ex)
            {
                builder.AppendLine("dump failed: " + ex);
            }

            try
            {
                File.WriteAllText(path, builder.ToString());
            }
            catch (IOException)
            {
                // 诊断失败不影响产品行为。
            }

            // 可选：同时导出 PNG（例如 docs/images 的证据图），必须在退出前完成。
            var pngPath = Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_DUMP_PNG");
            if (!string.IsNullOrWhiteSpace(pngPath))
            {
                try
                {
                    SavePng(window, pngPath);
                }
                catch (Exception)
                {
                    // 诊断失败不影响产品行为。
                }
            }

            if (Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_DUMP_EXIT") == "1")
            {
                Application.Current.Shutdown();
            }
        };
        timer.Start();
    }

    /// <summary>把窗口的 DPI、位置、尺寸与全部文本块边界描述成可读文本。</summary>
    public static string Describe(Window window)
    {
        var builder = new StringBuilder();
        if (!window.IsVisible)
        {
            builder.AppendLine($"window=\"{window.GetType().Name}\" visible=false");
            return builder.ToString();
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        builder.AppendLine(
            $"window=\"{window.GetType().Name}\" visible=true left={window.Left:0.#} top={window.Top:0.#} " +
            $"actual={window.ActualWidth:0.#}x{window.ActualHeight:0.#} w={window.Width:0.#} h={window.Height:0.#} " +
            $"stc={window.SizeToContent} dpi={dpi.DpiScaleX}x{dpi.DpiScaleY}");

        foreach (var textBlock in FindTextBlocks(window))
        {
            var bounds = textBlock.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, textBlock.ActualWidth, textBlock.ActualHeight));
            builder.AppendLine(
                $"text=\"{textBlock.Text}\" font={textBlock.FontSize:0.#} " +
                $"bounds={bounds.X:0.#},{bounds.Y:0.#},{bounds.Width:0.#}x{bounds.Height:0.#} " +
                $"clip={textBlock.ActualWidth:0.#}x{textBlock.ActualHeight:0.#}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 机械校验文本布局：所有文本必须落在窗口内，且任意两个文本矩形不得重叠。
    /// 这是"文字不重叠、不越界"的可重复证据，不依赖肉眼或视觉模型。
    /// </summary>
    public static string CheckLayout(Window window)
    {
        var bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        var items = new List<(string Text, Rect Rect)>();
        foreach (var textBlock in FindTextBlocks(window))
        {
            if (string.IsNullOrEmpty(textBlock.Text))
            {
                continue;
            }

            var rect = textBlock.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, textBlock.ActualWidth, textBlock.ActualHeight));
            items.Add((textBlock.Text, rect));
        }

        var outOfBounds = items
            .Where(i => i.Rect.Left < -0.5 || i.Rect.Top < -0.5 ||
                        i.Rect.Right > bounds.Right + 0.5 || i.Rect.Bottom > bounds.Bottom + 0.5)
            .Select(i => $"\"{i.Text}\"@{i.Rect.X:0.#},{i.Rect.Y:0.#},{i.Rect.Width:0.#}x{i.Rect.Height:0.#}")
            .ToList();

        var overlaps = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                var intersection = Rect.Intersect(items[i].Rect, items[j].Rect);
                // 只把有实际面积的重叠算作缺陷，相邻元素边界相接不算。
                if (intersection.Width > 0.5 && intersection.Height > 0.5)
                {
                    overlaps.Add($"\"{items[i].Text}\" × \"{items[j].Text}\" " +
                                 $"({intersection.Width:0.#}x{intersection.Height:0.#})");
                }
            }
        }

        var verdict = outOfBounds.Count == 0 && overlaps.Count == 0 ? "ok" : "FAILED";
        var detail = string.Empty;
        if (outOfBounds.Count > 0)
        {
            detail += " outOfBounds=[" + string.Join("; ", outOfBounds) + "]";
        }

        if (overlaps.Count > 0)
        {
            detail += " overlaps=[" + string.Join("; ", overlaps) + "]";
        }

        return $"layout={verdict} texts={items.Count}{detail}";
    }

    /// <summary>
    /// 校验“速度块整体垂直中心 = 流量数字控件垂直中心”（m03738）。
    /// 两者都在同一 Grid 行内 VerticalAlignment=Center，因此这里直接比较控件矩形中心；
    /// 误差超过 0.5 DIP 视为未对齐（不依赖截图或肉眼判断）。
    /// </summary>
    public static string CheckSpeedAlignment(
        Window window,
        string trafficName = "TrafficValue",
        string speedName = "SpeedBlock")
    {
        if (window.FindName(trafficName) is not FrameworkElement traffic ||
            window.FindName(speedName) is not FrameworkElement speed)
        {
            return "speedAlign=missing";
        }

        var trafficRect = traffic.TransformToAncestor(window)
            .TransformBounds(new Rect(0, 0, traffic.ActualWidth, traffic.ActualHeight));
        var speedRect = speed.TransformToAncestor(window)
            .TransformBounds(new Rect(0, 0, speed.ActualWidth, speed.ActualHeight));

        var trafficCenter = trafficRect.Top + trafficRect.Height / 2;
        var speedCenter = speedRect.Top + speedRect.Height / 2;
        var delta = Math.Round(speedCenter - trafficCenter, 2);
        var ok = Math.Abs(delta) <= 0.5;

        return $"speedAlign ok={ok} trafficCenterY={trafficCenter:0.##} speedCenterY={speedCenter:0.##} " +
               $"delta={delta:0.##} trafficH={trafficRect.Height:0.#} speedH={speedRect.Height:0.#}";
    }

    /// <summary>
    /// 校验"速度区在流量数字右侧、下载在上、上传在下"。
    /// 返回可读证据；只有在元素缺失或顺序颠倒时才标记 failed。
    /// </summary>
    public static string DescribeSpeedBlock(Window window, string name = "SpeedBlock")
    {
        if (window.FindName(name) is not FrameworkElement block || block.ActualWidth <= 0)
        {
            return "speedBlock=missing";
        }

        var blockBounds = block.TransformToAncestor(window)
            .TransformBounds(new Rect(0, 0, block.ActualWidth, block.ActualHeight));

        var rows = new List<(string Text, Rect Rect, double Font)>();
        foreach (var textBlock in FindTextBlocks(block))
        {
            if (string.IsNullOrEmpty(textBlock.Text) || textBlock.ActualWidth <= 0)
            {
                continue;
            }

            var rect = textBlock.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, textBlock.ActualWidth, textBlock.ActualHeight));
            rows.Add((textBlock.Text, rect, textBlock.FontSize));
        }

        // 用方向箭头识别上下行：最小排版会隐藏"下载/上传"文字，但箭头始终保留。
        var download = rows.FirstOrDefault(r => r.Text.Contains('↓'));
        var upload = rows.FirstOrDefault(r => r.Text.Contains('↑'));
        var stacked = download.Rect.Height > 0 && upload.Rect.Height > 0 &&
                      download.Rect.Top < upload.Rect.Top;
        var labelsVisible = rows.Any(r => r.Text.Contains("下载", StringComparison.Ordinal));
        var inside = blockBounds.Left >= -0.5 && blockBounds.Top >= -0.5 &&
                     blockBounds.Right <= window.ActualWidth + 0.5 &&
                     blockBounds.Bottom <= window.ActualHeight + 0.5;

        var texts = string.Join("; ", rows.Select(r =>
            $"\"{r.Text}\"@{r.Rect.X:0.#},{r.Rect.Y:0.#},{r.Rect.Width:0.#}x{r.Rect.Height:0.#} f={r.Font:0.#}"));
        return $"speedBlock={blockBounds.X:0.#},{blockBounds.Y:0.#},{blockBounds.Width:0.#}x{blockBounds.Height:0.#} " +
               $"stacked={stacked} labels={labelsVisible} inside={inside} rows=[{texts}] verdict={(stacked && inside ? "ok" : "FAILED")}";
    }

    /// <summary>
    /// 验证 4C-8 的 UI 精简：看不见"已连接/未连接"和"已使用流量"，
    /// 同时记录流量与速度字号，供报告引用。
    /// </summary>
    public static string CheckSimplifiedContent(Window window)
    {
        var texts = FindTextBlocks(window)
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var statusPresent = texts.Any(t => t is "已连接" or "未连接");
        var captionPresent = texts.Any(t => t.Contains("已使用流量", StringComparison.Ordinal));
        var hasWifiPath = FindDescendants<System.Windows.Shapes.Path>(window).Any();
        var trafficFont = window.FindName("TrafficValue") is TextBlock traffic ? traffic.FontSize : 0;
        var speedFont = window.FindName("SpeedBlock") is FrameworkElement block
            ? FindTextBlocks(block).Select(t => t.FontSize).DefaultIfEmpty(0).Max()
            : 0;

        var captionAbove = false;
        var captionLeftAligned = false;
        var captionFontMatch = false;
        var captionVisible = false;
        var captionOpacity = 0.0;
        var trafficOpacity = 0.0;
        var opacityIsolated = false;
        if (window.FindName("TrafficCaption") is TextBlock caption &&
            window.FindName("TrafficValue") is TextBlock trafficValue)
        {
            captionVisible = caption.Visibility == Visibility.Visible && caption.ActualWidth > 0;
            var captionRect = caption.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, caption.ActualWidth, caption.ActualHeight));
            var trafficRect = trafficValue.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, trafficValue.ActualWidth, trafficValue.ActualHeight));
            captionAbove = captionRect.Bottom <= trafficRect.Top + 0.5;
            captionLeftAligned = Math.Abs(captionRect.Left - trafficRect.Left) <= 0.5;
            captionFontMatch = string.Equals(
                caption.FontFamily.Source, trafficValue.FontFamily.Source, StringComparison.OrdinalIgnoreCase);
            captionOpacity = caption.Opacity;
            trafficOpacity = trafficValue.Opacity;
            // 透明度必须只加在标签自身：数字不透明，且共用的父容器也不透明。
            opacityIsolated = Math.Abs(captionOpacity - 0.65) <= 0.001
                              && trafficOpacity >= 0.999
                              && (caption.Parent is not FrameworkElement parent || parent.Opacity >= 0.999);
        }

        // 自动适配的 Compact/Minimal 密度有意隐藏标签；此时只校验"没有状态文字/图标"。
        var captionOk = !captionVisible ||
                        (captionPresent && captionAbove && captionLeftAligned && captionFontMatch);
        var ok = !statusPresent && captionOk && !hasWifiPath && (!captionVisible || opacityIsolated);
        return $"simplified ok={ok} statusText={statusPresent} caption={captionPresent} " +
               $"captionVisible={captionVisible} captionAbove={captionAbove} " +
               $"captionLeftAligned={captionLeftAligned} captionFontMatch={captionFontMatch} " +
               $"captionOpacity={captionOpacity:0.00} trafficOpacity={trafficOpacity:0.00} " +
               $"opacityIsolated={opacityIsolated} " +
               $"wifiIcon={hasWifiPath} trafficFont={trafficFont:0.#} speedFont={speedFont:0.#}";
    }

    /// <summary>把任一 WPF 元素渲染成 PNG；用于 ContextMenu 这类没有独立 Window 的弹出层。</summary>
    public static void SaveElementPng(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>读取设置窗口纵向滚动条的实际模板状态，证明深色模板已生效而不是系统默认。</summary>
    public static string DescribeScrollBar(DependencyObject root, string? viewerName = null)
    {
        var viewer = viewerName is not null && root is FrameworkElement element
            ? element.FindName(viewerName) as ScrollViewer
            : FindDescendants<ScrollViewer>(root).FirstOrDefault(v => v.ActualHeight > 100);
        if (viewer is null)
        {
            return "scrollViewer=missing";
        }

        var bars = FindDescendants<ScrollBar>(viewer)
            .Where(b => b.Orientation == Orientation.Vertical)
            .ToList();
        if (bars.Count == 0)
        {
            return $"scrollViewer=missing scrollable={viewer.ScrollableHeight:0.#} viewport={viewer.ViewportHeight:0.#}";
        }

        // TextBox 模板里也有隐藏的 ScrollBar，取实际有宽度的那一个。
        var bar = bars.FirstOrDefault(b => b.ActualWidth > 0) ?? bars[0];
        bar.ApplyTemplate();
        var track = bar.Template?.FindName("PART_Track", bar) as Track;
        var thumb = track?.Thumb;
        return $"scrollViewer scrollable={viewer.ScrollableHeight:0.#} viewport={viewer.ViewportHeight:0.#} " +
               $"bars={bars.Count} width={bar.ActualWidth:0.#} visible={bar.IsVisible} " +
               $"style={(bar.Style?.TargetType.Name ?? "null")} " +
               $"track={track is not null} thumbFill={(thumb?.Background as SolidColorBrush)?.Color} " +
               $"pageButton={(track?.DecreaseRepeatButton as RepeatButton)?.Style?.TargetType.Name ?? "null"}";
    }

    /// <summary>
    /// 用 PrintWindow 抓取窗口 PNG。分层/透明窗口无法用 CopyFromScreen 抓到，
    /// 但 PrintWindow 能让窗口自己绘制，作为视觉验证的备用证据。
    /// </summary>
    public static void SavePng(Window window, string path)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || window.ActualWidth <= 0 || window.ActualHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));

        using var bitmap = new Drawing.Bitmap(width, height, DrawingImaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(Drawing.Color.Transparent);
        var hdc = graphics.GetHdc();
        try
        {
            PrintWindow(handle, hdc, 2);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        bitmap.Save(path, DrawingImaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock textBlock)
            {
                yield return textBlock;
            }

            foreach (var nested in FindTextBlocks(child))
            {
                yield return nested;
            }
        }
    }
}