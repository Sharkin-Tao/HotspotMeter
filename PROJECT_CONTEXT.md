# PROJECT_CONTEXT.md — HotspotMeter 知识库入口

> 每次开始任务先读本文件，再按任务读取对应文档。
> 本文件只做导航、状态与"事实/假设"清单，详细内容在 `docs/` 下。

## 1. 项目一句话

Windows 原生 WPF 工具，监控电脑连接手机 Wi-Fi 热点时的网卡流量：一个极简悬浮胶囊显示**当前会话**流量与实时速度，右键打开**独立统计窗口**查看日/周/月/历史数据。

## 2. 当前状态

| 项目 | 状态 |
| --- | --- |
| 当前阶段 | **Traffic Guard 第二阶段（规则引擎 + 智能提醒）已完成（2026-11-17），等待用户验收；尚未提交** |
| 代码状态 | Core：模型/接口/累积器/恢复判定/归属名称解析/中英语言资源 + `TrafficGuard`（身份、状态分类、增量计算、采样服务）+ `GuardRuleEngine`/`GuardAlertService`/`GuardAlertText`；Infrastructure：WLAN 检测、计数器、`MonitorEngine`、系统时钟、SQLite 基础设施与业务表 **v5**、`ProcessInspector`/`DevicePathMapper`/`GuardStore`（含提醒仓储与聚合）；App：胶囊 + 托盘 + 设置 + `TrafficHistoryStore` + `TrafficStatsService` + `StatsWindow` + 中英双语 + `GuardUsageSource`/`GuardRuntime` + `ToastNotificationSender` + Guard/提醒接线；发布见“阶段 7 实际结果”，Guard 见“Traffic Guard 第一/第二阶段实际结果” |
| 未实现 | 流量守护完整设置/规则/信任列表/历史 UI、结束进程、风险评分、防火墙/自动阻断；周/月汇总、程序图标提取、用户手动选择目标热点 UI、睡眠/唤醒专项编排、安装程序/自动更新/代码签名；发布产物为便携 ZIP |
| 下一次要做的事 | 用户验收 Traffic Guard 第二阶段：确认规则默认值（100 MB/自然日/上下行合计/仅后台/1 小时冷却/二次 50 MB）、提醒文案与通知观感；随后再决定守护设置页、信任/忽略 UI 与历史列表的产品形态。 |

### 阶段 0 实际验证结果（2026-11-12）

- 环境：Windows 11 专业版 10.0.26200（x64）/ .NET SDK 10.0.402 / `Microsoft.WindowsDesktop.App 10.0.13`。
- `dotnet build HotspotMeter.sln -c Debug`：**0 警告 0 错误**。
- `dotnet test`：**13/13 通过**，其中包含真实打开内存 SQLite 并执行 `SELECT sqlite_version()` 的冒烟测试；原生 `runtimes\win-x64\native\e_sqlite3.dll` 已被正确复制到输出目录。
- `dotnet run --project HotspotMeter.App`：进程启动后按设计立即退出，退出码 0（阶段 0 没有窗口）。
- `AGENTS.md` 已确认会被 DeepSeek Harness 自动加载（`dsh-agent-instructions` 会从项目根向下加载 `AGENTS.md`/`CLAUDE.md` 链）。

### 阶段 1 实际验证结果（2026-11-12，11/11 通过）

- 原型 `tools/ApiProbe/` 真实跑完 P1-1～P1-11，结论与数值见 `docs/05` 第 9 节、日志见 `docs/10`。
- 三个会直接影响产品的结论：①只能按 InterfaceGuid 取唯一行（按 Type=71 求和是真实值的 6 倍）；②会话流量必须累加 delta（计数器重置后与真实会话差 267,124 B）；③睡眠/接口拆除后必须重建基线并两帧确认（否则会出现约 23 MB 幻影流量）。
- 项目目录已迁移到 `E:\hotspotmeter\HotspotMeter`（见 `docs/09` D-025）。

### 阶段 2 实际结果（2026-11-12，58/58 测试通过）

- `TrafficAccumulator`：会话累计 = Σ 有效 delta；首帧与长暂停/回退后的两帧确认增量记 0；真断开/SSID 变化才结束会话，接口暂时缺失只暂停。
- `BaselineResumeEvaluator` + `MonitorEngine.ResumeAsync`：Tick 回退→Rebooted，目标不匹配→NotOnTarget，行缺失→InterfaceMissing，LUID 变化→InterfaceChanged，计数回退→CounterReset，开机偏移 >10 min→Rebooted，否则 Continued 算 gap。
- 恢复默认**不**自动入账：默认证据源恒 `Unverified`，只发 `RecoveryCandidateDetected`；只有 `Continuous` 才 `SessionResumed`。
- **F-1 会话边界**：Unverified/Interrupted 时旧会话在 `gap.FromUtc` 结束（`RecoveryUnconfirmed`），同帧以新 SessionId 开新会话（累计 0），gap 只作候选；其余非 Continued 结局在最后检查点结束旧会话（`RecoveryRejected` + 具体 `ResumeOutcome`）；同一 SessionId 只结束一次，Id 严格递增。
- 恢复幂等：`RecoveryId` 稳定，进程内 HashSet + 信号量，重复调用返回 `AlreadyApplied=true`。
- 真机 45 s（60 MB 下载）比对：引擎 61.6 MB/715.06 KB，系统计数差 64,637,304/746,352 B，差异 <0.1%。

### 阶段 3A 实际结果（2026-11-12，101/101 测试通过）

- 新增 `HotspotMeter.Infrastructure/Persistence/`：`DatabasePaths`、`SqliteConnectionFactory`、`SchemaMigrator`、`DatabaseHealth`、`DatabaseQuarantine`、`DatabaseBootstrapper`、`IDatabaseWriter`、`SqliteWriter`；`SingleInstance/SingleInstanceGuard`。
- Schema v1 只有 `SchemaInfo(Key, Value)`（`created_at_utc`/`initialized_at_utc`/`app_version`）；版本唯一权威是 `PRAGMA user_version`，高版本库拒绝写入。
- 连接：`Pooling=false`；每连接 `foreign_keys=ON`、`busy_timeout=5000`、`synchronous=NORMAL`（阶段 6 已改为 `FULL`），并校验 `journal_mode=wal`。
- 损坏处理：`quick_check` 失败 → 把 db/-wal/-shm/-journal 整体移入 `corrupt-{时间戳}`；隔离失败则抛异常且不创建新库。
- Writer：单消费者 FIFO、每项独立事务、`StopAsync` 排空后释放连接；**未**实现优先级/多队列/业务命令。
- 测试新增 43 例（58 → 101），解决方案与 MonitorHost 均 0 警告 0 错误。

### 阶段 4C-1 实际结果（2026-11-13，悬浮胶囊首版）

- 新增 App 层：`Theme.xaml`（色板 + 深色右键菜单样式）、`ViewModels/CapsuleViewModel.cs`、`Views/CapsuleWindow.xaml(.cs)`、`Services/WindowStateStore.cs`、`Services/MonitorSessionService.cs`、`Services/UiDump.cs`。
- `App.xaml.cs` 成为组合根：`ShutdownMode=OnExplicitShutdown`，先显示胶囊再启动 `MonitorEngine`，退出时最多等 3 s 停止引擎。
- 胶囊：320×104 DIP、圆角 20、无边框透明 / Topmost / 不占任务栏；绿/红实心背景；左上 Wi-Fi 图标（未连接叠加斜杠）+ 状态；左中 34 号会话流量 + “已使用流量”；右下 ↓/↑ 速度；右键菜单（流量统计/设置暂禁用、固定/解除固定、退出）。
- 位置：首次运行放在当前显示器工作区右下角；Win32 `MonitorFromWindow` + `GetMonitorInfo` 的物理矩形按 `VisualTreeHelper.GetDpi` 换算成 DIP，虚拟屏用 `GetSystemMetrics` 同样换算后再夹取；位置与固定状态存 `%LOCALAPPDATA%\HotspotMeter\ui-state.json`。
- 目标热点临时策略：取“当前已连接、信号最强”的 Wi-Fi（`MonitorSessionService` 注释已注明）；正式的用户选择留到设置阶段，不凭 SSID 猜测第二个热点。
- 验证：`dotnet build` 0/0；`dotnet test` 348/348；真机运行 DPI=1.5、窗口 320×104 DIP、显示 `已连接`、`5.38 KB`、`↓1.59 KB/s ↑1.6 KB/s`；`PrintWindow` 全分辨率截图 `docs/images/capsule-connected.png`（480×156 物理像素），文字带 y=25..45 / 63..99 / 116..133，右侧速度距右边缘 28 px（≈18.7 DIP）。
- 未验证：断开热点后的红色/“上次会话”状态（本轮未切断网络）、拖动与固定交互、跨屏/DPI 变化后的重新夹取、透明区域点击命中行为。
- 视觉验证限制：本会话视觉引擎不可用（`read_image`/`modlens` 均失败），改用 `HOTSPOTMETER_UI_DUMP` 诊断（DPI/窗口位置/每个 TextBlock 文本与边界）+ `PrintWindow` 截图；未用肉眼确认像素级观感。

### 阶段 4C-2 实际结果（2026-11-13，托盘 + 设置 + 尺寸自定义）

- 新增 `HotspotMeter.App/Services/`：`TrayIconService`（WinForms `NotifyIcon` + 深色自绘菜单）、`AppIcons`（pack URI 资源）、`DarkTitleBar`（DWM 20/19 深色标题栏）、`ISettingsHost`。
- 新增 `Models/CapsuleSize`（默认 320×104、小型 280×91、大型 400×130、范围 220–480 × 71.5–156、统一宽高比 320 : 104）；`ViewModels/SettingsViewModel` + `Views/SettingsWindow.xaml(.cs)`（外观/行为/程序三组卡片，单实例）。
- 胶囊菜单改为：流量统计（阶段 5 前禁用）/ 固定位置或解除固定位置 / 调整大小… / 退出程序；**设置入口只保留在托盘**。
- 尺寸用代码设置 `Window.Width/Height`，不用绑定——实测绑定会让窗口高度变成内容期望高度（320×744.7），见 D-053。
- 托盘菜单：设置 / 显示悬浮胶囊 / 隐藏悬浮胶囊 / 退出；隐藏只 `Hide()`，不停引擎；`Dispose` 时置 `Visible=false`。
- 图标：`tools/IconGen/generate-icon.ps1` 程序化生成（120° Wi-Fi 弧+圆点+梯形底座），输出多尺寸 DIB 帧 `.ico` 与 256px PNG。
- 设置窗口也记忆位置：`SettingsLeft/SettingsTop` 与胶囊状态同存 `ui-state.json`，恢复时按虚拟屏夹取；无字段时仍居中。
- 验证：`dotnet build HotspotMeter.sln` 0/0；`dotnet test` 348/348；开发期自检覆盖尺寸预设/自定义/越界夹取/固定文案/设置单实例/托盘点击打开设置/隐藏后引擎继续（快照 6→7）/重启恢复 400×130/设置窗口位置恢复。
- 未验证：真实鼠标拖拽与固定后禁拖（代码守卫，未自动化）、100%/125%/200% DPI 与多显示器（仅 150% 单屏实机）、托盘图标在通知区域的真实外观。

### 阶段 4C-3 实际结果（2026-11-13，四档等比例缩放 + 右键闪退修复）

- **缺陷根因（已复现并确认）**：`CapsuleWindow.xaml` 把 `TargetType=MenuItem` 的样式设成 `ContextMenu.ItemContainerStyle`，WPF 会把同一容器样式套到菜单里的 `Separator`，首次布局抛
  `InvalidOperationException: 用于类型"MenuItem"的样式不能应用于类型"Separator"`（堆栈 `ItemsControl.ApplyItemContainerStyle` ← `Panel.GenerateChildren` ← `Popup.CreateWindow` ← `ContextMenu.OnIsOpenChanged`）。修复为 `<ContextMenu.Resources>` 内的隐式 `MenuItem` 样式，`Separator` 走默认样式；没有吞异常，异常仍按原路径抛出。见 D-055。
- 新增 `Services/CrashLog.cs`：把未处理异常的完整堆栈写到 `%LOCALAPPDATA%\HotspotMeter\logs\crash-yyyyMMdd-HHmmss-fff.log`，`App` 注册 Dispatcher/AppDomain/UnobservedTask 三处；用于取证，不改变异常行为。
- `CapsuleSize` 改为四档统一比例：迷你 220×71.5 / 小型 280×91 / 标准 320×104 / 大型 400×130；自定义范围 220–480 × 71.5–156，宽高比容差 1.2%，缩放系数 `clamp(min(W/320, H/104), 0.65, 1.6)`；比例不符时提示期望高度/宽度。见 D-056。
- 缩放方式：沿用单一 `ApplyMetrics` 派生圆角 20s、内边距 18/15/18/13s、状态 13s、图标 17s、流量 34s/行高 40s、说明 11s、速度 11s、间距 7s/4s/10s；布局结构、元素与相对位置不变，没有单行化或裁切。
- 验证：自检对四档 + 自定义 380×123.5 逐一断言 `layout=ok texts=7`（所有文本在窗口内且任意两文本矩形不重叠）；尺寸实际为 220×71.5 / 280×91.3 / 320×104 / 400×130；右键菜单 `IsOpen` 连续三开三关无异常；菜单仍为四项且不含"设置"。
- 截图证据（150% DPI 原生像素，未缩放）：`docs/images/capsule-size-mini.png`、`capsule-size-small.png`、`capsule-size-standard.png`、`capsule-size-large.png`，以及并排对比图 `docs/images/capsule-size-comparison.png`。
- 环境限制（如实记录）：本会话用 `SendInput` 合成鼠标事件无法送达该窗口（拖拽与右键都不响应，UIA 确认窗口 `IsEnabled=True`、非 offscreen），因此"真实鼠标"路径只能由用户手动验收；菜单崩溃修复的证据是同一 `ContextMenu.OnIsOpenChanged` 代码路径下的三开三关 + 修复前后崩溃日志对比。

### 阶段 4C-4 实际结果（2026-11-13，任务栏自动检测 + 胶囊自适应）

- 新增 `HotspotMeter.Core/Layout/TaskbarLayout.cs`：纯函数把"显示器矩形 / 工作区 / 任务栏矩形 / 边缘 / 自动隐藏 / DPI"换算成 DIP 放置结果（位置、宽高、缩放系数、放置类型、边缘、说明）。规则：`scale = min(1, 可用宽/(320·dpi), 可用高/(104·dpi))`；`scale ≥ 0.6875` 时贴任务栏并保持 320:104 比例，否则退到对侧角使用最小尺寸 220×71.5；连最小尺寸都放不下时标记 `MinimumForced`。见 D-059。
- 新增 `HotspotMeter.App/Services/TaskbarDetector.cs`：`SHAppBarMessage(ABM_GETTASKBARPOS/ABM_GETSTATE)` + `MonitorFromWindow`/`MonitorFromRect` + `GetMonitorInfo` + `GetDpiForMonitor`（失败退 `GetDpiForWindow`）；失败回退主屏工作区推断并记录来源，不抛异常。见 D-058。
- **真实缺陷**：`GetMonitorInfo` 的 P/Invoke 原用 `out` 且未设置 `cbSize`，恒返回 false，所有检测结果实际来自兜底；改为 `new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }` + `ref info`（D-058）。修复后真机检测到 `底部任务栏 · 150% · 工作区 1707×1019 DIP（SHAppBarMessage(ABM_GETTASKBARPOS)）`。
- `CapsuleWindow`：监听 `WM_SETTINGCHANGE(0x1A, wParam=0x2F)` / `WM_DISPLAYCHANGE` / `WM_DPICHANGED`，600 ms 防抖后重排；固定状态暂停自动移动；拖动或手动改尺寸会关闭自动适配；关闭自动适配恢复开启前的手动尺寸与位置（`Manual*` 字段，见 D-060）。
- 设置窗口"悬浮胶囊行为"卡片新增"自动适配任务栏"开关、当前检测结果、固定时暂停提示；开关状态写入 `ui-state.json`（旧文件无该字段时按关闭处理）。
- 验证：`dotnet test` 364/364（新增 12 个布局单元测试）；自检 `artifacts/ui-selftest18/selftest.txt` 中合成四边期望与实际完全一致（底部 1378.7,912 / 顶部 1378.7,50.7 / 左侧 50.7,8 / 右侧 1336,8），真实检测停靠 1378.7,906.7，固定状态重排不动，关闭自动适配恢复 400×130 手动尺寸。
- 未验证：真实多显示器混合 DPI、真实任务栏自动隐藏、真实 100/125/200% 缩放；合成坐标超出本机虚拟屏时会被夹取，不能当作多屏实测证据。
- 截图证据：`docs/images/capsule-autofit-bottom.png`、`docs/images/settings-window-autofit.png`。

### 阶段 4C-5 实际结果（2026-11-13，速度竖排 + 深色滚动条/菜单 + 遮挡修复）

- 胶囊速度区从"右下角横排"改为"交通流量数字右侧、上下两行竖排"：下载在上、上传在下，各自带 ↓/↑ 方向符、标签和实时数值；两行放在共享列宽的 `Grid` 里，避免不同字宽时标签与数值重叠。见 D-061。
- 四档尺寸与自定义范围不变，全部视觉量仍由同一缩放系数派生；自检对迷你/小型/标准/大型逐一输出 `layout=ok texts=9` 与 `speedBlock ... stacked=True inside=True verdict=ok`。
- 设置窗口滚动条改为自绘深色模板：轨道 `#1C1C20`、滑块 `#3A3A3F`（悬停 `#4A4A50`、拖动 `#5C5C63`）、宽 10 DIP、圆角 3、保留 PageUp/PageDown 与拖动/滚轮行为。见 D-062。
- 胶囊右键菜单白色区域根因：`ContextMenu` 自身仍使用系统默认模板。新增 `CapsuleContextMenuStyle` 替换整个模板（圆角 8、`#1F1F23` 背景、`#3A3A3F` 1 DIP 边框），`Separator` 单独深色模板；自检连续开关 10 次无异常，菜单背景/边框实测 `#FF1F1F23` / `#FF3A3A3F`。见 D-063。
- 任务栏适配"没效果"的根因不是没执行，而是旧算法把胶囊右对齐放回与手动默认几乎相同的位置，且 `MaxScale=1` 不改变尺寸；现改为靠近任务栏的左/上侧停靠，并让自动适配结果与手动位置明显区分。见 D-064。
- 遮挡修复：`ClampCurrentPosition` 从"虚拟屏幕"改为"当前显示器工作区"，拖动结束也夹取并重新置顶；置顶改用 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE|NOMOVE|NOSIZE)`，不再用 `Topmost=false→true` 抢焦点。见 D-065。
- 多屏检测缺陷：`TaskbarDetector` 原总是取"任务栏所在显示器"，副屏窗口会被算到主屏；改为以窗口所在显示器为准（`MonitorFromWindow`），仅在任务栏矩形属于该显示器时采用。见 D-066。
- 验证：`dotnet build` 0/0、`dotnet test` 364/364；自检 `artifacts/ui-selftest21/selftest.txt` 合成四边/200%/副屏 expected 与 actual 完全一致、真实检测停靠 (8, 902.7)、`reAdapt` 无异常、手动尺寸保护 400×130 恢复、设置滚动条 `width=10 visible=True thumbFill=#FF3A3A3F`。
- 截图：`docs/images/capsule-size-*.png`（四档新布局）、`capsule-speed-layout.png`、`capsule-autofit-bottom.png`、`settings-window-dark-scrollbar.png`、`context-menu-dark.png`。
- 未验证：真实鼠标右键/拖动、点击其他程序后任务栏遮挡是否彻底消失（用 `SetWindowPos` 置顶逻辑，仍需人工确认）；多显示器混合 DPI 与自动隐藏任务栏仍只有合成/单元测试覆盖。

### 阶段 4C-6 实际结果（2026-11-13，速度块与流量数字垂直中心对齐）

- 需求：右侧速度块整体的垂直中心必须等于**流量数字控件**的中心（不是"数字 + 已使用流量"整块，也不是窗口中心），四档与自定义尺寸都要成立，且不得用固定 Margin 模拟。
- 实现：`CapsuleWindow.xaml` 的 Row1 改为两行两列 `Grid`；`TrafficValue`（新增名称）与 `SpeedBlock` 同处第 0 行且都 `VerticalAlignment="Center"`，行高由较高者决定，中心由布局保证相等；说明文字独占第 1 行，不再参与对齐。速度块 `UseLayoutRounding="False"` 消除 150% 下 0.33 DIP 舍入误差。见 D-067。
- 验证：新增 `UiDump.CheckSpeedAlignment`，输出 `trafficCenterY / speedCenterY / delta`；四档 + 自定义全部 `delta=0`：
  迷你 37/37、小型 48/48、标准 53.33/53.33、大型 67.67/67.67、自定义 64.33/64.33；每档同时 `layout=ok texts=9`。
- 构建 0/0、测试 364/364；自检 `artifacts/ui-selftest22b/selftest.txt`；截图 `docs/images/capsule-speed-aligned.png` 等。
- 未验证：视觉引擎不可用，未做人眼像素比对；仅 150% 单屏实机，100%/125%/200% 未在真实缩放下跑过。

### 阶段 4C-7 实际结果（2026-11-13，任务栏内部显示可行性验证）

- 调研：微软 `Taskbar Extensions` 文档只覆盖 Jump List、缩略图工具栏、图标叠加、进度条、通知区域；
  DeskBand 是旧机制且需把 COM DLL 加载进 Explorer；没有公开的第三方任意 UI 嵌入任务栏接口。
  结论写 `docs/09` D-068。
- 实验：独立项目 `tools/TaskbarOverlayProbe` 把置顶透明窗口放进任务栏矩形内部（本机 150% DPI、
  任务栏高 72 px，探针 197×64 px 位于 `(249,1532)`）；`WindowFromPoint` 命中探针、z 序高于任务栏，
  合成点击/拖拽/右键菜单可用，切到 `Progman` 后仍在上方。
- 稳定性实测：Explorer 重启会重建任务栏（`TaskbarCreated`、新 hwnd），旧层级失效后约 0.8 s 内被盖住，
  必须重检测 + `SetWindowPos(HWND_TOPMOST)` 才能恢复；UIA 空隙枚举也不稳定（37 个元素时无空隙，
  过滤可见按钮后为 237..781），因此不具备"无需维护、稳定"的公开契约。
- 决策：不集成到产品；继续使用独立置顶窗口 + 任务栏附近自动停靠，不宣称任务栏内嵌。产品代码未改，
  `dotnet build` 0/0、`dotnet test` 364/364；证据 `artifacts/taskbar-probe-6/probe.log`、
  `docs/images/taskbar-overlay-probe.png`；未 commit/push。
- 未验证：真实任务栏自动隐藏、多显示器、混合 DPI 与真实鼠标操作。

### 阶段 4C-8 实际结果（2026-11-13，自动适配只改尺寸 + 胶囊 UI 精简）

- 自动适配不再计算位置、不扫描任务栏按钮、不停靠：开启后只按当前显示器的 DPI 与任务栏厚度调尺寸，
  `Left/Top` 始终由用户拖动决定；关闭恢复开启前的手动尺寸（`Manual*` 备份），两种模式互不污染。
  见 D-069。
- 新增 `Core/Layout/TaskbarSizing.cs`：高度 = clamp(任务栏厚度 − 4, 40, 104) DIP，宽度 = 高度 × 320/104
  （工作区宽减 16 封顶，最小 132）；密度 ≥88 正常 / ≥56 紧凑 / <56 最简（隐藏"下载/上传"标签，保留
  ↓/↑ 与数值）；厚度 <44 DIP 时 `FitsTaskbar=false` 并在说明中写明低于可读下限。见 D-070、D-072。
- 胶囊删除 Wi-Fi 图标、连接状态文字与"已使用流量"说明；标准档流量字号 34→46、速度 11→14，数字放进
  `Viewbox` 只缩不裁；速度仍下载在上、上传在下，速度块中心仍与流量数字控件中心对齐（偏差 ≤0.33 DIP）。
  见 D-071。
- 验证：`dotnet build` 0/0、`dotnet test` 376/376（新增 12 个尺寸算法测试）；UI 自检
  `artifacts/ui-selftest24/selftest.txt`：四档 + 自定义 `layout=ok texts=7`、`simplified ok=True`；
  真实任务栏检测 `48 DIP → 135.4×44（最简排版）`，位置保持 (240,240)；8 组合成几何 expected==actual 且
  `moved=False`；固定时 `sizeChanged=True` 而位置不变；手动 400×130 → 自动 → 关闭恢复 400×130；
  设置开关 `persisted=True/False`；设置窗口单实例、深色滚动条、三条校验文案正常。
- 已知取舍：48 DIP 任务栏下速度字号 8.5 DIP 且隐藏"下载/上传"标签；低于 44 DIP 只保证不裁切，不保证可读。
- 未验证：真实 100%/125%/200% DPI、多显示器混合 DPI、纵向/自动隐藏任务栏、真实鼠标拖入任务栏与置顶效果。
- Git：本次改动与 4C-1～4C-7 的未提交改动混在同一批文件（`CapsuleWindow.xaml.cs`、`CapsuleViewModel.cs`、
  `Theme.xaml`、`SettingsWindow.xaml`、`UiDump.cs`、`App.UiSelfTest.cs` 等），无法安全拆分，故未 commit/push。

### 阶段 4C-9 实际结果（2026-11-13，自动适配的拖动边界、自动定位与层级恢复）

- 自动适配开启时，拖动边界从 `工作区 + 8 DIP` 改为 `显示器物理边界 + 0 DIP`：胶囊可以进入任务栏矩形并在其中自由拖动，不再被弹回工作区。普通模式仍使用工作区 + 8 DIP 夹取。见 D-075。
- 首次开启 / 再次开启自动适配时，按当前显示器自动定位到底部任务栏偏左：`X = 显示器左边界 + 120 DIP`（夹在左右各 8 DIP 内），`Y = 任务栏顶 + (任务栏高 − 胶囊高)/2`（上限为显示器底 − 胶囊高，**垂直不扣 8 DIP**）。仅支持底部任务栏；其它方向返回不支持并记录原因，不擅自换边。见 D-074。
- 自动定位只在“开关从关到开”时执行；启动时只恢复保存位置并做边界夹取，不覆盖用户上次拖动位置；拖动结束重算尺寸（不改位置）并持久化；固定状态下只锁定位置，尺寸仍随任务栏高度变化。见 D-076。
- 层级：`EnsureTopmost()` 用 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE|NOMOVE|NOSIZE)`；不改 Explorer、不注入、不注册 AppBar；`TaskbarCreated` 只做一次重检测 + 重定位 + 置顶，并写入 `%LOCALAPPDATA%\HotspotMeter\logs\app.log`（新增 `AppLog`，1 MB 轮转）。见 D-077。
- 验证：`dotnet build` 0/0、`dotnet test` **385/385**（新增 9 个 `TaskbarAnchorCalculatorTests`）；自检 `artifacts/ui-selftest26/selftest.txt`：首次开启 `left=120 top=1020.7 insideTaskbar=True`，拖入任务栏后 `(300,1021.3)` 不被弹回，关闭后同位置被夹回工作区 `(300,906.7)`，再次开启重新定位 `(120,1020.7)`，`TaskbarCreated` 后位置不变；四档/合成几何/固定/设置开关/重启恢复全部通过。日志记录 `monitor 0,0,2560,1600 / taskbar 0,1528,2560,1600 / dpi 1.5 → 120,1020.7`。
- 真实桌面验证限制：`SendInput` 仍无法驱动该窗口的真实鼠标拖拽；"拖入任务栏"用 `DragEndForTest` 走同一 `OnDragFinished` 路径模拟，不能替代用户肉眼确认。任务栏激活后的遮挡、Explorer 重启的真实恢复仍需人工验收。
- 启动恢复顺序（提交后复验修正）：`OnSourceInitialized` 先 `ApplyAutoFitSizeOnly()` 再 `RestorePosition()`。否则持久化宽度 135.4 会被回退成 Mini 220×71.5，夹取得到 `1066.7 − 71.5 = 995.3`，保存的 1020.7 被改写。修正后同一 state 重启实测 `left=120 top=1020.7`，截图绿色区域 abs y=1531..1596（任务栏 1528..1600）。见 D-078。
- Git：本次按用户明确授权，将 4C-1～4C-9 的全部产品代码与文档改动统一提交（不含构建产物、日志、临时目录），未 push；启动恢复修复以 amend 追加到同一提交。

### 阶段 4C-10 实际结果（2026-11-13，说明标签移至流量数字上方）

- `CapsuleWindow.xaml` 改为两行：第 0 行"已使用流量"（`TextSecondary`、11×s、下边距 2×s、水平居中），第 1 行流量数字（同列水平居中）与速度块（右列，`VerticalAlignment=Center`）。速度块与数字同处一行，4C-6 的中心对齐不变。
- `CapsuleViewModel` 新增 `CaptionFontSize`、`CaptionMargin`、`ShowCaption`；普通四档/自定义尺寸与自动适配 Normal 密度显示标签，Compact/Minimal 因高度不足隐藏（44/56 DIP 放不下第三行文字）。
- 需要明确：4C-8 精简版曾删除该标签；本次按用户新要求重新加入并置于上方，属于对旧决定的有意覆盖，Wi-Fi 图标/连接状态仍保持删除。
- 验证：`dotnet build` 0/0、`dotnet test` **385/385**；自检 `artifacts/ui-selftest27/selftest.txt`：四档+自定义+长文本 `layout=ok texts=8`，`simplified ok=True captionAbove=True captionCentered=True`，速度对齐 delta ≤0.33；自动适配 8 组几何与定位/固定/重启路径全部通过。
- 实机标准档：标签 `85.3,16.7,54.7×14.7`、数字 `55.3,33.3,114.7×54`，间距 1.9 DIP，水平中心均为 112.65；速度块中心 60.3 与数字中心一致。见 `docs/images/capsule-caption-above.png`。
- 未验证：真实鼠标交互、视觉引擎目检（仍不可用，只能依赖坐标与像素统计）。

### 阶段 4C-11 实际结果（2026-11-13，标签与数字统一字体、左对齐）

- `CapsuleWindow.xaml` 左侧改为单个 `StackPanel`：`TrafficCaption` 在上，内层 Grid（数字 + 速度块）在下；标签与数字都 `HorizontalAlignment=Left`，左边缘由同一容器基准决定，不使用 Margin/绝对定位模拟。
- 新增 `TrafficFontFamily` 资源（`Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI`），标签与数字共用；标签 `FontWeight=SemiBold`、字号 11×s、`TextSecondary`，数字 46×s、SemiBold、White 均未改。
- 速度块仍与数字同处内层 Grid 同一行、`VerticalAlignment=Center`，位置/对齐与 4C-10 一致。
- `UiDump` 断言改为 `captionLeftAligned`（左边缘差 ≤0.5）与 `captionFontMatch`（`FontFamily.Source` 相等）。
- 验证：`dotnet build` 0/0、`dotnet test` **385/385**；`artifacts/ui-selftest28/selftest.txt`：四档+自定义+长文本 `layout=ok texts=8`，`captionAbove=True captionLeftAligned=True captionFontMatch=True`，速度 delta 0（自动 Minimal 0.33）；实机标准档标签与数字 x 均为 18，间距 1.9 DIP，速度块中心 60.3 = 数字中心。
- 未验证：字体渲染观感需人工确认；不同 DPI 下字形 bearing 的亚像素差异未单独测量。

### 阶段 4C-12 实际结果（2026-11-13，说明标签透明度）

- `CapsuleWindow.xaml` 仅在 `TrafficCaption` 上加 `Opacity="0.65"`；颜色仍 `TextSecondary #A1A1A6`，字体、字号 11×s、SemiBold、位置、左对齐均未改。`TrafficValue`、`SpeedBlock`、外层 `StackPanel` 的 `Opacity` 保持 1.0。
- `UiDump.CheckSimplifiedContent` 新增 `captionOpacity`、`trafficOpacity`、`opacityIsolated`，同时校验父容器不透明；判定纳入 `ok`。
- 验证：`dotnet build` 0/0、`dotnet test` **385/385**；`artifacts/ui-selftest29/selftest.txt`：四档+自定义+长文本 `layout=ok texts=8`、`captionOpacity=0.65 trafficOpacity=1.00 opacityIsolated=True`，速度 delta 0（自动 Minimal 0.33），自动适配/定位/固定/设置/重启全部通过。
- 对比度实测：绿底合成 `#739A82` 为 1.34:1，红底合成 `#AE7577` 为 1.55:1；实心灰原本为 1.63:1 / 2.25:1。这是"装饰性次级文字"的刻意弱化，不宣称满足 WCAG AA。
- 未验证：65% 在 11 px 下的实际可辨性需人工目检；视觉引擎仍不可用。

### 阶段 4C-13 实际结果（2026-11-13，标签与数字统一字体）

- 根因经本机字体表实测：`Microsoft YaHei UI` 无 SemiBold（Light/Normal/Bold），`Segoe UI Variable Display` 有 SemiBold 但不含 CJK。原资源以 Segoe 开头，中文逐字符回退雅黑、数字留在 Segoe；标签 SemiBold 在雅黑被映射为 Bold，数字是真 SemiBold，造成“标签更粗、两套字形”。
- 修复：`TrafficFontFamily` 改为 `Microsoft YaHei UI, Microsoft YaHei`；`TrafficCaption` 与 `TrafficValue` 均显式 `FontWeight=Bold`；字号、间距、`Opacity=0.65`、左对齐与速度区未动。
- `UiDump` 新增 `captionWeightMatch/captionStyleMatch/fontCoversCjk/fontCoversDigits`，用 `Typeface.TryGetGlyphTypeface(...).CharacterToGlyphMap` 验证 `已/流/0/B` 同属一个字体文件。
- 验证：`dotnet build` 0/0、`dotnet test` **385/385**；`artifacts/ui-selftest30/selftest.txt`：四档+自定义+长文本 `layout=ok texts=8`，`captionFontMatch/WeightMatch/StyleMatch=True`、`fontCoversCjk/Digits=True`，速度 delta ≤0.33，自动适配/定位/固定/设置/重启全部通过。
- 实机标准档：标签 `x=18,y=19.3,54.7×14`，数字 `x=18,y=35.3,183.3×48.7`（`18.26 KB`），间距 2.0 DIP；数字同字号下比 Segoe 更宽、行高更矮。
- 未验证：雅黑 Bold 在 11 px + 65% 下的观感需人工目检；视觉引擎仍不可用。

### 阶段 4C-14 实际结果（2026-11-13，自动适配字号放大）

- 实现：`CapsuleViewModel.AutoFitTrafficFontScale = 1.18`；`ApplyDensityMetrics` 对流量字号与行高统一乘倍率，普通 `ApplyNormalMetrics` 保持 1.0。最简密度水平内边距 7→5、与速度区间距 6→4，把省下的 6 DIP 让给数字，避免 `Viewbox` 因列宽不足把放大抵消。
- 修复隐性缺陷：`SetSize` 的提前返回原先只看“密度为 Normal 且尺寸相同”，无法区分自动 Normal 与手动 320×104，关闭自动适配时倍率残留（实测 54.3/1.18）。条件补充 `TrafficFontScale == 1.0` 后恢复 46/1.00。
- 诊断：`UiDump.DescribeTrafficValue` 输出声明字号、行高、渲染矩形与 `renderScale`；`App.UiSelfTest` 在真实/合成/关闭三处记录 `trafficFont/fontScale`。
- 验证：`dotnet build` 0/0、`dotnet test` **385/385**；`artifacts/ui-selftest34/selftest.txt`：48 DIP 任务栏 `trafficFont=18.9 fontScale=1.18 renderScale=1.000`、数字 72×22.7、`layout=ok texts=8`；40 DIP 档仍不越界；速度中心差 ≤0.33；关闭后 `trafficFont=46 fontScale=1.00`；四档/自定义字号 31.6/40.3/46/57.5 不变。
- 限制：紧凑/正常密度下数字列同时受胶囊宽度与速度区挤压，7–8 字符长数值的 `renderScale` 为 0.56–0.83，声明字号变大但渲染尺寸接近原值；只有最简档（48 DIP 任务栏）完整实现 18%。

### 阶段 5 实际结果（2026-11-14，任务栏层级 + 启动管理 + 统计窗口）

- **5.1 任务栏层级**：`TaskbarForegroundWatcher` 已被 `ShellForegroundWatcher` 取代（旧文件删除），不再只匹配三个 Shell 类名，任何前台变化都触发；为避开任务栏随后的抬升动作，改为**每次前台事件只安排一次 250 ms 后的重断言**，仍无轮询。实机探针 `artifacts/shell-zorder-watcher2.log`：开始菜单展开时屏幕绿色像素 0（`bandCapsule=1`、`bandShell=6`，`SetWindowPos` 返回成功但无法跨 band）；Esc 关闭 1.5 s 后**未手工重断言**即恢复 `green=11309`、胶囊 z=16、任务栏 z=23；点击任务栏空白后 `green=11267`、胶囊 z=16。自检 `topmost.watcher=True count=0->1 afterDisable=False`（模拟类名 `Chrome_WidgetWin_1`，证明非 Shell 类名也走同一路径）。
- **5.2 启动管理**：`StartupRegistrationService` 直接读写 HKCU Run（值名 `HotspotMeter`，带引号 exe 路径），默认不注册；自检实测 `NotRegistered → RegisteredForCurrentPath → 再次 Enable 仍单条 → Disable → NotRegistered`。`ShowStatsInTaskbar` 为 `bool?`，缺失按 true，显式 false 保留；自检实测设置即时作用于统计窗口。
- **5.3 历史与逐程序**：新增 `TrafficHistoryStore`（10 s 检查点、稳定 ProfileKey、跨午夜切分、OperationId 去重、每日 SessionCount +1、启动时把遗留 Active 会话按最后检查点收尾）；`MonitorSessionService` 用串行任务链保证“先会话后快照”。逐程序走 WinRT `GetAttributedNetworkUsageAsync`（非打包进程可用），按 SSID+adapter 选 profile，桌面程序名称从路径派生，空归属单列“未归属流量（系统/服务）”，合计差额只展示不分摊。
- **5.4 统计窗口**：托盘“流量统计”单实例打开，980×700 深色，今日/最近 7 天/指定日期、堆叠柱、程序排行（名称/上下行/总量/占比，分母为列表合计）、刷新与错误/不可用状态；关闭窗口后引擎继续采样（自检 `5->6`）。
- **实测数据**：数据库 6 个会话、当日 `SessionCount=6`；`ConnectionSessions` 汇总 `10,431,015 / 14,377,165 B` 与 `DailyTraffic` 完全一致；快照分段求和一致。统计窗口真实显示“今日 23.66 MB”，程序 67 项，状态注明未归属差额 3.13 MB。
- **验证**：`dotnet build` 0/0；`dotnet test` **390/390**（新增 5 个归属名称解析测试）；运行时自检 `artifacts/stage5-selftest2.txt`；统计窗口布局 `artifacts/stage5-stats3.txt`（`layout=ok texts=43`）与截图 `artifacts/stage5-stats3.png`。
- **限制**：置顶无法绝对覆盖 UAC/安全桌面等系统界面；开始菜单/搜索展开期间 Shell 处于更高窗口 band（诊断读数 6），普通 `HWND_TOPMOST` 无法覆盖，关闭后才恢复；任务栏仅“显示/隐藏”而无前台变化时不会触发重断言；Windows 归属 API 有延迟且桌面程序 `AttributionName` 为空；逐程序数据未入库（每次按日期查询）；程序图标暂未提取；统计窗口周/月汇总未实现；未做 24 h 长跑与真实 Explorer 重启的人工验收。
- **5.1 附加 UI（统计窗口标题栏）**：`StatsWindow` 在 `SourceInitialized` 调用现有 `DarkTitleBar.Apply`，标题栏 `#0B0B0C`、边框 `#26262A`、标题 `#F5F5F7`；实机屏幕采样（物理 1470×100）主色 `rgb(11,11,12)`，亮色文字/按钮像素 877；未改窗口尺寸、布局与三个系统按钮行为。

### 阶段 6 实际结果（2026-11-14，发布前审查与加固）

- **审查方式**：4 个只读子代理分别审查 App 生命周期/资源、持久化/数据、监控/Win32、统计 UI/测试；主代理逐项复核并只实施低风险修复，不改 Schema、不清数据、不改默认行为。
- **P0 修复**：退出路径 `App.xaml.cs` 原在 UI 线程同步 Wait，`MonitorSessionService.DisposeAsync` 的续体又要求 UI 线程 → 固定卡 3 秒且释放不完整。改为 `Task.Run(...DisposeAsync()).Wait(3s)`，`DisposeAsync` 内全部 `ConfigureAwait(false)`、无条件释放历史写入器；运行时自检 7.4 s 内正常退出，`app.log` 无“退出时监控释放超时”。
- **数据正确性修复**：
  - 统计查询原 `SUM(DailyTraffic)` 未按 `ProfileKey` 过滤，会把用户连过的所有热点混加。现在 `TrafficStatsService.GetDailyAsync/GetDayAsync/GetLastSevenDaysAsync` 都按当前目标热点的 `ProfileKey` 过滤；无目标/无库时返回完整日期轴并标注无数据。
  - 逐程序 `FindProfile` 改为 adapter GUID 与 SSID 都非空且精确匹配，缺一返回不可用，避免取“列表第一个配置”张冠李戴。
  - `TrafficHistoryStore` 启动清扫把遗留 Active 会话的映射标为 `MappingStatus=3`（Abandoned），不再冒充正常断开；连接时长在快照间隔 >30 s 或目标未连接时记 0，避免睡眠被算成 3 小时连接；字节仍按增量完整计入。
- **数据库健康**：`DatabaseHealth` 区分可隔离与暂时占用；BUSY/LOCKED(5/6) 与 IOERR(10) 不再隔离，CANTOPEN(14) 仍尝试隔离（文件真被占用时 Move 会失败并抛 `DatabaseQuarantineException`，不会静默清空）；`DatabaseBootstrapper` 在不可隔离时抛异常保留原文件；结果带 `CanQuarantine`，`TrafficHistoryStore` 写日志。
- **持久性**：`SqliteConnectionFactory` 改 `synchronous=FULL`，与“断电最多丢最后一个检查点”的口径一致；连接工厂测试同步更新。
- **统计窗口并发**：`StatsViewModel` 所有可见写入加版本守卫，旧查询不能覆盖新结果；`StatsWindow` 关闭时 `Cancel()` 取消在途加载；WinRT 查询透传 `CancellationToken`；程序列表说明“仅 N/7 天有归属数据、占比不可跨日比较”。
- **UI/交互**：胶囊右键“流量统计”从禁用改为真正打开单实例统计窗；设置窗口按工作区夹取尺寸、激活时若正在编辑宽高则不覆盖输入；`ui-state.json` 改临时文件 + `File.Replace` 原子写，损坏文件改名 `.bad-*` 并记日志；托盘菜单字体改用系统字体（不再泄漏 GDI 对象），并用 `HWND_MESSAGE` 监听 `TaskbarCreated` 重新挂载图标。
- **启动/重连**：目标热点未识别时每 5 秒重试（仅定时器，非高频轮询），识别成功后停止；启动失败会清理半初始化的引擎，避免重试被 `_engine != null` 永久挡住。
- **运行时验证**：Debug 启动后 `TrafficSnapshots` 从 314 增至 315、会话字节与 `DailyTraffic` 同步增长；运行 `HOTSPOTMETER_STAGE5_TEST` 正常退出后 `active=[]`（会话已结算），无超时日志。统计窗口 `artifacts/stage6-stats.txt`：`layout=ok texts=44`，截图 1470×1050，标题栏采样 `rgb(11,11,12)`。
- **构建/测试**：Debug、Release 均 0 警告 0 错误；`dotnet test` **390/390**（Debug 与 Release）。
- **已知未解决**：WLAN 查询永久失败时不会自动重开句柄（仅目标未识别会重试）；采样间隔 >3 s 仍按睡眠处理，会丢该段字节；`ResumeAsync` 并发边角未覆盖；单实例使用 `Local\` 且清扫不按 RunId 过滤，同用户双会话可能互踩；无 `PowerModeChanged` 睡前检查点；Alt+F4 关闭胶囊后从托盘“显示”可能对已关闭窗口调 `Show()`；Explorer 重启后的托盘恢复只做了代码级实现，尚未人工实测。

### 中英双语实际结果（2026-11-15，首次启动选择 + 全局即时切换）

- **目标**：首次启动可选择中文/English 并记住；设置、统计、胶囊、托盘菜单、运行期提示同步切换，不重启、不减少既有功能。
- **资源层**：`HotspotMeter.Core/Localization/LanguageCatalog.cs` 用两套并排字典（约 120 键）而非 XAML 资源字典，使 Core/Infrastructure 也能取词，并可用单测强制中英键集合与占位符一致；`LocalizationService` 提供单例索引器、`T/F`、`LanguageChanged`；XAML 用 `{loc:Tr Key}` 绑定实时刷新。
- **首次启动**：`App.OnStartup` 先读 `ui-state.json` 的 `Language`；有效则直接使用，缺失/非法则先单实例、再弹 `LanguageWindow`（460×340 深色双语卡片）。取消/关闭直接退出且**不写入**语言；选择后写入现有配置、切换文化并重新加载。选择发生在主窗口创建前，无中文闪烁。
- **设置/统计/胶囊/托盘**：设置窗新增“语言”卡（780×700），全部静态与状态文案资源化；统计窗 44 条文本、图表/悬停/表头/空错状态全部资源化；胶囊标签、速度标签、右键菜单、固定文字、上次会话前缀资源化；托盘菜单改为 `ApplyLanguage()` 就地更新文本后 `Refresh()`。
- **验证**：构建 0/0；`dotnet test` **408/408**（含 18 个本地化用例）。UIAutomation 首次启动选择 English 后配置写入 `en-US`；英文设置窗 `layout=ok texts=26`、英文统计窗 `layout=ok texts=44`、英文胶囊四档/自定义 `layout=ok`、菜单 `Traffic Statistics | Lock Position | Resize… | Exit`；运行中中英互切即时生效；取消语言窗进程退出且配置无 `Language`。证据见 `artifacts/i18n-*.txt/png`。
- **限制**：本会话视觉引擎不可用，英文界面靠 UIAutomation/布局断言/像素验证，未人工肉眼终检；托盘菜单英文未实际展开（图标在隐藏区），仅代码与 `MenuItemTexts` 级验证；自动适配最简档按设计隐藏说明标签。详见 `docs/13_Localization.md`。

### 阶段 7 实际结果（2026-11-15，发布工程）

- **发布基线**：`main`，工作区干净；App `net10.0-windows10.0.26100.0`、Windows 10 1809+、x64；未改 schema、未删功能、未改默认行为。
- **构建/测试**：Release `dotnet clean/restore/build` 0 警告 0 错误；`dotnet test -c Release --no-build` **408/408**。
- **发布模式**：self-contained、win-x64、文件夹、非单文件、无 PDB；版本 **0.1.0**。选择理由见 D-104。
- **产物**：`artifacts/release/HotspotMeter-win-x64/`（210,418,431 B / 200.67 MiB / 483 文件）、`HotspotMeter-win-x64.zip`（81,890,833 B / 78.10 MiB）、`SHA256SUMS.txt`。
  - ZIP SHA-256：`2753fb8ae31c46173313b0b3842b1d71eb1295d53b51dad4f80ccdce98708f01`
  - `HotspotMeter.exe` SHA-256：`0c766d93f62e22377b5648d9e590a0840294944518fc6db1c504adfe6cb42931`
- **独立运行**：发布目录与 ZIP 解压目录均直接运行退出码 0；胶囊 `120,1020.7 / 135.3×44 / dpi1.5`；统计窗 `980×700` 读取真实历史；SQLite 37 会话/2 天/1179 快照保留。
- **安装包**：环境无 Inno Setup/NSIS，未擅自安装；仅交付便携 ZIP，安装/升级/卸载程序标记“未验证”（D-105）。
- **隐私/安全**：凭证、个人路径、外联、依赖漏洞扫描通过；当前工作区 SSID/BSSID/GUID 已替换为示例值，README 统计截图已脱敏。**Git 历史仍含早期真实标识与未脱敏截图**，未重写历史，属高风险残留（D-107/D-108）。
- **文档**：`docs/11_Release_Checklist.md`、`docs/12_Release_Notes.md`、`docs/13_Localization.md`、README 中英，以及 02/04/05/08/09/10 与本文。
- **限制**：未签名；无安装程序；干净虚拟机/混合 DPI/多显示器/24 h 长稳未验证；开始菜单展开期间仍受 Windows band 6 限制。

### Traffic Guard 第一阶段实际结果（2026-11-17）

- **范围**：只做底层守护引擎与持久化，不做提醒弹窗、风险评分、阈值规则、信任列表 UI、结束进程、守护页面或防火墙；现有胶囊/统计/设置/历史功能保持不变。
- **采样事实**：WinRT `GetNetworkUsageAsync/GetAttributedNetworkUsageAsync` 在本机实测约 **30 秒**才刷新一次，不是实时计数器。因此采样周期定为 30 秒，**首轮只建基线**，不把“未变化”解释成 0，也不把缺失归属分配给任何程序。
- **身份**：`AppKey` 为 `exe:{规范化完整路径}`、`pkg:{小写 PFN}` 或 `uncertain:{pid}:{startTicks}:{name}`；同名不同目录不合并；UWP 多进程按 PFN 合并；空归属跳过；设备路径先映射到 DOS 路径再匹配进程；无法确认的伪条目（如系统控制消息）使用 `opaque:{hash}` 且标记 `IdentityCertain=0`。
- **增量**：按 `(ProfileKey, AppKey)` 保存基线；首见只建基线、重复时间戳跳过、任一方向回退重建、ProfileKey 变化清空、本轮缺失程序删除基线、间隔 >90 秒标 `IntervalGap`；仅正增量写 `GuardTrafficSamples`，写库失败会回滚内存基线，下一轮从旧基线重算。
- **状态**：前台 PID → `Foreground`；否则有网络增量或可见主窗口 → `Background`；否则 `Idle`。不把“无窗口”直接等同于后台，也不做高频轮询。
- **数据库**：`SchemaMigrator.SupportedVersion=4`；新增 `GuardSettings`（单行默认 30 秒/保留 30 天）、`GuardAppRules`、`GuardTrafficSamples`、`GuardAlertHistory`（预留）；增量迁移、外键、CHECK、唯一索引与故障回滚均有测试。实际库从 v3 升到 v4，原有会话/快照/日汇总仍在。
- **运行时**：真实热点下连续采样，规则与样本正常写入；修复了设备路径未映射导致的伪 `exe:e:\device\...` 身份；`opaque:… / IPv6 Control Message` 被正确列为不确定身份；出现多进程聚合样本（`ProcessCount=2/3/10`）与 `Foreground/Background` 样本。
- **验证**：Debug/Release 构建 0 警告 0 错误；`dotnet test` **454/454**；现有 Stage5 自检通过（startup/taskbar/topmost/stats/monitor、`ui-layout=ok`），app.log 记录 Guard 已启动。
- **未做**：提醒/评分/阈值/信任 UI、结束进程、守护页、24 小时长稳、真实睡眠/唤醒与网络切换专项人工验收；逐应用历史只在 Guard 采样表中，尚未做展示。

### Traffic Guard 第二阶段实际结果（2026-11-17，规则引擎 + Windows 通知）

- **范围**：全局/单程序阈值、三种周期（当前会话/本地自然日/滚动 24 小时）、三种口径（下载/上传/合计）、仅后台或全部、单程序信任/关闭/暂时忽略；首次提醒、冷却、二次增长提醒；Windows 11 原生 Toast 与中英文案；提醒结果写 `GuardAlertHistory`。不做守护完整 UI、结束进程、防火墙、评分。
- **规则优先级**：全局开关 → 单程序 `Trusted/Disabled` → `Ignored`（到期恢复继承）→ 仅后台 → 阈值（单程序覆盖否则全局）→ 同周期去重 → 冷却 → 二次增长。判定为纯函数 `GuardRuleEngine`，无 I/O，边界可用模拟数据覆盖。
- **默认值**：100 MB、自然日、上下行合计、仅后台、1 小时冷却、二次提醒开启且增长 50 MB；只作用于 Guard，不改变胶囊/统计/设置既有默认。
- **周期起点**：`Session` 取 `ConnectionSessions` 中该 Profile 的活动会话 `StartedAtUtc`；`Today` 取本地时区零点转 UTC；`Rolling24Hours` 取评估时刻 −24 h。跨日/新会话会重新允许首次提醒。
- **去重与失败隔离**：先插 `NotificationStatus=Pending` 再发通知，插入失败则不发送；`Show` 失败记 `Failed` + 错误摘要，记录不丢；单程序异常不阻断其他程序；提醒评估异常不影响已写入的流量样本。
- **通知通道（已验证）**：未打包进程需 `SetCurrentProcessExplicitAppUserModelID` + 开始菜单快捷方式 + `PKEY_AppUserModel_ID`（x64 `PROPVARIANT` 必须 24 字节）+ `SHChangeNotify` + `CreateToastNotifier` + `Show`。未打包进程读 `ToastNotifier.Setting` 会抛 `0x80070490`，因此不据此判断权限；XML 必须 UTF-8，`activationType="foreground"`、`launch="stats"` 与 `<actions>` 齐全。
- **数据库**：`SchemaMigrator.SupportedVersion=5`；v5 仅 `ALTER TABLE ADD COLUMN`，`GuardSettings` 增提醒列、`GuardAppRules` 增规则列、`GuardAlertHistory` 增周期/上下行/通知结果/处理时间与索引；不重建、不删除 v4 数据。
- **验证**：Debug/Release 构建 0 警告 0 错误；`dotnet test` **485/485**；真实热点下把阈值临时调到 1 MB，30 秒内产生 2 条提醒记录，`NotificationStatus=1 (Sent)`、无错误，冷却期内未重复；周期起点为本地零点对应的 UTC。测试后已从备份恢复数据库并重新迁移为 v5，测试提醒记录已清除。
- **限制**：勿扰模式/通知权限无法通过 API 可靠检测，只能记录 `Show` 异常或“已发送但可能被系统抑制”；提醒设置目前只有数据库/服务 API，没有守护设置页；二次提醒只在冷却结束且增长达到阈值时触发，关闭二次提醒后同周期不再重复。

## 3. 文档索引与阅读顺序

| 文件 | 内容 | 什么时候读 |
| --- | --- | --- |
| [docs/00_Project_Overview.md](docs/00_Project_Overview.md) | 背景、目标、边界、成功标准 | 第一次接触项目 |
| [docs/01_Product_Requirements.md](docs/01_Product_Requirements.md) | 功能/非功能需求、验收标准 | 实现功能前 |
| [docs/02_UI_Specification.md](docs/02_UI_Specification.md) | 两个窗口的完整视觉与交互规范 | 写任何界面代码前 |
| [docs/03_Technical_Architecture.md](docs/03_Technical_Architecture.md) | 分层、组件、线程模型、生命周期 | 设计模块或加依赖前 |
| [docs/04_Data_Model.md](docs/04_Data_Model.md) | 表结构、索引、聚合与迁移 | 改数据库前 |
| [docs/05_Network_Monitoring.md](docs/05_Network_Monitoring.md) | WLAN/IP Helper 识别与采样 | 写网络代码前 |
| [docs/06_Data_Recovery.md](docs/06_Data_Recovery.md) | 检查点、崩溃恢复、重算 | 写持久化/恢复逻辑前 |
| [docs/07_Test_Plan.md](docs/07_Test_Plan.md) | 测试分层与异常场景矩阵 | 每个阶段收尾 |
| [docs/08_Development_Roadmap.md](docs/08_Development_Roadmap.md) | 阶段 0–7 的目标与退出标准 | 规划下一步 |
| [docs/09_Decisions.md](docs/09_Decisions.md) | 架构决策记录（ADR） | 想改已有选择前 |
| [docs/10_Development_Log.md](docs/10_Development_Log.md) | 每次工作的操作与结果 | 接手/复盘 |
| [docs/13_Localization.md](docs/13_Localization.md) | 中英资源组织、切换机制、配置与维护方式 | 改任何界面文字前 |

## 4. 已确认的事实（可依赖）

1. 产品形态、两个窗口的职责边界、右键菜单项、"悬浮窗只显示会话流量"等来自用户总指令，**属于已确认设计，不得擅自变更**。
2. 统计口径是"电脑目标 Wi-Fi 网卡的收发字节"，不等于运营商计费；界面与文案必须如实说明差异。
3. 一天内多次连接/断开，全部会话流量按本地自然日汇总；跨午夜会话按采样时间拆到两天。
4. 程序启动时若存在未完成会话，用最后一次有效检查点恢复；网卡/电脑重启后不得重复累计历史字节。
5. 突然断电可能丢失最后一次检查点之后的数据，不承诺绝对零丢失；`DailyTraffic` 必须能由基础记录重算。
6. 普通 WPF 应用无法通过公开 API 融入 Windows 11 原生任务栏，因此是"独立透明窗口 + 自动定位 + 手动拖动兜底"；4C-7 用独立探针实测了"置顶透明窗口覆盖任务栏内部"（视觉可行、但 Explorer 重启/UIA 空隙/层级重断言均不可靠），结论仍是不内嵌（D-068）。
7. 环境事实：`.NET 10.0.402` SDK、WindowsDesktop 10.0.13 运行时、x64、Windows 11 26200 均已在本机确认。
8. 本机 `Microsoft.Data.Sqlite 10.0.15` 能加载原生 SQLite（已由测试验证），`Microsoft.Extensions.Logging.Abstractions 10.0.15` 可还原。
9. WLAN API 可稳定取得 SSID/BSSID/GUID；`MIB_IF_ROW2.InterfaceGuid` 与之一一对应。`InOctets/OutOctets` 反映真实收发字节（150 MB 下载实测比值 1.0295）。
10. 结构体布局（x64）：`MIB_IF_ROW2`=1352、`WLAN_CONNECTION_ATTRIBUTES`=604、`WLAN_INTERFACE_INFO`=532。
11. 测速用 `Stopwatch` 不受墙钟跳变影响；重启可由 `TickCount64` + 开机近似时刻识别。
12. 引擎行为已被 58 项自动化测试锁定：会话累计=Σ delta、两帧确认、六状态、恢复六种结局、恢复幂等（含并发）、墙钟跳变不影响速度、F-1 恢复会话切分与单次结束。
13. 恢复 gap 的连续性默认 `Unverified`：`ResumeAsync` 算出候选但**不**并入原会话，必须由证据源给出 `Continuous` 才合并。
14. `MonitorEngine` 位于 Infrastructure；Core 不引用 Windows API、WPF、SQLite（靠项目引用方向保证）。
15. 采样间隔默认 1000 ms、最小 500 ms，`MonitorOptions.Validate()` 强制。
16. 3A 基础设施已被 101 项测试锁定：路径约束、PRAGMA（外键/WAL/忙等待）、迁移事务回滚与高版本拒绝、`quick_check` 损坏检测、四类文件整体隔离与失败不覆盖、单实例互斥、Writer 事务回滚/排空停止/100 并发串行/取消后可用。
17. 4C-8 实测：本机 150% DPI、底部任务栏厚 48 DIP，自动尺寸为 135.4×44 DIP（最简密度、隐藏速度标签）；任务栏 60/120 DIP 分别得到 192.8×62.7（紧凑）与 320×104（正常）。
18. 4C-9 实测：本机 150% DPI、任务栏 `0,1528,2560,1600`（48 DIP）时，自动定位结果为 `(120,1020.7)`，胶囊 135.4×44 完整落在任务栏内；垂直方向不能扣 8 DIP 边距，否则会被顶回任务栏上方。
19. 5.1 实测：任务栏/开始菜单可被 Shell 提升到更高窗口 band（诊断读数：胶囊 1、SearchHost/任务栏 6）；band 6 期间 `SetWindowPos(HWND_TOPMOST)` 返回成功但无法覆盖。band 回落到 1 后，一次重断言即可恢复；前台事件后需延迟约 250 ms，等任务栏完成自身抬升，立即置顶会被覆盖。
20. 阶段 6 实测：`synchronous=FULL` 下 14 秒内写入 5 个检查点，`DailyTraffic` 与会话字节同步增长；正常退出后无 Active 会话，自检退出耗时 7.4 s（含自检等待），无释放超时日志。
21. 阶段 6 修复了一个真实运行时缺陷：`CloseAbandonedSessionsAsync` 的 SQL 中误写 C# 注释 `//`，导致每次启动历史库初始化失败（`SQLite Error 1: near "/": syntax error`）；改为 SQL 的 `--` 后恢复。纯构建/单测无法发现，必须启动并查 `app.log`。
22. 阶段 6 统计口径已验证：统计查询按当前目标热点 `ProfileKey` 过滤；逐程序合计与总量差额只解释为“未归属/系统”，不做分摊。
23. 阶段 6 Release `dotnet clean/build -c Release` 0 警告 0 错误，`dotnet test -c Release --no-build` 390/390；Debug 同。
24. 中英双语（2026-11-15）已实现并验证：首次无 `Language` 时弹选择窗，选择写入 `ui-state.json`；取消不写；非法值按未选择处理并回退 zh-CN；运行中切换设置/统计/胶囊/托盘即时生效；`dotnet test` 408/408。英文界面经 UIAutomation/布局断言/像素验证，未人工目检。
25. 阶段 7 发布（2026-11-15）：self-contained win-x64 文件夹 210,418,431 B（200.67 MiB，483 文件），`HotspotMeter.exe` 544,768 B、`FileVersion=0.1.0.0`；ZIP 81,890,833 B（78.10 MiB），SHA-256 `2753fb8a…98708f01`；exe SHA-256 `0c766d93…cb42931`。
26. 发布目录与 ZIP 解压目录均独立运行成功：退出码 0，胶囊 `120,1020.7 / 135.3×44 / dpi1.5`，统计窗 `980×700` 读取真实历史；SQLite 37 会话/2 天/1179 快照保留，说明升级不写安装目录、不丢 `%LOCALAPPDATA%` 数据。
27. 安全扫描：工作区凭证/个人路径/外联/依赖漏洞均无命中；真实 SSID/BSSID/GUID 已替换为示例值，README 截图已脱敏。Git 历史仍含早期真实网络标识与未脱敏截图，属高风险历史残留，未重写。
28. 发布环境无 Inno Setup/NSIS，未擅自安装；本轮只交付便携 ZIP，安装包与安装/卸载程序未验证（D-105）。
29. Traffic Guard 实测：Windows 逐应用归属 API 约 **30 秒**刷新一次；4 MB 下载后约 12 秒内累计值不变，之后一次跳变约 4.1 MB，逐应用合计比网卡总量少约 4.5 MB（未归属差额），探针进程本身未出现在归属列表中。因此不能假设实时，也不可把缺失当 0。
30. Traffic Guard v4 已实测：真实库 `user_version=4`，原有 52 会话/5439 快照/4 天日汇总仍在，`GuardTrafficSamples`/`GuardAppRules` 正常增长；样本间隔 30 秒，规则首末时间正常。
31. Traffic Guard 身份实测：设备路径映射缺失时会产生伪 `exe:e:\device\...`；映射后恢复 `exe:c:\...`/`exe:e:\...`/`pkg:...`。无法确认的 `IPv6 Control Message` 已转为 `opaque:{hash}` + `IdentityCertain=0`，不会与真实 EXE 合并。
32. Traffic Guard v5 已实测：真机库 `user_version=5`，v5 仅在 v4 上 `ADD COLUMN`，既有会话/快照/日汇总/Guard 样本保留；默认提醒配置为 100 MB、自然日、合计、仅后台、3600 秒冷却、二次 50 MB，提醒表初始为空。
33. Windows Toast 未打包注册链路已实测可弹出：`SetCurrentProcessExplicitAppUserModelID` + 开始菜单 `.lnk` + `PKEY_AppUserModel_ID`（x64 `PROPVARIANT` 必须 24 字节）+ `SHChangeNotify` + `CreateToastNotifier` + `Show`；`ToastNotifier.Setting` 在未打包进程会抛 `0x80070490`，不能用于权限判断。
34. 提醒实机验证：临时阈值 1 MB、关闭仅后台、冷却 60 s，真实热点 75 s 内产生 2 条提醒（Claude、node），`NotificationStatus=1 (Sent)`，再等 45 s 无重复；恢复备份后提醒记录归零、默认阈值恢复。
35. 阶段 2 测试数 **485/485**（Debug/Release）；新增规则引擎、提醒服务、v5 迁移、GuardStore 扩展与“提醒异常不影响样本”用例。

## 5. 未验证的假设（禁止当结论使用）

| 假设 | 计划验证阶段 |
| --- | --- |
| 1000ms 采样间隔下速度数字可读；但 UI 上的平滑度与动画效果 | 阶段 2 已验证数字可读，平滑度阶段 4 定 |
| 引擎连续运行 30 分钟无内存增长/句柄泄漏 | 阶段 7（阶段 2 只做了分钟级真机运行） |
| 透明圆角窗口的性能与点击命中：阶段 4C-1 已验证渲染、DPI 定位与文字坐标；拖动、透明区穿透、跨屏/DPI 变化未测 | 阶段 4 剩余验收 |
| 能通过任务栏窗口 + UI Automation 稳定定位"开始按钮"附近空位 | 阶段 4 |
| 通过 `PowerModeChanged`/`SystemEvents` 可靠捕获睡眠唤醒 | 阶段 3/4 |
| 真实磁盘损坏/断电后的 `quick_check` 结果与隔离目录权限行为（3A 只模拟了垃圾字节与被占用文件） | 阶段 3E |
| 单文件发布 + WPF + SQLite 原生库在目标机无需额外依赖 | 阶段 7 |
| 企业级/隐藏 SSID、多无线网卡、2.4G/5G 切换、超长休眠下的行为 | 阶段 2/7 |

## 6. 工作约定（摘要，详见 AGENTS.md）

- 阶段门禁：先读知识库 → 检查代码 → 分析 → 给计划 → **等确认** → 小步实现 → 构建测试 → 更新知识库 → 汇报。
- 已确认设计不改；发现技术问题先说明问题 + 备选方案，等用户决定。
- 文档区分已验证事实与未验证假设；不把猜测写成已实现。

## 7. 常用命令

```powershell
dotnet build HotspotMeter.sln -c Debug
dotnet test  HotspotMeter.sln -c Debug
dotnet run --project HotspotMeter.App   # 阶段 0~3 无界面，启动即退出

# 阶段 1 验证原型（独立于解决方案）
dotnet run --project tools/ApiProbe/ApiProbe.csproj -c Release -- selftest
dotnet run --project tools/ApiProbe/ApiProbe.csproj -c Release -- list
dotnet run --project tools/ApiProbe/ApiProbe.csproj -c Release -- watch --seconds 30
dotnet run --project tools/ApiProbe/ApiProbe.csproj -c Release -- continuity --save tools/ApiProbe/out/state.json

# 阶段 2 控制台宿主（独立于解决方案）
dotnet run --project tools/MonitorHost -- --list
dotnet run --project tools/MonitorHost -- --ssid "TestHotspot" --seconds 60
dotnet run --project tools/MonitorHost -- --ssid "TestHotspot" --seconds 20 --json
```

## 8. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-12 | 建立知识库；完成阶段 0（骨架、构建、测试、文档） |
| 2026-11-12 | 完成阶段 1（11 项 API 验证）；项目迁移到 `E:\hotspotmeter\HotspotMeter`；新增 D-017～D-025 |
| 2026-11-12 | 完成阶段 2（Core 模型/累积器/恢复判定、Infrastructure 检测器/计数器/引擎、54 项测试、MonitorHost 真机比对）；新增 D-026～D-034 |
| 2026-11-12 | F-1 收尾修正：恢复时切分会话、非 Continued 不再静默丢会话、单次结束水位；58 项测试通过；新增 D-035 |
| 2026-11-12 | 阶段 3A：SQLite 路径/连接/迁移/健康检查/隔离/单实例/串行 Writer 骨架；101 项测试通过；新增 D-036～D-040 |
| 2026-11-13 | 阶段 4C-1：悬浮胶囊首版（胶囊窗口、ViewModel、引擎桥接、DPI 感知定位、位置记忆、UI 诊断开关、截图）；348 项测试通过；未提交 |
| 2026-11-13 | 阶段 4C-2：托盘（NotifyIcon）、设置窗口（单实例）、四档尺寸 + 自定义、固定/解除固定、图标生成；348 项测试通过；新增 D-051～D-054 |
| 2026-11-13 | 阶段 4C-3：四档统一比例 + 最小/最大范围、右键菜单 `Separator` 崩溃根因修复、崩溃日志；364 项测试通过；新增 D-055～D-057 |
| 2026-11-13 | 阶段 4C-4：任务栏检测（AppBar + 工作区回退）、自动缩放/停靠、设置开关、手动优先；364 项测试通过；新增 D-058～D-060 |
| 2026-11-13 | 阶段 4C-5：速度竖排、深色滚动条与右键菜单模板、任务栏停靠左/上对齐、工作区夹取与 `SetWindowPos` 置顶、多屏检测修正；364 项测试通过；新增 D-061～D-066 |
| 2026-11-13 | 阶段 4C-6：速度块与流量数字共用同一 Grid 行并 `VerticalAlignment=Center`，新增 `CheckSpeedAlignment` 断言，四档 + 自定义 `delta=0`；364 项测试通过；新增 D-067 |
| 2026-11-13 | 阶段 4C-7：任务栏内部显示可行性验证（独立探针、Explorer 重启/UIA 空隙实测）；结论不内嵌，产品代码未改；新增 D-068 |
| 2026-11-13 | 阶段 4C-8：自动适配只调尺寸（高度优先 40–104 DIP、三档密度）、胶囊删除 Wi-Fi/状态/说明并放大数字与速度；新增 12 个测试（376 通过）；新增 D-069～D-073；未 commit（混合改动无法安全拆分） |
| 2026-11-13 | 阶段 4C-9：自动适配可拖入任务栏（显示器边界 + 0 DIP）、首次开启自动定位底部偏左 120 DIP、拖动结束只重算尺寸、`TaskbarCreated` 层级恢复与 `AppLog`；新增 9 个测试（385 通过）；新增 D-074～D-077；统一提交 4C-1～4C-9 全部改动（未 push） |
| 2026-11-13 | 阶段 4C-10：胶囊说明标签"已使用流量"移到数字上方（两行 Grid，速度块与数字同行保持中心对齐）；普通四档/自定义可见，自动适配 Compact/Minimal 隐藏；更新 docs/02、D-079、日志 |
| 2026-11-13 | 阶段 4C-11：标签与数字合并到同一 StackPanel、共用 TrafficFontFamily、标签 SemiBold 且左边缘严格对齐；UiDump 增加左对齐/字体一致断言；新增 D-080（覆盖 D-079 对齐部分） |
| 2026-11-13 | 阶段 4C-12：说明标签自身 `Opacity=0.65`（父容器与数字保持 1.0），UiDump 增加透明度隔离断言；对比度实测记录；新增 D-081；提交全部未提交改动 |
| 2026-11-13 | 阶段 4C-13：标签与数字统一 `Microsoft YaHei UI` + Bold，消除 CJK 逐字符回退与 SemiBold→Bold 错配；UiDump 增加字重/样式/字形覆盖断言；新增 D-082；提交 |
| 2026-11-13 | 阶段 4C-14：自动适配任务栏时流量数字 ×1.18（最简档收窄水平内边距保证 renderScale=1.0），关闭立即恢复；修复 `SetSize` 倍率残留；UiDump 增加渲染尺寸诊断；新增 D-083；提交 |
| 2026-11-14 | 阶段 5：任务栏置顶事件监听、HKCU 开机自启、任务栏显示默认开启、`TrafficHistoryStore` 持久化接入、WinRT 逐程序归属读取、独立统计窗口；390 项测试通过；新增 D-084～D-089；统一提交 |
| 2026-11-14 | 阶段 5.1 修复：前台监听不再按 Shell 类名过滤，250 ms 后单次重断言；开始菜单展开期间受 band 6 限制、关闭后自动恢复；统计窗口接入 `DarkTitleBar`；390 测试通过；新增 D-090～D-092 |
| 2026-11-14 | 阶段 6：发布前审查与加固（退出死锁、统计按 ProfileKey 过滤、DB 健康分类、synchronous=FULL、统计竞态取消、托盘 TaskbarCreated、原子配置、设置窗夹取、目标热点重试、SQL 注释运行时缺陷）；Debug/Release 390 测试通过；新增 D-093～D-099 |
| 2026-11-15 | 中英双语：`LanguageCatalog`/`LocalizationService`/`{loc:Tr}`、首次启动语言选择窗、`ui-state.Language`、设置/统计/胶囊/托盘即时切换；408 测试通过；新增 D-100～D-103、docs/11 |
| 2026-11-15 | 阶段 7 发布：0.1.0 self-contained win-x64 文件夹 + 便携 ZIP + SHA256；发布目录与解压目录运行验证；截图脱敏、SSID/BSSID/GUID 替换、安全扫描；新增 docs/11–13、D-104～D-108；安装包未制作（工具缺失） |
| 2026-11-17 | Traffic Guard 第一阶段：Core 身份/状态/增量/采样服务，Infrastructure 进程检查与 v4 存储，App WinRT 来源与启停接线；454 测试通过；新增 D-109～D-114；未提交 |
| 2026-11-17 | Traffic Guard 第二阶段：纯规则引擎（三周期/三口径/信任/忽略/冷却/二次）、`GuardAlertService` 先落库后通知、Windows Toast 未打包注册、v5 增量迁移、中英文案；485 测试通过；新增 D-115～D-120；本地独立提交 |