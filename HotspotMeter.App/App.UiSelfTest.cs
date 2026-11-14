using System.IO;
using System.Text;
using System.Windows;
using HotspotMeter.App.Models;
using HotspotMeter.App.Services;
using HotspotMeter.App.ViewModels;
using HotspotMeter.App.Views;
using HotspotMeter.Core.Layout;

namespace HotspotMeter.App;

/// <summary>
/// 开发期 UI 自检：仅当环境变量 HOTSPOTMETER_UI_TEST 指向输出文件时运行。
/// 它驱动真实的窗口/设置/尺寸代码路径并记录结果，不是产品功能，也不参与正式启动逻辑。
/// </summary>
public partial class App
{
    internal async Task RunUiSelfTestAsync(string outputPath)
    {
        var log = new StringBuilder();
        var directory = Path.GetDirectoryName(outputPath) ?? ".";
        Directory.CreateDirectory(directory);

        try
        {
            if (!_capsule.IsVisible)
            {
                log.AppendLine(
                    $"startup.hidden=true snapshots={_monitor?.SnapshotCount} trayVisible={_tray?.IsVisible}");
                _capsule.ShowCapsule();
                await Task.Delay(700);
            }

            // 等首帧快照到达，确保记录的是真实数据而不是初始占位。
            await Task.Delay(1000);

            log.AppendLine("== initial ==");
            log.Append(UiDump.Describe(_capsule));
            log.AppendLine(UiDump.CheckLayout(_capsule));
            log.AppendLine(UiDump.CheckSimplifiedContent(_capsule));
            log.AppendLine(
                $"vm.size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"pinned={_capsuleViewModel.IsPinned} session={_capsuleViewModel.SessionText} " +
                $"speed={_capsuleViewModel.DownloadText}/{_capsuleViewModel.UploadText} " +
                $"labels={_capsuleViewModel.ShowSpeedLabels}");
            UiDump.SavePng(_capsule, Path.Combine(directory, "capsule-default.png"));

            // 胶囊菜单不得包含"设置"（设置入口只在托盘）。
            var capsuleMenu = string.Join(
                " | ",
                _capsule.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                    .Select(i => $"{i.Header}{(i.IsEnabled ? string.Empty : "(禁用)")}"));
            log.AppendLine($"capsule.menu={capsuleMenu}");
            log.AppendLine($"capsule.menuHasSettings={capsuleMenu.Contains("设置", StringComparison.Ordinal)}");

            // 右键菜单真实打开/关闭循环：4C-2 的闪退发生在菜单首次布局时
            // （MenuItem 样式被套到 Separator 上），这里直接驱动同一条路径。
            for (var cycle = 0; cycle < 10; cycle++)
            {
                _capsule.ContextMenu.IsOpen = true;
                await Task.Delay(220);
                if (cycle == 0)
                {
                    var menuBackground = (_capsule.ContextMenu.Background as System.Windows.Media.SolidColorBrush)?.Color;
                    var menuBorder = (_capsule.ContextMenu.BorderBrush as System.Windows.Media.SolidColorBrush)?.Color;
                    log.AppendLine(
                        $"contextMenu.background={menuBackground} border={menuBorder} " +
                        $"padding={_capsule.ContextMenu.Padding} template={_capsule.ContextMenu.Template is not null}");
                    UiDump.SaveElementPng(_capsule.ContextMenu, Path.Combine(directory, "context-menu.png"));
                }

                _capsule.ContextMenu.IsOpen = false;
                await Task.Delay(120);
            }

            log.AppendLine("contextMenu.cycles=10 ok=true");

            log.AppendLine(
                $"tray.visible={_tray?.IsVisible} tray.menu={string.Join(" | ", _tray?.MenuItemTexts ?? [])}");
            log.AppendLine($"engine.running={_monitor?.IsRunning} snapshots={_monitor?.SnapshotCount}");

            foreach (var (name, size) in new (string, CapsuleSize)[]
                     {
                         ("mini", CapsuleSize.Mini),
                         ("small", CapsuleSize.Small),
                         ("large", CapsuleSize.Large),
                         ("standard", CapsuleSize.Default),
                     })
            {
                ApplySize(size);
                await Task.Delay(450);
                log.AppendLine($"== size {name} ==");
                log.Append(UiDump.Describe(_capsule));
                log.AppendLine(UiDump.CheckLayout(_capsule));
                log.AppendLine(UiDump.CheckSimplifiedContent(_capsule));
                log.AppendLine(
                    $"vm.size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                    $"trafficFont={_capsuleViewModel.TrafficFontSize:0.#} speedFont={_capsuleViewModel.SpeedFontSize:0.#}");
                log.AppendLine(UiDump.DescribeSpeedBlock(_capsule));
                log.AppendLine(UiDump.CheckSpeedAlignment(_capsule));
                UiDump.SavePng(_capsule, Path.Combine(directory, $"capsule-{name}.png"));
            }

            ApplySize(new CapsuleSize(380, 123.5));
            await Task.Delay(450);
            log.AppendLine("== size custom 380x123.5 ==");
            log.Append(UiDump.Describe(_capsule));
            log.AppendLine(UiDump.CheckLayout(_capsule));
            log.AppendLine(UiDump.CheckSpeedAlignment(_capsule));
            UiDump.SavePng(_capsule, Path.Combine(directory, "capsule-custom.png"));

            // 极端长文本：最小尺寸下也不能把速度块挤出窗口（Viewbox 只缩小、不裁切）。
            ApplySize(CapsuleSize.Mini);
            await Task.Delay(350);
            var originalSession = _capsuleViewModel.SessionText;
            var originalDownload = _capsuleViewModel.DownloadText;
            var originalUpload = _capsuleViewModel.UploadText;
            _capsuleViewModel.SetSessionTextForTest("999.9 GB");
            _capsuleViewModel.SetSpeedTextForTest("999.9 MB/s", "999.9 MB/s");
            await Task.Delay(300);
            log.AppendLine("== long text stress ==");
            log.AppendLine(UiDump.CheckLayout(_capsule));
            log.AppendLine(UiDump.DescribeSpeedBlock(_capsule));
            log.AppendLine(UiDump.CheckSpeedAlignment(_capsule));
            UiDump.SavePng(_capsule, Path.Combine(directory, "capsule-long-text.png"));
            _capsuleViewModel.SetSessionTextForTest(originalSession);
            _capsuleViewModel.SetSpeedTextForTest(originalDownload, originalUpload);

            ApplySize(CapsuleSize.Default);
            await Task.Delay(300);

            // 位置越界校正：模拟记忆到屏幕外的坐标。
            _capsule.ClampForTest(99999, 99999);
            await Task.Delay(100);
            log.AppendLine($"clamp.bottomRight=left={_capsule.Left:0.#} top={_capsule.Top:0.#}");
            _capsule.ClampForTest(-99999, -99999);
            await Task.Delay(100);
            log.AppendLine($"clamp.topLeft=left={_capsule.Left:0.#} top={_capsule.Top:0.#}");

            // 固定/解除固定
            SetPinned(true);
            await Task.Delay(200);
            log.AppendLine($"== pinned == pinned={_capsuleViewModel.IsPinned} menu=\"{_capsuleViewModel.PinMenuText}\"");
            SetPinned(false);
            await Task.Delay(200);
            log.AppendLine($"== unpinned == pinned={_capsuleViewModel.IsPinned} menu=\"{_capsuleViewModel.PinMenuText}\"");

            // 自动适配：只改尺寸，不改位置。先放到确定的安全位置，便于比较。
            _capsule.MoveForTest(240, 240);
            await Task.Delay(150);
            SetAutoFitTaskbar(true);
            await Task.Delay(700);
            log.AppendLine("== autofit real ==");
            log.AppendLine($"taskbar.detect={_capsule.DescribeTaskbar()}");
            log.AppendLine(
                $"autofit.real=left={_capsule.Left:0.#} top={_capsule.Top:0.#} " +
                $"size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"density={_capsuleViewModel.Density} labels={_capsuleViewModel.ShowSpeedLabels} " +
                $"trafficFont={_capsuleViewModel.TrafficFontSize:0.#} fontScale={_capsuleViewModel.TrafficFontScale:0.00} " +
                $"layout={UiDump.CheckLayout(_capsule)}");
            log.AppendLine(UiDump.CheckSpeedAlignment(_capsule));
            log.AppendLine(UiDump.DescribeTrafficValue(_capsule));
            log.AppendLine(UiDump.CheckSimplifiedContent(_capsule));
            UiDump.SavePng(_capsule, Path.Combine(directory, "capsule-autofit.png"));

            // 4C-9：首次开启自动适配应定位到底部任务栏内部偏左（X≈120 DIP），
            // 而不是继续保持用户原来的位置。
            var realInput = _capsule.LastDetectionInput
                ?? throw new InvalidOperationException("自动适配未产生任务栏检测结果。");
            var realDpi = realInput.DpiScale;
            var realTaskbar = realInput.TaskbarRect;
            var realHeightPx = _capsuleViewModel.Size.Height * realDpi;
            var anchorTopPx = _capsule.Top * realDpi;
            var anchorInsideTaskbar = realTaskbar is { } anchorTaskbar
                && (_capsule.Left * realDpi) >= anchorTaskbar.Left
                && anchorTopPx >= anchorTaskbar.Top - 0.5
                && (anchorTopPx + realHeightPx) <= anchorTaskbar.Bottom + 0.5;
            log.AppendLine(
                $"autofit.anchor left={_capsule.Left:0.#} top={_capsule.Top:0.#} " +
                $"expectedLeft=120 insideTaskbar={anchorInsideTaskbar} note=\"{_capsule.LastAnchorNote}\"");

            // 自动适配开启时允许把胶囊拖进任务栏：不能在工作区底部被弹回。
            if (realTaskbar is { } dragTaskbar)
            {
                var dragY = (dragTaskbar.Top + 4) / realDpi;
                _capsule.MoveForTest(300, dragY);
                _capsule.DragEndForTest();
                await Task.Delay(300);
                var draggedTopPx = _capsule.Top * realDpi;
                var dragInside = draggedTopPx >= dragTaskbar.Top - 0.5
                    && (draggedTopPx + realHeightPx) <= dragTaskbar.Bottom + 0.5;
                log.AppendLine(
                    $"autofit.dragIntoToolbar left={_capsule.Left:0.#} top={_capsule.Top:0.#} " +
                    $"insideTaskbar={dragInside}");
            }

            // 关闭自动适配：恢复工作区边界；同样的位置必须被夹回任务栏上方。
            SetAutoFitTaskbar(false);
            await Task.Delay(400);
            var normalY = realTaskbar is { } normalTaskbar ? (normalTaskbar.Top + 4) / realDpi : 1020;
            _capsule.MoveForTest(300, normalY);
            _capsule.DragEndForTest();
            await Task.Delay(250);
            var workBottomDips = realInput.WorkArea.Bottom / realDpi;
            var clampedInWorkArea = (_capsule.Top + _capsuleViewModel.Size.Height) <= workBottomDips + 0.5;
            log.AppendLine(
                $"autofit.normalClamp left={_capsule.Left:0.#} top={_capsule.Top:0.#} " +
                $"insideWorkArea={clampedInWorkArea} size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#}");

            // 再次开启：重新执行一次初始定位。
            SetAutoFitTaskbar(true);
            await Task.Delay(700);
            var reEnabledInside = realTaskbar is { } reEnabledTaskbar
                && ((_capsule.Top * realDpi) >= reEnabledTaskbar.Top - 0.5);
            log.AppendLine(
                $"autofit.reEnable left={_capsule.Left:0.#} top={_capsule.Top:0.#} " +
                $"insideTaskbar={reEnabledInside}");

            // TaskbarCreated：Explorer 重启后重新检测并恢复置顶，但不得移动用户位置。
            var beforeTaskbarCreated = (_capsule.Left, _capsule.Top);
            _capsule.NotifyTaskbarCreatedForTest();
            await Task.Delay(1100);
            log.AppendLine(
                $"autofit.taskbarCreated before={beforeTaskbarCreated.Item1:0.#},{beforeTaskbarCreated.Item2:0.#} " +
                $"after={_capsule.Left:0.#},{_capsule.Top:0.#} " +
                $"stable={Math.Abs(beforeTaskbarCreated.Item1 - _capsule.Left) < 0.5 && Math.Abs(beforeTaskbarCreated.Item2 - _capsule.Top) < 0.5}");

            log.AppendLine("== autofit synthetic ==");
            // 合成用例只验证"尺寸会变、位置不会被自动挪动"；先回到安全位置，
            // 避免真实任务栏锚点与更大的合成尺寸叠加后被边界夹取，干扰 moved 的含义。
            _capsule.MoveForTest(240, 240);
            await Task.Delay(150);
            var monitor = new PixelRect(0, 0, 2560, 1600);
            foreach (var (name, edge, bounds, work, taskbar, dpiScale) in new (string, TaskbarEdge, PixelRect, PixelRect, PixelRect, double)[]
                     {
                         ("bottom", TaskbarEdge.Bottom, monitor, new PixelRect(0, 0, 2560, 1528), new PixelRect(0, 1528, 2560, 1600), 1.5),
                         ("top", TaskbarEdge.Top, monitor, new PixelRect(0, 64, 2560, 1600), new PixelRect(0, 0, 2560, 64), 1.5),
                         ("left", TaskbarEdge.Left, monitor, new PixelRect(64, 0, 2560, 1600), new PixelRect(0, 0, 64, 1600), 1.5),
                         ("right", TaskbarEdge.Right, monitor, new PixelRect(0, 0, 2496, 1600), new PixelRect(2496, 0, 2560, 1600), 1.5),
                         ("dpi200", TaskbarEdge.Bottom, new PixelRect(0, 0, 3840, 2400), new PixelRect(0, 0, 3840, 2304), new PixelRect(0, 2304, 3840, 2400), 2.0),
                         ("secondMonitor", TaskbarEdge.Bottom, new PixelRect(1920, 0, 4480, 1440), new PixelRect(1920, 0, 4480, 1400), new PixelRect(1920, 1400, 4480, 1440), 1.0),
                         ("thin", TaskbarEdge.Bottom, monitor, new PixelRect(0, 0, 2560, 1560), new PixelRect(0, 1560, 2560, 1600), 1.5),
                         ("tall", TaskbarEdge.Bottom, monitor, new PixelRect(0, 0, 2560, 1500), new PixelRect(0, 1500, 2560, 1600), 1.5),
                     })
            {
                var input = new TaskbarLayoutInput(bounds, work, taskbar, edge, false, dpiScale);
                var expected = TaskbarSizingCalculator.Compute(
                    new TaskbarSizingInput(bounds, work, taskbar, edge, dpiScale));

                var beforePosition = (_capsule.Left, _capsule.Top);
                _capsule.ApplyAutoFitForTest(input);
                await Task.Delay(200);
                var moved = Math.Abs(beforePosition.Item1 - _capsule.Left) > 0.5 || Math.Abs(beforePosition.Item2 - _capsule.Top) > 0.5;

                log.AppendLine(
                    $"autofit.{name}=expectedSize={expected.Width:0.#}x{expected.Height:0.#} " +
                    $"actualSize={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                    $"density={_capsuleViewModel.Density} fits={expected.FitsTaskbar} " +
                    $"moved={moved} layout={UiDump.CheckLayout(_capsule)} " +
                    $"{UiDump.DescribeTrafficValue(_capsule)} " +
                    $"speed={UiDump.DescribeSpeedBlock(_capsule)}");
            }

            // 固定优先：位置锁定期间自动适配仍可改尺寸，但绝不能移动窗口。
            // 这里故意换一个会改变尺寸的几何（任务栏 120 DIP → 320×104），
            // 避免沿用上一个合成用例导致“尺寸没变”而无法证明尺寸仍可调整。
            SetPinned(true);
            await Task.Delay(150);
            var pinnedPosition = (_capsule.Left, _capsule.Top);
            var pinnedSizeBefore = _capsuleViewModel.Size;
            _capsule.ApplyAutoFitForTest(new TaskbarLayoutInput(
                monitor,
                new PixelRect(0, 0, 2560, 1420),
                new PixelRect(0, 1420, 2560, 1600),
                TaskbarEdge.Bottom,
                false,
                1.5));
            await Task.Delay(200);
            log.AppendLine(
                $"autofit.pinnedStable={Math.Abs(pinnedPosition.Item1 - _capsule.Left) < 0.5 && Math.Abs(pinnedPosition.Item2 - _capsule.Top) < 0.5} " +
                $"sizeChanged={!_capsuleViewModel.Size.IsNearly(pinnedSizeBefore)} " +
                $"size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#}");
            SetPinned(false);
            await Task.Delay(200);

            // 关闭自动适配：恢复开启前的手动尺寸（320×104）。
            SetAutoFitTaskbar(false);
            await Task.Delay(400);
            log.AppendLine(
                $"autofit.disabled size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"enabled={AutoFitTaskbar} density={_capsuleViewModel.Density} " +
                $"trafficFont={_capsuleViewModel.TrafficFontSize:0.#} fontScale={_capsuleViewModel.TrafficFontScale:0.00}");

            // 设置窗口开关：走 SettingsViewModel → ISettingsHost → CapsuleWindow 的真实链路，
            // 并确认状态已经写入 ui-state.json（重启后据此恢复）。
            var settingsVm = new SettingsViewModel(this);
            settingsVm.AutoFitTaskbar = true;
            await Task.Delay(600);
            log.AppendLine(
                $"autofit.viaSettings enabled={AutoFitTaskbar} size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"pos={_capsule.Left:0.#},{_capsule.Top:0.#} persisted={PersistedAutoFitValue()} " +
                $"status=\"{settingsVm.TaskbarStatusText}\"");

            // 任务栏变化消息：投递真实 WM_SETTINGCHANGE(SPI_SETWORKAREA)，验证钩子 + 600ms 防抖后的重算。
            var beforeReAdapt = (_capsule.Left, _capsule.Top);
            _capsule.NotifyTaskbarChangedForTest();
            await Task.Delay(1100);
            log.AppendLine(
                $"autofit.reAdapt before={beforeReAdapt.Item1:0.#},{beforeReAdapt.Item2:0.#} " +
                $"after={_capsule.Left:0.#},{_capsule.Top:0.#} size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"layout={UiDump.CheckLayout(_capsule)}");

            settingsVm.AutoFitTaskbar = false;
            await Task.Delay(400);
            log.AppendLine($"autofit.viaSettingsOff enabled={AutoFitTaskbar} persisted={PersistedAutoFitValue()}");

            // 自动适配不得覆盖用户手动尺寸：先手动设 400×130，再开/关自动适配，应回到 400×130。
            ApplySize(CapsuleSize.Large);
            await Task.Delay(350);
            settingsVm.AutoFitTaskbar = true;
            await Task.Delay(600);
            log.AppendLine(
                $"autofit.keepsManual.on size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"layout={UiDump.CheckLayout(_capsule)}");
            settingsVm.AutoFitTaskbar = false;
            await Task.Delay(450);
            log.AppendLine(
                $"autofit.keepsManual.off size={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"layout={UiDump.CheckLayout(_capsule)}");

            // 设置窗口：单实例 + 激活已有窗口
            OpenSettings();
            await Task.Delay(800);
            var settings = Windows.OfType<SettingsWindow>().FirstOrDefault();
            log.AppendLine("== settings ==");
            log.Append(settings is null ? "settings window missing" : UiDump.Describe(settings));
            if (settings is not null)
            {
                log.AppendLine($"settings.size={settings.ActualWidth:0.#}x{settings.ActualHeight:0.#}");
                log.AppendLine("settings." + UiDump.DescribeScrollBar(settings, "SettingsScroll"));
                UiDump.SavePng(settings, Path.Combine(directory, "settings-window.png"));
            }

            OpenSettings();
            await Task.Delay(300);
            var settingsCount = Windows.OfType<SettingsWindow>().Count();
            log.AppendLine($"settings.instanceCount={settingsCount}");

            // 自定义尺寸校验：范围与宽高比都必须给出中文原因。
            _ = CapsuleSize.TryCreate(200, 65, out _, out var tooSmall);
            _ = CapsuleSize.TryCreate(320, 200, out _, out var tooTall);
            _ = CapsuleSize.TryCreate(380, 130, out _, out var badRatio);
            log.AppendLine($"validation.tooSmall=\"{tooSmall}\"");
            log.AppendLine($"validation.tooTall=\"{tooTall}\"");
            log.AppendLine($"validation.badRatio=\"{badRatio}\"");

            // 隐藏/显示：位置与尺寸不得变化，监控不停止
            var snapshotsBeforeHide = _monitor?.SnapshotCount ?? 0;
            var before = (_capsule.Left, _capsule.Top, _capsuleViewModel.Size);
            _capsule.HideCapsule();
            await Task.Delay(1600);
            log.AppendLine($"== hidden == visible={_capsule.IsVisible}");
            log.AppendLine(
                $"hidden.snapshotsBefore={snapshotsBeforeHide} after={_monitor?.SnapshotCount} " +
                $"engineStillRunning={(_monitor?.SnapshotCount ?? 0) > snapshotsBeforeHide}");
            _capsule.ShowCapsule();
            await Task.Delay(400);
            log.AppendLine($"== shown == visible={_capsule.IsVisible} left={_capsule.Left:0.#} top={_capsule.Top:0.#}");
            log.AppendLine(
                $"hidden.positionStable={Math.Abs(before.Item1 - _capsule.Left) < 0.5 && Math.Abs(before.Item2 - _capsule.Top) < 0.5} " +
                $"sizeStable={before.Item3.IsNearly(_capsuleViewModel.Size)}");

            if (settings is not null)
            {
                settings.Close();
            }

            await Task.Delay(300);

            // 托盘"设置"菜单项走真实点击路径，验证入口确实打开独立窗口。
            _tray?.ClickSettingsForTest();
            await Task.Delay(700);
            var viaTray = Windows.OfType<SettingsWindow>().ToList();
            log.AppendLine($"tray.openSettings.count={viaTray.Count}");
            foreach (var window in viaTray)
            {
                window.Close();
            }

            await Task.Delay(300);
            log.AppendLine("== ui-state.json ==");
            log.AppendLine(File.Exists(_stateStore.FilePath)
                ? File.ReadAllText(_stateStore.FilePath)
                : "(missing)");

            // 下次启动应恢复 400×130（由外部脚本以第二个进程验证）。
            // 走真实路径：ApplySize → PersistState 由窗口在尺寸变化/关闭时写盘。
            ApplySize(CapsuleSize.Large);
            await Task.Delay(400);
            _capsule.ResetToDefaultPosition();
            await Task.Delay(300);
            log.AppendLine(
                $"restart.expect=400x130 actual={_capsuleViewModel.Size.Width:0.#}x{_capsuleViewModel.Size.Height:0.#} " +
                $"left={_capsule.Left:0.#} top={_capsule.Top:0.#}");
        }
        catch (Exception ex)
        {
            log.AppendLine("SELFTEST FAILED: " + ex);
        }

        try
        {
            File.WriteAllText(outputPath, log.ToString());
        }
        catch (IOException)
        {
            // 诊断输出失败不影响退出。
        }

        Shutdown();
    }

    /// <summary>
    /// 打开真实统计窗口，等待一次异步加载后把文本边界写盘；用于验证独立窗口、
    /// 程序排行与 7 天柱状图确实由真实数据库驱动，而不是静态占位。
    /// </summary>
    internal async Task RunStatsDumpAsync(string outputPath)
    {
        await Task.Delay(1200);
        OpenStats();

        // 数据库查询 + WinRT 逐应用查询都是异步的，给首轮加载留出时间。
        await Task.Delay(2600);

        var builder = new StringBuilder();
        builder.AppendLine(UiDump.Describe(_statsWindow!));
        builder.AppendLine(UiDump.CheckLayout(_statsWindow!));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, builder.ToString());

        var pngPath = Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_STATS_PNG");
        if (!string.IsNullOrWhiteSpace(pngPath))
        {
            UiDump.SavePng(_statsWindow!, pngPath);
        }

        if (Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_DUMP_EXIT") == "1")
        {
            Shutdown();
        }
    }

    /// <summary>
    /// 阶段 5 的运行时验收：真实读写 HKCU Run、真实切换任务栏显示、真实触发置顶回调，
    /// 并验证统计窗口单实例与关闭后后台继续采样。仅在显式设置环境变量时运行。
    /// </summary>
    internal async Task RunStage5SelfTestAsync(string outputPath)
    {
        var log = new StringBuilder();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        try
        {
            if (!_capsule.IsVisible)
            {
                _capsule.ShowCapsule();
                await Task.Delay(600);
            }

            await Task.Delay(800);
            var before = _startupRegistration.Read();
            log.AppendLine($"startup.before={before.State} command={before.Command}");
            var wasOurs = before.State == StartupRegistrationState.RegisteredForCurrentPath;
            if (before.State == StartupRegistrationState.RegisteredForOtherPath)
            {
                log.AppendLine("startup.write=skipped(foreign-entry)");
            }
            else
            {
                var enabled = _startupRegistration.Enable();
                var again = _startupRegistration.Enable();
                log.AppendLine(
                    $"startup.enable={enabled.State} again={again.State} read={_startupRegistration.Read().State}");
                var disabled = _startupRegistration.Disable();
                log.AppendLine($"startup.disable={disabled.State} read={_startupRegistration.Read().State}");
                if (wasOurs)
                {
                    _startupRegistration.Enable();
                    log.AppendLine($"startup.restored={_startupRegistration.Read().State}");
                }
            }

            var persisted = _stateStore.GetOrCreate().ShowStatsInTaskbar;
            log.AppendLine($"taskbar.value={persisted?.ToString() ?? "null(default-true)"}");
            SetShowStatsInTaskbar(false);
            OpenStats();
            await Task.Delay(700);
            log.AppendLine(
                $"taskbar.off persisted={ShowStatsInTaskbar} window={_statsWindow?.ShowInTaskbar}");
            SetShowStatsInTaskbar(true);
            log.AppendLine(
                $"taskbar.on persisted={ShowStatsInTaskbar} window={_statsWindow?.ShowInTaskbar}");
            if (persisted is null)
            {
                _stateStore.Update(state => state.ShowStatsInTaskbar = null);
            }

            var autoFitBefore = _capsule.IsAutoFitTaskbar;
            _capsule.SetAutoFitTaskbar(true);
            await Task.Delay(400);
            var reassertBefore = _capsule.TopmostReassertCount;
            var watcherRunning = _capsule.IsForegroundWatcherRunning;
            _capsule.SimulateForegroundChangedForTest("Chrome_WidgetWin_1");
            await Task.Delay(500);
            var reassertAfter = _capsule.TopmostReassertCount;
            _capsule.SetAutoFitTaskbar(false);
            await Task.Delay(250);
            log.AppendLine(
                $"topmost.watcher={watcherRunning} count={reassertBefore}->{reassertAfter} " +
                $"afterDisable={_capsule.IsForegroundWatcherRunning}");
            _capsule.SetAutoFitTaskbar(autoFitBefore);

            OpenStats();
            await Task.Delay(1400);
            var first = _statsWindow;
            OpenStats();
            var same = ReferenceEquals(first, _statsWindow);
            var stats = first?.DataContext as StatsViewModel;
            log.AppendLine(
                $"stats.singleInstance={same} days={stats?.Days.Count} programs={stats?.Programs.Count} " +
                $"total={stats?.TotalText} status={stats?.ProgramStatus}");
            first?.Close();
            await Task.Delay(400);
            log.AppendLine($"stats.closedReleases={_statsWindow is null}");

            var snapshotsBefore = _monitor?.SnapshotCount ?? 0;
            await Task.Delay(1600);
            var snapshotsAfter = _monitor?.SnapshotCount ?? 0;
            log.AppendLine($"monitor.afterClose={snapshotsBefore}->{snapshotsAfter} stillRunning={snapshotsAfter > snapshotsBefore}");
            log.AppendLine($"ui-layout={UiDump.CheckLayout(_capsule)}");
        }
        catch (Exception ex)
        {
            log.AppendLine("STAGE5 TEST FAILED: " + ex);
        }

        try
        {
            File.WriteAllText(outputPath, log.ToString());
        }
        catch (IOException)
        {
            // 诊断输出失败不影响退出。
        }

        Shutdown();
    }

    /// <summary>读取持久化文件里的 AutoFitTaskbar 值，用于断言设置确实落盘。</summary>
    private string PersistedAutoFitValue()
    {
        try
        {
            if (!File.Exists(_stateStore.FilePath))
            {
                return "missing";
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_stateStore.FilePath));
            return document.RootElement.TryGetProperty("AutoFitTaskbar", out var value)
                ? value.ToString()
                : "absent";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return "unreadable";
        }
    }
}
