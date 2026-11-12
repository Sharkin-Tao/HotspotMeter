# 09 架构决策记录（ADR）

格式：编号、日期、状态（已决定 / 暂定待确认 / 已否决）、背景、决策、理由、影响。
已否决的方案保留原因，避免以后重复讨论。

---

## D-001 使用 .NET 10 + WPF（原生）

- 状态：已决定（用户指定）
- 背景：需要常驻桌面、透明窗口、低内存、能调 Win32。
- 决策：`net10.0-windows` + WPF，不使用 WinUI 3 / Avalonia / Electron。
- 理由：与用户技术栈一致；WPF 的透明窗口、`WindowChrome`、数据绑定成熟；WinUI 3 对无边框置顶窗口与 P/Invoke 场景更重；Electron 内存占用高。
- 影响：UI 层只能跑在 Windows；跨平台不在目标内。

## D-002 平铺四个项目而不是 `src/` + `tests/` 目录

- 状态：已决定（阶段 0）
- 背景：用户给出四个项目名；常见做法是 `src/` + `tests/` 分组。
- 决策：项目直接放在仓库根目录：`HotspotMeter.App`、`.Core`、`.Infrastructure`、`.Tests`。
- 理由：四个项目规模小，少一层目录对初学者更直观；与用户给出的结构一致，减少认知负担。
- 影响：将来增加 `tools/`、安装包项目时，再考虑引入 `src/`；迁移成本低。

## D-003 集中包版本管理（CPM）

- 状态：已决定（阶段 0）
- 背景：四个项目会共享 `Microsoft.Extensions.Logging.Abstractions`、测试包等。
- 决策：`Directory.Packages.props` 统一声明版本，项目文件只写包名。
- 理由：避免版本漂移；升级只需要改一处；评审时依赖一目了然。
- 影响：新增包必须先在 `Directory.Packages.props` 加 `PackageVersion`，否则还原报错（对新手是一个学习点）。

## D-004 `global.json` 固定 SDK 10.0.402，`rollForward=latestFeature`

- 状态：已决定
- 背景：机器上同时装有 7/9/10 运行时。
- 决策：固定 `10.0.402`，允许更高 feature band，禁止预览版。
- 理由：保证团队成员/未来自己构建结果一致，同时不阻止安装补丁版本。
- 影响：若换机器只有 10.0.100，需要安装对应 SDK 或调整该文件（需记录到日志）。

## D-005 阶段 0 的 WPF 应用不创建任何窗口

- 状态：已决定
- 背景：阶段 0 只验证环境，用户明确"不实现正式 UI"。
- 决策：`App.xaml` 不设 `StartupUri`，`OnStartup` 直接 `Shutdown()`；删除模板 `MainWindow`。
- 理由：避免提前固化 UI 结构；保证 `dotnet run` 不会留下无界面的常驻进程（默认 `ShutdownMode=OnLastWindowClose` 在无窗口时不会自动退出）。
- 影响：阶段 4 必须删除该 `Shutdown()` 并改为创建悬浮窗，已在代码注释中标明。

## D-006 测试框架用模板自带的 xUnit 2.9.3

- 状态：已决定（阶段 0），可在阶段 3 重新评估
- 背景：NuGet 上 xUnit v3（4.0.x）已发布，但模板默认仍是 v2 + VSTest。
- 决策：沿用模板版本（xunit 2.9.3 + Microsoft.NET.Test.Sdk 17.14.1 + runner.visualstudio 3.1.4）。
- 理由：与 `dotnet test`、IDE 兼容性最好；v3 + Microsoft.Testing.Platform 在部分工具链上仍需适配，当前阶段不值得冒险。
- 影响：无功能差异；后续如需并行/原生 AOT 测试再迁移。

## D-007 数据访问用 Microsoft.Data.Sqlite + 手写 SQL，不用 EF Core

- 状态：已决定
- 背景：表只有 5 张，查询以聚合为主。
- 决策：`Microsoft.Data.Sqlite 10.0.15`，手写 DDL 与 SQL，自己实现迁移器。
- 理由：EF Core 会引入较大依赖与 LINQ 翻译的不确定性；手写 SQL 便于精确控制事务、索引与聚合语义（跨午夜拆分等），也更利于学习。
- 影响：需要自己写映射与迁移；测试要覆盖 SQL 正确性。
- 备选（已否决）：Dapper（多一个依赖，收益有限）、EF Core（过重）。

## D-008 MVVM 基类：手写 vs CommunityToolkit.Mvvm

- 状态：暂定待确认（阶段 5 前定）
- 背景：需要 `INotifyPropertyChanged` 与 `ICommand`。
- 倾向：App 层手写 `ObservableObject` + `RelayCommand`（约 60 行）。
- 理由：零额外依赖，逻辑透明，适合学习；本项目视图模型数量少（3 个窗口）。
- 备选：`CommunityToolkit.Mvvm`（源生成器，样板代码更少，但引入新依赖与生成器调试成本）。
- 影响：待用户确认后写入正式决策。

## D-009 时间与日期存储约定

- 状态：已决定（设计）
- 背景：跨午夜、夏令时、系统改时间都会影响"每日归属"。
- 决策：所有时刻存 UTC ISO-8601 文本（`...Z`）；同时冗余存本地日期 `yyyy-MM-dd`（`LocalDate`）用于分组；时间差用 `Stopwatch`，不用墙钟。
- 理由：文本可读、可排序；UTC 避免歧义；本地日期由写入时按 `TimeZoneInfo.Local` 计算，夏令时切换日也能得到正确的"自然日"。
- 影响：查询按 `LocalDate` 索引；用户改时区后历史日期不重算（已发生的事实不追溯）。

## D-010 快照存"区间增量"而不是"累计值"

- 状态：已决定（设计）
- 背景：需要同时满足恢复精度与日聚合。
- 决策：`TrafficSnapshots` 每行是一个区间的增量（含起止时间、归属日期），会话总量另存于 `ConnectionSessions`，二者可互相校验。
- 理由：增量天然可按日期拆分；累计值若直接按日切分会产生重复/遗漏；增量表也让"会话总量 = 快照之和"成为可验证不变量。
- 影响：每 10 秒一行，需要保留期策略（D-011）。

## D-011 原始快照默认保留 30 天

- 状态：暂定待确认
- 背景：10 秒一条 ≈ 8640 行/天 ≈ 26 万行/月。
- 决策（草案）：`TrafficSnapshots` 保留 30 天，`ConnectionSessions` 与 `DailyTraffic` 永久；超出保留期的日统计由 `DailyTraffic` 承担，重算时用会话比例估算。
- 理由：控制数据库体积；日统计长期可用。
- 影响：30 天前的"精确重算"能力下降，需在"关于"中说明。
- 备选：保留 1 年（体积约 100+ MB）、把老快照压缩成分钟级汇总表（增加复杂度）。

## D-012 单实例用命名互斥量

- 状态：已决定（设计）
- 背景：重复启动会导致双倍采样与双写数据库。
- 决策：`Mutex("Global\\HotspotMeter-{用户SID}")`（或 `Local\`，阶段 6 实测），第二个实例激活第一个并退出。
- 理由：Win32 原生、无需额外依赖；比端口占用/文件锁更直接。
- 影响：需要处理"激活已有实例"的进程间通信（可用命名管道或窗口消息，阶段 6 定）。

## D-013 不引入 Microsoft.Extensions.Hosting

- 状态：已决定
- 背景：桌面程序需要精确控制启动顺序与窗口生命周期。
- 决策：只用 `Microsoft.Extensions.DependencyInjection` + 手写启动流程（阶段 2/3 引入）。
- 理由：通用主机的后台服务模型与 WPF 的 UI 线程生命周期并不天然契合，额外抽象反而增加理解成本。
- 影响：需要自己管理启动/停止顺序（已在 03 文档第 6 节写明）。

## D-014 应用清单声明 PerMonitorV2 DPI 感知

- 状态：已决定（阶段 0 已加入 `app.manifest`）
- 背景：悬浮窗需在多显示器、不同缩放下保持清晰与命中正确。
- 决策：`dpiAware=true/pm` + `dpiAwareness=PerMonitorV2`，同时 `longPathAware=true`。
- 理由：WPF 在 PerMonitorV2 下可正确处理跨屏 DPI 变化；系统级 DPI 虚拟化会导致模糊。
- 影响：阶段 4 必须处理 `DpiChanged` 与跨屏重新布局；若发现兼容问题，可退回 System DPI（需记录）。

## D-015 建立 AGENTS.md 作为协作规则文件

- 状态：已决定（阶段 0）
- 背景：用户要求检查 DeepSeek Harness 是否有项目规则文件机制，且不能凭空假设。
- 决策：在仓库根建立 `AGENTS.md`（简洁规则 + 指向 `PROJECT_CONTEXT.md`）。
- 依据：`dsh-agent-instructions` 会从项目根向下加载 `AGENTS.md`/`CLAUDE.md` 链；实际验证——写入后本会话已自动收到该文件内容。
- 影响：规则文件会被自动注入，必须保持简短（有字节预算），详细内容放 `docs/`。

## D-016 字节格式化采用 1024 进制、最多两位小数

- 状态：暂定待确认（阶段 4 定稿）
- 背景：界面示例为 `2.36 GB`、`2.4 MB/s`、`128 KB/s`。
- 决策：`ByteSizeFormatter` 以 1024 换算，单位标签仍写 KB/MB/GB，格式 `0.##`（不补零）。
- 理由：与 Windows 资源管理器/任务管理器习惯一致；`0.##` 同时满足 `2.36 GB` 与 `128 KB` 两种示例。
- 影响：与运营商 GB（1000 进制）存在约 7% 的显示差异，需在口径说明中提示"按 1024 进制显示"。

## D-017 只按 InterfaceGuid 精确取一行，禁止按类型/别名/MAC 求和

- 状态：已决定（阶段 1 实测证据）
- 背景：`GetIfTable2` 会为同一块无线网卡返回多行（物理行 + WFP 过滤层行），VPN 适配器也会产生同值行。
- 决策：始终用 WLAN API 得到的 InterfaceGuid 在 `MIB_IF_ROW2` 中定位唯一一行；找不到就视为接口缺失，不做任何"近似匹配"。
- 证据：P1-8 实测 45 行中有 16 行 `Type=71`，其中 5 行与目标行计数完全相同；按类型求和得到 5,135,393,598 B，是真实值 855,898,933 B 的 **6.00 倍**。Radmin VPN 另有 4 行同值。
- 影响：采样器必须容忍"GUID 存在但行不存在"这一状态；不得用 SSID、Alias、MAC 作为回退匹配键。

## D-018 会话流量 = Σ 有效 delta，界面不显示网卡累计计数器

- 状态：已决定（阶段 1 实测证据）
- 背景：网卡计数器会在适配器禁用/驱动重置/重启后归零，直接显示累计值会让会话流量"跳回 0"。
- 决策：`SessionDownloadBytes/UploadBytes` 一律由每帧 delta 累加得到；网卡原始计数器只作为内部基线，不出现在 UI。
- 证据：P1-5 中计数器归零后界面显示 0，而真实会话增量仍为 8,830,748/2,167,194，两者相差 267,124 B。
- 影响：delta 必须逐帧持久化（阶段 3 的快照即"区间增量"），崩溃恢复才可能尽量少丢。

## D-019 测速只用 Stopwatch，墙钟只用于时间戳与自然日归属

- 状态：已决定（阶段 1 实测证据）
- 背景：用户可能改系统时间，Windows 也会做时间同步回调。
- 决策：两次采样之间的时间差取单调时钟；样本另存 `timestampUtc` 与 `gapMs` 两列/两字段，前者用于展示与跨日归属，后者用于速率与超时判断。
- 证据：P1-7 墙钟 +7,196,980 ms 跳变时，单调时钟仅 1,015 ms，速率无任何突变；-5,201 ms 的同步回调同样被吸收。
- 影响：阶段 3 的跨午夜拆分必须用"当时的本地时间"计算归属，不能用 gap 累加推算日期。

## D-020 非 Running 状态重建基线，恢复后需两帧确认

- 状态：已决定（阶段 1 实测证据）
- 背景：接口拆除瞬间可能读到 `0`，随后又恢复原值（P1-6 睡眠开始时即如此），直接相减会产生约 23 MB 幻影流量。
- 决策：进入 `InterfaceMissing` / `CounterReset` / `Suspended` / `Rebooted` 任一状态都丢弃基线；恢复后的**第一帧只作为基线、增量为 0**，第二帧起才继续累计；同时对"单帧增量超过链路速率上限"的情况记告警并放弃该帧。
- 理由：宁可少算一帧（≤1 秒），也不让幻影数据进入数据库与界面。
- 影响：阶段 2 的状态机与阶段 3 的快照写入都要显式表达"基线帧"。

## D-021 睡眠/长暂停判定：间隔 > 3×采样间隔，且循环按有效时长计时

- 状态：已决定（阶段 1 实测证据）
- 背景：睡眠时 Stopwatch 继续走；第一版原型用墙钟时长判停，唤醒瞬间直接退出，一帧都没采到。
- 决策：`gapMs > 3 × intervalMs` 视为长暂停，该帧增量 0、计入 `wakeRebuilds`、不计入有效时长；循环按累计有效时长结束。
- 证据：P1-6b 睡眠 94,534 ms 被正确识别，唤醒首帧增量 0，之后恢复正常。
- 影响：阶段 2 的 `MonitorEngine` 必须把"暂停/恢复"作为一等状态，而不是异常。

## D-022 重启识别：TickCount64 变小，或开机近似偏移超过 10 分钟

- 状态：已决定（阶段 1 实测证据，阶段 3 会再验证）
- 背景：重启后网卡计数器从 0 开始，若把当前读数当增量会重复累计历史流量。
- 决策：保存基线时记录 `TickCount64` 与"当前 UTC − TickCount64"得到的开机近似时刻；恢复时任一条件触发即判定 `REBOOT_DETECTED`，放弃恢复。
- 证据：P1-11 真机重启后 TickCount64 5,744,156 → 256,093、开机偏移 99.1 min，判定正确，未给出任何可恢复增量。
- 影响：误判方向是"放弃恢复"（少算），不会多算；长时间休眠可能造成误判，需在日志中说明。

## D-023 崩溃 gap 的字节可算、日期不可还原 → 恢复会话 + Recovered 标记

- 状态：倾向方案 B（阶段 3 定稿）
- 背景：P1-10 证明进程退出期间计数器继续增长，因此字节总量可精确恢复；但无法知道这些字节发生在哪一天/哪一秒。
- 决策：恢复时创建一个带 `Recovered` 标记的会话，字节计入**恢复当天**，并在统计窗口标注"含恢复流量"；不做跨日猜测。
- 理由：比丢弃更接近真实用量，又不会伪造历史日期；用户可解释差异来源。
- 影响：`ConnectionSessions` 需要 `IsRecovered` 字段与对应查询逻辑。

## D-024 验证原型 `tools/ApiProbe` 不加入解决方案

- 状态：已决定（阶段 1）
- 背景：原型依赖 P/Invoke 与命令行参数，与产品分层无关；但结论可复现、便于后续回归。
- 决策：源码入库、`bin/obj/out` 忽略；不加入 `HotspotMeter.sln`；阶段 2 只把它当参考实现，不引用其程序集。
- 影响：`dotnet build HotspotMeter.sln` 不会编译原型；需要时单独 `dotnet run --project tools/ApiProbe`。

## D-025 项目根目录迁移为 `E:\hotspotmeter\HotspotMeter`

- 状态：已执行（用户操作，2026-11-12）
- 背景：用户把项目从 `E:\HotspotMeter` 移到新位置；原处残留一个无关的克隆仓库（`bd9001a Initial commit`），造成嵌套 `.git`。
- 决策：把原项目 `.git` 迁入新目录、作为唯一仓库；克隆残留的 `.git` 移到 `E:\hotspotmeter-clone-git-backup` 保留，不直接删除。
- 影响：所有文档与命令中的路径以 `E:\hotspotmeter\HotspotMeter` 为准；旧路径下的残留目录待用户确认后清理。

## D-026 采样间隔默认 1000 ms，最小 500 ms

- 状态：已决定（阶段 2 实现）
- 背景：阶段 1 用 1000 ms 验证过稳定性；间隔太小会放大读取开销，也让"两帧确认"失效过快。
- 决策：`MonitorOptions.SamplingInterval` 默认 1 s，`Validate()` 拒绝 < 500 ms；测试若想加快只能改成 500 ms，不能绕过校验。
- 影响：引擎测试耗时约 2 s；UI 侧速度显示按 1 s 刷新即可。

## D-027 恢复判定顺序：先判"是否还在目标热点"，再判"计数行是否存在"

- 状态：已决定（阶段 2 实现，测试锁定）
- 背景：最初先判行缺失，导致"未连任何 Wi-Fi"被误报为 `InterfaceMissing`。
- 决策：Tick 回退 → 目标匹配失败（NotOnTarget）→ 行缺失（InterfaceMissing）→ LUID 变化 → 计数回退 → 开机偏移 → Continued。
- 影响：恢复时检测器抛异常（例如网卡被禁用、`WlanQueryInterface` 返回 1168）按 `InterfaceMissing` 处理，不当作"连了别的网络"。

## D-028 Stop 时快照清零，UI 的上次会话数据来自 SessionEnded 事件

- 状态：已决定（阶段 2 实现，阶段 4 使用）
- 背景：`StopAsync` 会结束会话，`MonitorSnapshot` 随之为 `HasSession=false`、累计 0。
- 决策：引擎不在快照里保留"上一次会话"；阶段 4 的悬浮窗订阅 `SessionEnded`（含 Download/Upload）保存展示值，并标注"上次会话"。
- 影响：UI 不能只读当前快照来显示"未连接时的上次流量"；这是刻意保持 Core 语义简单。

## D-029 疑似超限增量默认只告警，不丢弃

- 状态：已决定（阶段 2 实现）
- 背景：链路速率来自 WLAN 报告值（kbps→bps），与实际吞吐不完全一致；用未标定阈值丢流量会静默少算。
- 决策：`PlausibilityMode.Warn` 为默认：保留字节并发 `ImplausibleDelta` 告警；`Drop` 必须显式开启，供阶段 7 对照实验；无链路速率时不做上限判断。
- 影响：告警可能偏多，但不会因为阈值猜错而丢账。

## D-030 状态机在 Core、平台 IO 在 Infrastructure

- 状态：已决定（阶段 2 实现）
- 背景：需要"假时钟 + 假计数器"就能完整测试所有异常路径。
- 决策：`TrafficAccumulator`（会话/基线/确认帧/告警）与 `BaselineResumeEvaluator`（纯判定）放 Core；`MonitorEngine`（循环、事件、恢复编排、互斥）放 Infrastructure，只做 IO 与调度。
- 影响：Core 保持零 Windows 依赖；引擎测试仍需真实 `Task.Delay`（可接受），纯逻辑测试全部无 IO。

## D-031 恢复 gap 只带"连续性可信状态"，默认不自动并入原会话

- 状态：已决定（用户 m00752 要求）
- 背景：同开机、同接口、计数递增只能证明"读数连续"，不能证明期间没有断开热点、连过别的 Wi-Fi 再连回。
- 决策：`RecoveryGap.Continuity ∈ {Continuous, Unverified, Interrupted}`；只有 `Continuous` 才 `MayMergeIntoSession` 并触发 `SessionResumed`；否则只发 `RecoveryCandidateDetected`，快照暴露 `HasUnmergedRecovery/UnmergedRecoveryBytes`。
- 影响：阶段 2 默认证据源恒为 `Unverified`，所以默认行为是"算出候选但不入账"；阶段 3 决定候选如何持久化与展示。

## D-032 恢复幂等：稳定 RecoveryId + 进程内 HashSet + 串行化

- 状态：已决定（用户 m00757 要求）
- 背景：重复调用 `ResumeAsync`（启动重试、界面重连、并发调用）不得重复并入或重复持久化。
- 决策：`RecoveryId = {sessionId}:{guid:N}:{utcTicks}:{inOctets}:{outOctets}`；`MonitorEngine` 用 `SemaphoreSlim` 串行化恢复、用 `HashSet<string>` 记录已处理 ID，第二次返回 `AlreadyApplied=true`；阶段 3 再加数据库唯一索引。
- 影响：ID 包含基线计数，计数器变化后同一会话的另一次恢复会得到不同 ID（这是期望行为，代表不同的恢复窗口）。

## D-033 `tools/MonitorHost` 不入解决方案，但引用产品项目

- 状态：已决定（阶段 2 实现）
- 背景：需要一个能真机比对的控制台宿主，又不能把原型逻辑复制一遍。
- 决策：`tools/MonitorHost` 用 `ProjectReference` 引用 Core/Infrastructure，`IsPackable=false`，不加入 `HotspotMeter.sln`；参数 `--list/--ssid/--guid/--seconds/--json`。
- 影响：`dotnet build HotspotMeter.sln` 不编译它；需要时单独 `dotnet run --project tools/MonitorHost`。

## D-034 引擎循环用 `Task.Delay(剩余时间)`，不用 `PeriodicTimer`

- 状态：已决定（阶段 2 实现）
- 背景：单次采样耗时（WLAN 查询 + 接口表读取）可能接近或超过间隔；`PeriodicTimer` 在回调变慢时不会补偿，容易积压。
- 决策：每轮结束后按"间隔 − 本轮耗时"计算延迟，耗时超过间隔则立即进入下一轮；循环整体受 `CancellationToken` 控制。
- 影响：实际采样周期略大于配置值；速度仍用两次采样的真实单调时间差计算，不会虚高。

## D-035 F-1：未确认恢复必须切开会话，不得复用原 SessionId

- 状态：已决定并实现（阶段 2 收尾修正，用户 m01067/m01093 批准）
- 背景：原实现对 Unverified/Interrupted 只在快照里标记"有未确认候选"，会话本身仍是原 SessionId，
  恢复后的新流量继续累加到旧会话与旧 `StartedAtUtc` 上；非 Continued 的五个结局还会静默丢弃旧会话。
  这违反了"当前会话流量只属于当前连接"的产品定义，也让阶段 3 无法正确落库。
- 决策：
  1. `SessionEndReason` 追加 `RecoveryUnconfirmed`（Unverified/Interrupted）与 `RecoveryRejected`（其余非 Continued）。
  2. `MonitorSessionEndedInfo` 增加可选 `ResumeOutcome`，正常结束为 null，恢复结束必须带具体结局。
  3. Unverified/Interrupted 在 `gap.FromUtc` 结束旧会话，同一 `MonitorStep` 内以新 SessionId 开新会话（累计 0），
     gap 只作独立候选保留；后续 delta 只进新会话。
  4. 非 Continued 结局在 `Baseline.CapturedAtUtc` 结束旧会话并清理状态。
  5. `_lastClosedSessionId` 水位保证同一 SessionId 只结束一次（正常与恢复路径共用）；
     SessionId 严格递增，Int64 上限抛异常，绝不复用。
  6. Continuous 路径不变：沿用原 SessionId、合并 gap、发 `SessionResumed`，不产生 `SessionEnded`。
- 影响：`ResetForResume` 签名改为 `(ResumeContext, ResumeOutcome, DateTimeOffset)`；
  阶段 3 落库必须遵守 `docs/06` §3.1 的六条约束（唯一索引、原始精度、更新而非忽略、同事务、不入日统计、失败不丢候选）。