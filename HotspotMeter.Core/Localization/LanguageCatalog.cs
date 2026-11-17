namespace HotspotMeter.Core.Localization;

/// <summary>
/// 中英文界面文案的唯一来源。放在 Core 而不是 App，是为了让测试能直接校验
/// 两套字典的键完全一致、没有空值或格式占位符不匹配——这类问题在运行时往往
/// 只在某个很少打开的提示框里才暴露。
/// <para>
/// 这里只放"用户可见文字"。日志、异常和诊断信息仍使用开发语言，避免为了翻译
/// 而改变排障时依赖的原始信息。
/// </para>
/// </summary>
public static class LanguageCatalog
{
    public const string ChineseSimplified = "zh-CN";

    public const string English = "en-US";

    public static IReadOnlyList<string> SupportedLanguages { get; } = [ChineseSimplified, English];

    /// <summary>把配置中的值规范化为受支持的语言；无法识别时返回 null。</summary>
    public static bool TryNormalize(string? value, out string language)
    {
        language = ChineseSimplified;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals(ChineseSimplified, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            language = ChineseSimplified;
            return true;
        }

        if (trimmed.Equals(English, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("en", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
        {
            language = English;
            return true;
        }

        return false;
    }

    public static string Normalize(string? value) =>
        TryNormalize(value, out var language) ? language : ChineseSimplified;

    /// <summary>取文案；键不存在时返回键本身，便于自检发现遗漏而不是显示空白。</summary>
    public static string Get(string? language, string key)
    {
        var table = Normalize(language) == English ? EnglishStrings : ChineseStrings;
        return table.TryGetValue(key, out var value) ? value : key;
    }

    public static IReadOnlyDictionary<string, string> For(string? language) =>
        Normalize(language) == English ? EnglishStrings : ChineseStrings;

    public static IReadOnlyCollection<string> Keys => ChineseStrings.Keys;

    private static readonly Dictionary<string, string> ChineseStrings = new(StringComparer.Ordinal)
    {
        ["App_AlreadyRunning"] = "HotspotMeter 已在运行。请从系统托盘图标打开设置或显示悬浮胶囊。",
        ["App_UnhandledTitle"] = "HotspotMeter 遇到错误",

        ["Settings_WindowTitle"] = "设置 — HotspotMeter",
        ["Settings_Title"] = "设置",
        ["Settings_Subtitle"] = "调整悬浮胶囊的外观、位置与启动行为，修改立即生效。",
        ["Settings_LanguageSection"] = "语言",
        ["Settings_LanguageName"] = "界面语言",
        ["Settings_LanguageHint"] = "选择整个程序使用的语言，切换后立即生效，并记住你的选择。",
        ["Settings_AppearanceSection"] = "悬浮胶囊外观",
        ["Settings_AppearanceHint"] = "四种尺寸均按 320 : 104 等比例缩放，内部布局与标准尺寸完全一致，修改立即生效。",
        ["Settings_PresetMini"] = "迷你 · 220 × 71.5",
        ["Settings_PresetSmall"] = "小型 · 280 × 91",
        ["Settings_PresetStandard"] = "标准 · 320 × 104",
        ["Settings_PresetLarge"] = "大型 · 400 × 130",
        ["Settings_Width"] = "宽度",
        ["Settings_Height"] = "高度",
        ["Settings_Apply"] = "应用",
        ["Settings_Dip"] = "DIP",
        ["Settings_SizeRangeHint"] = "宽度 220–480 DIP · 高度 71.5–156 DIP · 宽高比固定为 320 : 104",
        ["Settings_BehaviorSection"] = "悬浮胶囊行为",
        ["Settings_PinName"] = "位置固定",
        ["Settings_PinHint"] = "固定后禁止拖动，避免误触；随时可以解除。",
        ["Settings_AutoFitName"] = "自动适配任务栏",
        ["Settings_AutoFitHint"] = "按当前显示器 DPI 与任务栏实际厚度自动调整胶囊尺寸，位置仍由你拖动；不会自动停靠或寻找任务栏空隙。",
        ["Settings_ResetName"] = "恢复默认位置",
        ["Settings_ResetHint"] = "回到主屏右下角、任务栏上方，并保留当前尺寸。",
        ["Settings_ResetButton"] = "恢复默认位置",
        ["Settings_ProgramSection"] = "程序行为",
        ["Settings_ShowOnStartup"] = "启动时显示悬浮胶囊",
        ["Settings_ShowOnStartupHint"] = "关闭后启动时只保留托盘图标，后台监控仍然运行。",
        ["Settings_AutoStartName"] = "开机自动启动 HotspotMeter",
        ["Settings_StatsTaskbarName"] = "统计窗口出现在任务栏",
        ["Settings_StatsTaskbarHint"] = "控制独立流量统计窗口是否拥有任务栏按钮，与“自动适配任务栏”无关；关闭后仍可从托盘打开。",
        ["Settings_FooterNote"] = "关闭设置或统计窗口不会停止监控；退出程序请使用托盘菜单或胶囊右键菜单。",
        ["Settings_AllChangesSaved"] = "所有更改会立即应用并保存。",
        ["Settings_Done"] = "完成",
        ["Settings_PinStatusPinned"] = "已固定：胶囊不可拖动",
        ["Settings_PinStatusUnpinned"] = "未固定：可按住胶囊拖动",
        ["Settings_PinButtonPin"] = "固定位置",
        ["Settings_PinButtonUnpin"] = "解除固定",
        ["Settings_AspectRatio"] = "宽高比 {0:0.00}",
        ["Settings_TaskbarHintPinned"] = "已固定：位置锁定，自动定位不会移动窗口；尺寸仍会随任务栏高度变化。",
        ["Settings_TaskbarHintUnpinned"] = "首次开启会按任务栏高度调整尺寸，并放到屏幕底部偏左；之后位置以你的拖动为准，不会自动寻找空隙。",
        ["Settings_PositionUnknown"] = "尚未定位",
        ["Settings_Position"] = "屏幕坐标 {0:0}, {1:0} DIP",
        ["Settings_ErrorWidthNumber"] = "宽度必须是数字。",
        ["Settings_ErrorHeightNumber"] = "高度必须是数字。",
        ["Settings_ErrorWidthRange"] = "宽度需在 {0:0}–{1:0} DIP 之间。",
        ["Settings_ErrorHeightRange"] = "高度需在 {0:0.#}–{1:0.#} DIP 之间（宽度 {2:0} 对应 {3:0.#}）。",
        ["Settings_ErrorAspect"] = "宽高比需保持 320 : 104（约 3.08 : 1）。请把高度改为 {0:0.#}，或把宽度改为 {1:0.#}。",

        ["Startup_WriteFailed"] = "启动项写入失败：{0}",
        ["Startup_Registered"] = "已开启：登录 Windows 时会自动启动当前程序。",
        ["Startup_NotRegistered"] = "未开启。开启后会在当前用户登录时自动启动。",
        ["Startup_OtherPath"] = "启动项指向其他路径，未自动覆盖：{0}",
        ["Startup_ReadFailed"] = "读取启动项失败：{0}",
        ["Startup_ErrorNoPath"] = "无法确定当前程序路径。",
        ["Startup_ErrorNoRegistry"] = "无法打开当前用户启动项注册表。",
        ["Startup_ErrorOtherProgram"] = "同名启动项属于其他程序，未覆盖。请先手动处理该项。",
        ["Startup_ErrorNotOurs"] = "同名启动项不属于 HotspotMeter，未删除。",

        ["Taskbar_Status"] = "{0}任务栏 · {1:0}% · 工作区 {2:0}×{3:0} DIP{4} · 任务栏厚 {5:0.#} DIP → 胶囊 {6:0.#}×{7:0.#} DIP（{8}）",
        ["Taskbar_EdgeBottom"] = "底部",
        ["Taskbar_EdgeTop"] = "顶部",
        ["Taskbar_EdgeLeft"] = "左侧",
        ["Taskbar_EdgeRight"] = "右侧",
        ["Taskbar_EdgeUnknown"] = "未识别",
        ["Taskbar_AutoHide"] = " · 自动隐藏",
        ["Taskbar_DensityNormal"] = "标准排版",
        ["Taskbar_DensityCompact"] = "紧凑排版",
        ["Taskbar_DensityMinimal"] = "最简排版",
        ["Taskbar_DetectFailed"] = "未检测到任务栏：{0}",
        ["Taskbar_AnchorPinnedSkipped"] = "位置已固定，自动定位未执行。",

        ["Stats_WindowTitle"] = "流量统计 — HotspotMeter",
        ["Stats_Title"] = "流量统计",
        ["Stats_SubtitlePrefix"] = "本机热点网卡口径 · ",
        ["Stats_Today"] = "今日",
        ["Stats_Last7"] = "最近 7 天",
        ["Stats_DateTooltip"] = "按日期查询，格式 yyyy-MM-dd",
        ["Stats_Query"] = "查询",
        ["Stats_Refresh"] = "刷新",
        ["Stats_Download"] = "下载流量",
        ["Stats_Upload"] = "上传流量",
        ["Stats_ChartTitle"] = "最近 7 天",
        ["Stats_ChartHint"] = "每根柱子 = 下载（绿）+ 上传（蓝），鼠标悬停查看数值",
        ["Stats_ProgramsTitle"] = "各程序流量",
        ["Stats_Program"] = "程序",
        ["Stats_ColDownload"] = "下载",
        ["Stats_ColUpload"] = "上传",
        ["Stats_Total"] = "合计",
        ["Stats_Share"] = "占比",
        ["Stats_Retry"] = "重试",
        ["Stats_NoData"] = "无数据",
        ["Stats_Loading"] = "正在读取历史数据…",
        ["Stats_TargetUnresolved"] = "目标热点未解析",
        ["Stats_Target"] = "目标热点：{0}",
        ["Stats_Updated"] = "更新于 {0}",
        ["Stats_Footer"] = "数据来自本机热点会话检查点；不是运营商计费流量。",
        ["Stats_FooterNoDb"] = "尚未生成历史数据库：连接热点并保持程序运行后会出现记录。",
        ["Stats_ErrorHistory"] = "历史数据读取失败：{0}",
        ["Stats_ErrorRetry"] = "可点击刷新重试；后台监控不受影响。",
        ["Stats_DateFormat"] = "日期格式应为 yyyy-MM-dd，例如 2026-11-14。",
        ["Stats_FutureDate"] = "不能查询未来的日期。",
        ["Stats_RangeToday"] = "今日",
        ["Stats_RangeLast7"] = "最近 7 天",
        ["Stats_NoTargetPrograms"] = "未连接目标热点，无法确定逐应用统计属于哪张网卡。连接热点后点击刷新。",
        ["Stats_BarNoData"] = "无数据",
        ["Stats_BarNoCheckpoint"] = "当天没有检查点",
        ["Stats_BarDetail"] = "↓ {0}  ↑ {1}",
        ["Stats_ProgramsLoading"] = "正在读取逐应用数据…",
        ["Stats_ProgramsEmpty"] = "{0}没有可用的逐应用归属记录；系统可能尚未生成统计。",
        ["Stats_ProgramsSummary"] = "系统归属数据：{0} 项（{1}）；占比按列表合计；未归属差额 {2}{3}。",
        ["Stats_Coverage"] = "；仅 {0}/{1} 天有系统归属数据，占比不可跨日比较",
        ["Stats_Unattributed"] = "未归属流量（系统/服务）",
        ["Stats_Unavailable"] = "逐应用流量不可用：{0}",
        ["Stats_UnavailableShort"] = "逐应用流量不可用。",
        ["Stats_NoProfile"] = "未找到当前热点对应的 Windows 连接配置。",
        ["Stats_NoAttribution"] = "系统没有返回逐应用数据（可能尚未生成归属统计）。",
        ["Stats_AttributionSummary"] = "系统归属数据：{0} 项；未归属差额 {1}。",

        ["Capsule_Caption"] = "已使用流量",
        ["Capsule_MenuStats"] = "流量统计",
        ["Capsule_MenuResize"] = "调整大小…",
        ["Capsule_MenuExit"] = "退出程序",
        ["Capsule_MenuPin"] = "固定位置",
        ["Capsule_MenuUnpin"] = "解除固定位置",
        ["Capsule_Previous"] = "上次 {0}",
        ["Capsule_Download"] = "下载",
        ["Capsule_Upload"] = "上传",

        ["Tray_Stats"] = "流量统计",
        ["Tray_Settings"] = "设置",
        ["Tray_Show"] = "显示悬浮胶囊",
        ["Tray_Hide"] = "隐藏悬浮胶囊",
        ["Tray_Exit"] = "退出程序",

        ["Warning_RecoveryUnconfirmed"] = "检测到未确认的恢复流量：{0} 字节",
        ["Warning_DetectorFailure"] = "Wi-Fi 状态查询暂时失败，正在重试。",
        ["Warning_CounterFailure"] = "暂时无法读取网卡计数，正在重试。",
        ["Warning_ImplausibleDelta"] = "检测到异常大的流量增量，已保留数据并继续监控。",
        ["Capsule_WaitingForHotspot"] = "等待已连接的 Wi-Fi 热点…（后台每 5 秒重新检测）",

        ["Guard_AlertTitle"] = "流量提醒",
        ["Guard_AlertSecondaryTitle"] = "流量继续增长",
        ["Guard_AlertBody"] = "检测到 {0} 在{6}内累计使用 {1}（下载 {2}，上传 {3}），已达到 {4} 阈值。当前状态：{5}。",
        ["Guard_AlertSecondaryBody"] = "检测到 {0} 在{6}内流量继续增长，累计 {1}（下载 {2}，上传 {3}），已超过 {4} 阈值。当前状态：{5}。",
        ["Guard_AlertAction"] = "查看流量统计",
        ["Guard_PeriodSession"] = "本次连接",
        ["Guard_PeriodToday"] = "今天",
        ["Guard_PeriodRolling24h"] = "最近 24 小时",
        ["Guard_StateForeground"] = "前台",
        ["Guard_StateBackground"] = "后台",
        ["Guard_StateIdle"] = "空闲",
    };

    private static readonly Dictionary<string, string> EnglishStrings = new(StringComparer.Ordinal)
    {
        ["App_AlreadyRunning"] = "HotspotMeter is already running. Use the tray icon to open Settings or show the capsule.",
        ["App_UnhandledTitle"] = "HotspotMeter Error",

        ["Settings_WindowTitle"] = "Settings — HotspotMeter",
        ["Settings_Title"] = "Settings",
        ["Settings_Subtitle"] = "Adjust the capsule's appearance, position, and startup behavior. Changes apply immediately.",
        ["Settings_LanguageSection"] = "Language",
        ["Settings_LanguageName"] = "Interface language",
        ["Settings_LanguageHint"] = "Choose the language used throughout the app. The change applies immediately and is remembered.",
        ["Settings_AppearanceSection"] = "Capsule Appearance",
        ["Settings_AppearanceHint"] = "All four sizes keep the 320 : 104 ratio and the same internal layout. Changes apply immediately.",
        ["Settings_PresetMini"] = "Mini · 220 × 71.5",
        ["Settings_PresetSmall"] = "Small · 280 × 91",
        ["Settings_PresetStandard"] = "Standard · 320 × 104",
        ["Settings_PresetLarge"] = "Large · 400 × 130",
        ["Settings_Width"] = "Width",
        ["Settings_Height"] = "Height",
        ["Settings_Apply"] = "Apply",
        ["Settings_Dip"] = "DIP",
        ["Settings_SizeRangeHint"] = "Width 220–480 DIP · Height 71.5–156 DIP · Fixed aspect ratio 320 : 104",
        ["Settings_BehaviorSection"] = "Capsule Behavior",
        ["Settings_PinName"] = "Lock Position",
        ["Settings_PinHint"] = "Prevents accidental dragging while locked. You can unlock at any time.",
        ["Settings_AutoFitName"] = "Taskbar Adaptation",
        ["Settings_AutoFitHint"] = "Fits the capsule to the current monitor DPI and taskbar thickness. You still control its position; it does not dock or search for taskbar gaps.",
        ["Settings_ResetName"] = "Restore Default Position",
        ["Settings_ResetHint"] = "Returns the capsule above the taskbar in the lower-right corner of the primary display, keeping the current size.",
        ["Settings_ResetButton"] = "Restore Position",
        ["Settings_ProgramSection"] = "Application",
        ["Settings_ShowOnStartup"] = "Show capsule on startup",
        ["Settings_ShowOnStartupHint"] = "When off, only the tray icon appears at startup; background monitoring continues.",
        ["Settings_AutoStartName"] = "Start HotspotMeter with Windows",
        ["Settings_StatsTaskbarName"] = "Show statistics in taskbar",
        ["Settings_StatsTaskbarHint"] = "Controls whether the statistics window has a taskbar button. Independent of Taskbar Adaptation. It can still be opened from the tray.",
        ["Settings_FooterNote"] = "Closing Settings or Statistics does not stop monitoring. Use the tray or capsule menu to exit.",
        ["Settings_AllChangesSaved"] = "All changes are applied and saved immediately.",
        ["Settings_Done"] = "Done",
        ["Settings_PinStatusPinned"] = "Locked: the capsule cannot be dragged",
        ["Settings_PinStatusUnpinned"] = "Unlocked: drag the capsule by holding it",
        ["Settings_PinButtonPin"] = "Lock Position",
        ["Settings_PinButtonUnpin"] = "Unlock Position",
        ["Settings_AspectRatio"] = "Aspect ratio {0:0.00}",
        ["Settings_TaskbarHintPinned"] = "Locked: automatic positioning will not move the window. Its size still follows the taskbar height.",
        ["Settings_TaskbarHintUnpinned"] = "Turning this on fits the capsule to the taskbar and places it near the lower-left of the screen. Afterwards your drag position wins; it does not search for gaps.",
        ["Settings_PositionUnknown"] = "Not positioned yet",
        ["Settings_Position"] = "Screen position {0:0}, {1:0} DIP",
        ["Settings_ErrorWidthNumber"] = "Width must be a number.",
        ["Settings_ErrorHeightNumber"] = "Height must be a number.",
        ["Settings_ErrorWidthRange"] = "Width must be between {0:0} and {1:0} DIP.",
        ["Settings_ErrorHeightRange"] = "Height must be between {0:0.#} and {1:0.#} DIP (width {2:0} implies {3:0.#}).",
        ["Settings_ErrorAspect"] = "The aspect ratio must stay 320 : 104 (about 3.08 : 1). Set the height to {0:0.#}, or the width to {1:0.#}.",

        ["Startup_WriteFailed"] = "Could not update the startup entry: {0}",
        ["Startup_Registered"] = "Enabled: HotspotMeter starts when you sign in to Windows.",
        ["Startup_NotRegistered"] = "Disabled. When enabled, HotspotMeter starts when the current user signs in.",
        ["Startup_OtherPath"] = "The startup entry points to another path and was not overwritten: {0}",
        ["Startup_ReadFailed"] = "Could not read the startup entry: {0}",
        ["Startup_ErrorNoPath"] = "The current executable path could not be determined.",
        ["Startup_ErrorNoRegistry"] = "The current user's startup registry key could not be opened.",
        ["Startup_ErrorOtherProgram"] = "A startup entry with the same name belongs to another program and was not overwritten. Please handle it manually.",
        ["Startup_ErrorNotOurs"] = "The startup entry with the same name does not belong to HotspotMeter and was not removed.",

        ["Taskbar_Status"] = "{0} taskbar · {1:0}% · work area {2:0}×{3:0} DIP{4} · bar {5:0.#} DIP → capsule {6:0.#}×{7:0.#} DIP ({8})",
        ["Taskbar_EdgeBottom"] = "Bottom",
        ["Taskbar_EdgeTop"] = "Top",
        ["Taskbar_EdgeLeft"] = "Left",
        ["Taskbar_EdgeRight"] = "Right",
        ["Taskbar_EdgeUnknown"] = "Unrecognized",
        ["Taskbar_AutoHide"] = " · auto-hide",
        ["Taskbar_DensityNormal"] = "standard layout",
        ["Taskbar_DensityCompact"] = "compact layout",
        ["Taskbar_DensityMinimal"] = "minimal layout",
        ["Taskbar_DetectFailed"] = "Taskbar not detected: {0}",
        ["Taskbar_AnchorPinnedSkipped"] = "Position is locked; automatic placement was skipped.",

        ["Stats_WindowTitle"] = "Traffic Statistics — HotspotMeter",
        ["Stats_Title"] = "Traffic Statistics",
        ["Stats_SubtitlePrefix"] = "This PC's hotspot adapter · ",
        ["Stats_Today"] = "Today",
        ["Stats_Last7"] = "Last 7 Days",
        ["Stats_DateTooltip"] = "Query by date (yyyy-MM-dd)",
        ["Stats_Query"] = "Query",
        ["Stats_Refresh"] = "Refresh",
        ["Stats_Download"] = "Download",
        ["Stats_Upload"] = "Upload",
        ["Stats_ChartTitle"] = "Last 7 Days",
        ["Stats_ChartHint"] = "Each bar = download (green) + upload (blue). Hover for values.",
        ["Stats_ProgramsTitle"] = "Traffic by App",
        ["Stats_Program"] = "App",
        ["Stats_ColDownload"] = "Download",
        ["Stats_ColUpload"] = "Upload",
        ["Stats_Total"] = "Total",
        ["Stats_Share"] = "Share",
        ["Stats_Retry"] = "Retry",
        ["Stats_NoData"] = "No data",
        ["Stats_Loading"] = "Loading history…",
        ["Stats_TargetUnresolved"] = "Target hotspot not resolved",
        ["Stats_Target"] = "Target hotspot: {0}",
        ["Stats_Updated"] = "Updated at {0}",
        ["Stats_Footer"] = "Data comes from local hotspot session checkpoints, not carrier billing.",
        ["Stats_FooterNoDb"] = "No history database yet. Connect the hotspot and keep HotspotMeter running to create records.",
        ["Stats_ErrorHistory"] = "Could not load history: {0}",
        ["Stats_ErrorRetry"] = "Click Refresh to retry. Background monitoring is unaffected.",
        ["Stats_DateFormat"] = "Use the yyyy-MM-dd format, for example 2026-11-14.",
        ["Stats_FutureDate"] = "Future dates cannot be queried.",
        ["Stats_RangeToday"] = "Today",
        ["Stats_RangeLast7"] = "Last 7 Days",
        ["Stats_NoTargetPrograms"] = "The target hotspot is not connected, so per-app traffic cannot be attributed to an adapter. Connect it and refresh.",
        ["Stats_BarNoData"] = "No data",
        ["Stats_BarNoCheckpoint"] = "No checkpoints on this day",
        ["Stats_BarDetail"] = "↓ {0}  ↑ {1}",
        ["Stats_ProgramsLoading"] = "Loading per-app data…",
        ["Stats_ProgramsEmpty"] = "No per-app attribution records are available for {0}; Windows may not have generated statistics yet.",
        ["Stats_ProgramsSummary"] = "System attribution: {0} apps ({1}); shares are based on the listed total; unattributed difference {2}{3}.",
        ["Stats_Coverage"] = "; system attribution is available for only {0}/{1} days, so shares are not comparable across days",
        ["Stats_Unattributed"] = "Unattributed traffic (system/services)",
        ["Stats_Unavailable"] = "Per-app traffic is unavailable: {0}",
        ["Stats_UnavailableShort"] = "Per-app traffic is unavailable.",
        ["Stats_NoProfile"] = "No Windows connection profile matches the current hotspot.",
        ["Stats_NoAttribution"] = "Windows has not returned per-app attribution data yet.",
        ["Stats_AttributionSummary"] = "Windows attribution: {0} apps; unattributed difference {1}.",

        ["Capsule_Caption"] = "Data used",
        ["Capsule_MenuStats"] = "Traffic Statistics",
        ["Capsule_MenuResize"] = "Resize…",
        ["Capsule_MenuExit"] = "Exit",
        ["Capsule_MenuPin"] = "Lock Position",
        ["Capsule_MenuUnpin"] = "Unlock Position",
        ["Capsule_Previous"] = "Last {0}",
        ["Capsule_Download"] = "Down",
        ["Capsule_Upload"] = "Up",

        ["Tray_Stats"] = "Traffic Statistics",
        ["Tray_Settings"] = "Settings",
        ["Tray_Show"] = "Show Capsule",
        ["Tray_Hide"] = "Hide Capsule",
        ["Tray_Exit"] = "Exit",

        ["Warning_RecoveryUnconfirmed"] = "Unconfirmed recovered traffic detected: {0} bytes",
        ["Warning_DetectorFailure"] = "Wi-Fi status lookup failed temporarily; retrying.",
        ["Warning_CounterFailure"] = "Adapter counters are temporarily unavailable; retrying.",
        ["Warning_ImplausibleDelta"] = "An unusually large traffic delta was observed; data was kept and monitoring continues.",
        ["Capsule_WaitingForHotspot"] = "Waiting for a connected Wi-Fi hotspot… (retrying every 5 seconds)",

        ["Guard_AlertTitle"] = "Traffic alert",
        ["Guard_AlertSecondaryTitle"] = "Traffic keeps growing",
        ["Guard_AlertBody"] = "Detected {0} using {1} {6} (download {2}, upload {3}), reaching the {4} threshold. Current state: {5}.",
        ["Guard_AlertSecondaryBody"] = "Detected {0} continuing to grow {6}: {1} total (download {2}, upload {3}), above the {4} threshold. Current state: {5}.",
        ["Guard_AlertAction"] = "View traffic statistics",
        ["Guard_PeriodSession"] = "this connection",
        ["Guard_PeriodToday"] = "today",
        ["Guard_PeriodRolling24h"] = "in the last 24 hours",
        ["Guard_StateForeground"] = "Foreground",
        ["Guard_StateBackground"] = "Background",
        ["Guard_StateIdle"] = "Idle",
    };
}