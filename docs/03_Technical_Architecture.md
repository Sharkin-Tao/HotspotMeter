# 03 技术架构

> 本文件描述**目标架构**。截至阶段 0，只有解决方案骨架、`ByteSizeFormatter` 与测试真实存在；其余均为设计，不是已实现功能。

## 1. 分层与依赖

```
HotspotMeter.App  (WPF: 视图 / 视图模型 / 窗口定位 / 程序生命周期)
        │
        ├──────────────► HotspotMeter.Core  (领域模型 / 规则 / 接口，纯 .NET)
        │                        ▲
        └──► HotspotMeter.Infrastructure ─┘  (Win32 / WLAN / IP Helper / SQLite)
```

规则：

- **Core** 不引用 WPF、Win32、SQLite、日志实现；可以在任何 .NET 测试宿主里跑。
- **Infrastructure** 实现 Core 定义的接口；可以引用 `Microsoft.Data.Sqlite`、`Microsoft.Extensions.Logging.Abstractions`。
- **App** 只做"组装 + 展示 + 用户交互"，不写计数算法与 SQL。
- 依赖注入在 App 启动时完成组装；Core 不知道容器存在。

为什么不拆成更多项目（如 `.Domain` / `.Application` / `.Persistence`）：当前规模下 4 个项目已经能表达边界，继续拆会让学生维护成本上升，收益很小。若将来 Core 膨胀，再按"监控 / 统计 / 存储"拆分。

## 2. 项目职责

### HotspotMeter.Core

| 目录（计划） | 内容 |
| --- | --- |
| `Models/` | `HotspotProfile`、`HotspotConnection`、`ConnectionSession`、`TrafficDelta`、`DailyTraffic`、`SessionStatus`、`AppSettings` |
| `Abstractions/` | `IClock`、`IHotspotDetector`、`ITrafficCounter`、`ISessionRepository`、`ISnapshotRepository`、`IDailyTrafficRepository`、`ISettingsRepository`、`ITrafficStatisticsService`、`IMonitorEngine` |
| `Services/` | `SessionManager`（状态机）、`TrafficAggregator`（日聚合/跨午夜拆分）、`StatisticsCalculator`（周/月汇总） |
| `Formatting/` | `ByteSizeFormatter`（已实现）、时长/速度格式化（待实现） |

### HotspotMeter.Infrastructure

| 目录（计划） | 内容 |
| --- | --- |
| `Interop/` | `WlanApi`（WlanOpenHandle/EnumInterfaces/QueryInterface/FreeMemory）、`IpHelper`（GetIfTable2/FreeMibTable）、`NativeMethods`（任务栏矩形、DPI、电源事件） |
| `Monitoring/` | `WlanHotspotDetector`、`InterfaceTrafficCounter`、`MonitorEngine`（采样循环 + 状态机驱动） |
| `Persistence/` | `SqliteConnectionFactory`、`SchemaMigrator`、各仓储实现、`UnitOfWork` |
| `Settings/` | 基于 `AppSettings` 表的设置读写 |
| `Diagnostics/` | 文件日志提供程序、崩溃转储钩子（可选） |

### HotspotMeter.App

| 目录（计划） | 内容 |
| --- | --- |
| `Views/` | `FloatingBarWindow`、`StatisticsWindow`、`SettingsWindow` |
| `ViewModels/` | 对应三个窗口的 VM；`ObservableObject` / `RelayCommand` 基类（待定，见 D-008） |
| `Services/` | `SingleInstanceGuard`、`WindowPositioner`、`StatisticsWindowLauncher`、`TrayIconService`（待确认） |
| `Resources/` | 样式、图标、字体设置 |

### HotspotMeter.Tests

- `Unit/`：纯逻辑（格式化、日边界、会话状态机、速度计算、恢复算法）。
- `Integration/`：SQLite 仓储（临时文件库）、迁移、重算。
- 网络 API 用接口隔离，单元测试用假实现；真实 API 由阶段 1 的独立原型验证。

## 3. 关键接口草案（阶段 2 会细化为正式代码）

```csharp
// Core/Abstractions
public interface IHotspotDetector
{
    Task<IReadOnlyList<HotspotCandidate>> GetCandidatesAsync(CancellationToken ct);
    Task<HotspotConnection?> GetConnectionAsync(CancellationToken ct);
}

public interface ITrafficCounter
{
    // 返回目标接口的累计收发字节；接口不存在时返回 null。
    Task<InterfaceCounters?> TryReadAsync(NetworkInterfaceKey key, CancellationToken ct);
}

public interface ISessionRepository
{
    Task<long> StartSessionAsync(HotspotId hotspotId, DateTimeOffset startUtc, CancellationToken ct);
    Task CheckpointAsync(long sessionId, long download, long upload, DateTimeOffset atUtc, CancellationToken ct);
    Task EndSessionAsync(long sessionId, SessionStatus status, DateTimeOffset endUtc, CancellationToken ct);
    Task<IReadOnlyList<ConnectionSession>> GetUnfinishedSessionsAsync(CancellationToken ct);
}
```

设计要点：

- `NetworkInterfaceKey` 用 WLAN 接口 GUID + LUID 组合，避免只靠字符串名称匹配。
- 时间一律 `DateTimeOffset`（UTC 存储），本地日归属由 `IClock` + 时区规则计算，便于测试跨午夜/夏令时。
- 计数读取与持久化都是 `async`，不阻塞 UI 线程。

## 4. 采样循环（计划）

```
每 1 秒 tick:
 1. 检测目标热点是否连接（WLAN API）
    ├─ 未连接 → 若当前有会话：结束会话（写最终检查点）
    └─ 已连接 → 若无会话：读取网卡基线，创建会话
 2. 读取目标接口累计 In/Out（GetIfTable2）
 3. 校验：
    - 接口 GUID/LUID 是否变化 → 重建基线，不累加
    - 当前值 < 上次值 → 计数器回退，重建基线，不累加
    - 距上次采样 > 阈值（唤醒/卡顿）→ 只累加本次增量，不把间隔时间用于速度
 4. 计算增量与速度（Stopwatch 单调时间）
 5. 更新内存中的会话累计与 UI 可观察状态
 6. 每 5–10 秒（或断开/退出/睡眠前）写一次检查点 + 快照
```

异常隔离：单次 tick 内任何异常都记录日志并继续下一 tick；连续失败 N 次才把状态置为"采集异常"，绝不因一次异常终止监控。

## 5. 线程模型

| 工作 | 线程 |
| --- | --- |
| 采样循环 | 单个后台任务（`PeriodicTimer` + `Task`），不占 UI 线程 |
| 数据库写入 | 采样循环内串行执行（SQLite 单写入者），用事务批量提交 |
| UI 更新 | `Dispatcher.BeginInvoke`，只传值不传对象 |
| 统计查询 | 后台线程查询，结果回 UI 线程绑定 |

不让 UI 线程等待数据库；不让多个线程同时写 SQLite（WAL 模式下读可以并发）。

## 6. 生命周期

```
启动
 → 单实例检查（重复则激活已有实例并退出）
 → 初始化日志、数据目录
 → 打开数据库、执行迁移
 → 恢复未完成会话（见 06）
 → 创建悬浮窗（阶段 4）
 → 启动采样循环
 → 注册电源事件（睡眠/唤醒）、显示设置变化事件
退出
 → 停止采样循环（等待当前 tick 结束）
 → 写最后检查点、结束会话
 → 关闭数据库、刷新日志
 → 释放互斥量
```

- 统计窗口关闭只销毁窗口与 VM，不影响服务。
- `ShutdownMode` 设为 `OnExplicitShutdown`（阶段 4 起），避免关闭统计窗口导致进程退出。

## 7. 依赖注入与配置（计划）

- 使用 `Microsoft.Extensions.DependencyInjection`（阶段 2/3 引入，版本与 10.0.x 对齐）。
- 组合根在 `App.OnStartup`：注册 Core 服务、Infrastructure 实现、App 视图模型。
- 配置项：目标热点、采样间隔、固定状态、窗口位置、开机启动、日志级别。存 `AppSettings` 表（键值对，带类型标记）。
- 不引入 `Microsoft.Extensions.Hosting`：桌面程序不需要通用主机，手写启动顺序更直观。

## 8. 数据与文件位置

| 内容 | 路径 |
| --- | --- |
| 数据库 | `%LOCALAPPDATA%\HotspotMeter\hotspotmeter.db` |
| 日志 | `%LOCALAPPDATA%\HotspotMeter\logs\` |
| 崩溃标记 | `%LOCALAPPDATA%\HotspotMeter\session.lock`（可选） |

不写程序安装目录，避免 UAC 与多用户问题；卸载时由用户或安装包清理数据目录。

## 9. 日志与错误处理

- 日志级别：默认 Information；采样细节用 Debug；计数器回退、恢复、迁移用 Warning/Information。
- 全局兜底：`DispatcherUnhandledException`、`TaskScheduler.UnobservedTaskException`、`AppDomain.UnhandledException` 都记录后尽量安全退出，不弹裸异常框。
- 面向用户的错误用中文短句，技术细节进日志。

## 10. 可测试性设计

- 所有系统 API 通过接口注入，Core 不 new 具体实现。
- 时间通过 `IClock` 注入，可测跨午夜、夏令时、系统时间回拨。
- 计数器读取通过 `ITrafficCounter` 注入，可模拟回退/接口消失/巨大间隔。
- 仓储对 `:memory:` 或临时文件库做集成测试。
- UI 逻辑（定位、格式化）尽量抽成纯函数，窗口代码只做装配。

## 11. 当前实现状态（阶段 0）

| 项 | 状态 |
| --- | --- |
| 解决方案、4 个项目、引用关系、CPM、`global.json` | 已完成 |
| WPF 应用骨架（无窗口、启动即退出） | 已完成，仅用于验证工具链 |
| `ByteSizeFormatter` + 13 个测试 | 已完成（暂定格式规则） |
| 其余接口/服务/表结构/窗口 | 未实现 |
## 阶段 5 新增组件（2026-11-14）

- `App/Services/TrafficHistoryStore`：持久化协调器；稳定 ProfileKey、10 s 检查点、跨午夜分段、OperationId 去重、
  启动收尾遗留会话；写入通过 Infrastructure 的 `SqliteWriter` 单消费者执行。
- `App/Services/TrafficStatsService`：只读查询 `DailyTraffic`，按本地自然日返回 `DailyUsage`（缺失 != 0）。
- `App/Services/AttributedUsageReader`：WinRT 逐程序归属；纯名称解析已下沉到 `Core/Usage/AttributionDisplayName`。
- `App/Services/ShellForegroundWatcher`、`StartupRegistrationService`：Win32 前台事件钩子（不按类名过滤；
  每次前台变化只安排 250 ms 后单次重断言，避开任务栏随后的自身抬升）与 HKCU Run 注册。
- `App/ViewModels/StatsViewModel` + `App/Views/StatsWindow`：统计窗口；由 `App.OpenStats()` 单实例管理。
- 新增依赖边界：App 的 TFM 升为 `net10.0-windows10.0.26100.0` 才能引用 WinRT 网络归属 API；
  Core/Infrastructure/Tests 仍保持 `net10.0`，Core 不出现 Windows 类型。

## 阶段 6 加固（2026-11-14）

- **退出生命周期**：`App.OnExit` 把 `MonitorSessionService.DisposeAsync()` 放到线程池执行，再以 3 秒为上限等待；服务内部 `ConfigureAwait(false)`，先等历史写入任务链排空，再无条件关闭 `SqliteWriter`。避免 UI 线程同步等待自身续体造成固定 3 秒冻结。
- **统计查询边界**：`TrafficStatsService` 的三类查询都要求当前目标热点的 `ProfileKey`；`JOIN HotspotProfiles` 过滤，不再把历史连接过的其他热点混入。没有目标时返回完整日期轴且 `HasRecord=false`，UI 显示"暂无数据"而不是伪造 0。
- **统计加载并发**：`StatsViewModel` 用 `_loadVersion` 丢弃过期结果，所有可见字段在版本守卫后写入；窗口关闭时 `_lifetime.Cancel()`，WinRT 调用透传取消令牌。
- **持久化健康**：`DatabaseHealth` 把 BUSY/LOCKED/IOERR 视为暂时占用（不隔离），`DatabaseBootstrapper` 在不可隔离时抛异常保留原文件；只有确定损坏才整体移入 `corrupt-*`。`SqliteWriter` 仍为单消费者、每项独立事务。
- **托盘生命周期**：`TrayIconService` 使用 `HWND_MESSAGE` 隐藏窗口监听 `TaskbarCreated`，Explorer 重启后重新设置 `NotifyIcon.Visible`；菜单字体使用 `SystemFonts.MenuFont`，不创建/泄漏 GDI Font。
- **配置写入**：`WindowStateStore` 先写临时文件再 `File.Replace`；读取失败把原文件改名为 `.bad-*` 并记录日志，避免半写文件被静默当成默认值。
- **热点重试**：`MonitorSessionService` 在未识别到目标热点时启动 5 秒 `DispatcherTimer` 重试；成功后停止。启动失败会释放半初始化引擎，防止重试被非空引用挡住。

## Traffic Guard 第一阶段组件（2026-11-17）

Guard 与现有网卡流量监控**并行但独立**：网卡引擎负责会话/日汇总，Guard 只负责逐程序归属与增量，二者通过 `ProfileKey`（接口 GUID+SSID 派生）关联，不互相写对方的表。

| 层 | 组件 | 职责 |
| --- | --- | --- |
| Core | `TrafficGuard/AppIdentity`、`AppIdentityResolver` | 把归属 Id/进程路径解析为稳定 `AppKey`；桌面路径、PFN、Uncertain、opaque 四类；同名不同路径不合并 |
| Core | `ProcessStateClassifier` | 根据前台 PID、可见窗口、网络增量判定 `Foreground/Background/Idle`；纯函数可测 |
| Core | `TrafficDeltaCalculator` | 按 `(ProfileKey,AppKey)` 保存基线，算正增量；首见建基线、重复时间戳跳过、回退/切换/缺失重建；支持 `CaptureState/RestoreState` 回滚 |
| Core | `TrafficGuardService` | 30 秒 `PeriodicTimer` 采样循环；Start/Pause/Resume/Stop；防重复启动；异常只记诊断，不逃逸；暂停不采样、恢复重建基线 |
| Core | `IGuardUsageSource/IProcessInspector/IGuardSampleSink/IGuardTargetProvider/IGuardDiagnostics` | 端口接口；Core 不出现 Win32/WinRT/SQLite 类型 |
| Infrastructure | `TrafficGuard/ProcessInspector` | Toolhelp32 一次枚举 PID/父进程/路径；`QueryFullProcessImageName`；`GetPackageFamilyName`；`EnumWindows` 找可见主窗口（排除 cloaked/tool window/owned）；前台 PID |
| Infrastructure | `DevicePathMapper` | `\device\harddiskvolumeN\...` → DOS 路径，60 秒缓存；让归属 Id 能与进程路径匹配 |
| Infrastructure | `GuardStore` | 单事务 upsert 规则 + 插入样本；`INSERT ... ON CONFLICT DO NOTHING` 幂等；设置读取/保存；经 `SqliteWriter` 串行 |
| App | `GuardUsageSource` | WinRT `GetNetworkUsageAsync` + `GetAttributedNetworkUsageAsync`，按当前目标热点 adapter GUID+SSID 精确选 profile；返回 `IsAvailable`，失败不伪造 0 |
| App | `GuardRuntime` | 从 `MonitorSessionService` 取目标并派生 `ProfileKey`；诊断写 `app.log`；Guard 启停与主程序解耦 |

**生命周期**：`App.OnStartup` 在监控初始化后启动 Guard（无论当前是否已连热点）；Guard 初始化失败只记录，不影响胶囊/统计/监控。`OnExit` 先停 Guard，再释放 Monitor，最后停 `SqliteWriter`。`GuardSettings.Enabled=0` 时不建采样服务。

**为什么 30 秒**：WinRT 归属 API 实测约 30 秒刷新一次，低于该频率只会重复读到同一累计值；首轮只建基线，避免把历史累计当成本轮流量。高于 90 秒的间隔标 `IntervalGap`，仍按真实差值计算但不假设连续。

**边界**：本阶段不写提醒/评分/信任 UI；`GuardAlertHistory` 只建表；不做进程结束/防火墙；不把未归属流量分摊给任何程序。
