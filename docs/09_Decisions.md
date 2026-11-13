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

## D-036 阶段 3A 只做基础设施，业务表推迟到 3B 冻结

- 状态：已决定并实现（阶段 3A，用户 m01299 授权）
- 背景：第二轮规划的 7 张表仍有跨日快照分段、基线更新、恢复候选更新策略等未定项，
  若在基础设施阶段就把 DDL 写死，后续修订需要迁移已落库的结构与数据。
- 决策：3A 只建立路径、连接工厂、迁移器、健康检查、隔离、单实例、Writer 骨架；
  Schema v1 只有 `SchemaInfo` 元数据表；业务表在 3B 前完成审核后以 v2 迁移加入。
- 影响：3A 的"可运行"表现为"能安全建库、检查、隔离、串行写入"，而不是"开始记录流量"。

## D-037 Schema 版本唯一权威是 `PRAGMA user_version`

- 状态：已决定并实现
- 背景：如果 SchemaInfo 里再存一份 `schema_version`，迁移事务失败或手工修库时两份版本会分叉，
  程序无法判断该信哪一个。
- 决策：迁移器只读写 `PRAGMA user_version`；SchemaInfo 只保存 `created_at_utc`、
  `initialized_at_utc`、`app_version` 等元信息。每个版本的 DDL、种子数据与版本号写入
  在同一事务内提交；数据库版本高于程序支持版本时抛异常拒绝写入。
- 影响：新增迁移必须同时改 `SchemaMigrator` 的迁移列表与 `SupportedVersion`，测试会校验连续性。

## D-038 3A 的 Writer 只做单队列 FIFO，不做优先级

- 状态：已决定并实现（用户明确禁止在 3A 采用"两条独立队列 + Critical 优先"）
- 背景：恢复、检查点、跨日快照之间存在顺序依赖；优先级调度会让同一会话的事件乱序，
  而流量统计最怕的就是"先写结束、后写开始"。
- 决策：`SqliteWriter` 使用单消费者 Channel，先进先出，每项独立事务；`StopAsync` 先关闭入口、
  排空队列，再释放连接；连接只在写入线程内可见，外部无法绕过队列直接写库。
  多队列/合并/重试策略等 3B 的事务边界确定后再设计。
- 影响：写入吞吐由单连接串行决定；阶段 2 的 1 s 采样频率远低于该上限，不构成瓶颈。

## D-039 损坏数据库整体隔离，隔离失败则拒绝启动

- 状态：已决定并实现
- 背景：WAL 模式下主库之外还有 `-wal`/`-shm`，回滚日志模式下有 `-journal`。
  只移动主文件会留下孤儿日志，新库创建后可能被误当成自己的日志，产生更隐蔽的损坏。
- 决策：`DatabaseQuarantine` 把四类文件整体移入 `corrupt-{时间戳}` 目录；移动失败时尝试还原，
  然后抛 `DatabaseQuarantineException`，调用方必须停止初始化，禁止在原名创建新库。
  不删除任何损坏文件，也不实现未经验证的只读降级。
- 影响：健康检查失败时程序会启动失败并留下可追溯的隔离目录；这是有意的"失败要响亮"。

## D-040 单实例用 `Local\` 命名 Mutex，并在进程内额外去重

- 状态：已决定并实现
- 背景：文件锁只能防止同时写同一文件，挡不住第二个实例重复采样、重复累计；
  而 Windows Mutex 对"持有它的线程"是可重入的，同一进程内重复获取会成功。
- 决策：名称固定为 `Local\HotspotMeter.SingleInstance.v1`（数据库按用户隔离，故用 Local 命名空间）；
  获取时处理 `AbandonedMutexException`（上次异常退出允许重新启动）；
  进程内用静态集合记录已持有名称，防止同进程重复获取。
- 影响：多用户同时登录时各自运行一套实例，符合 `%LOCALAPPDATA%` 的数据布局。

## D-041 跨午夜拆分用 BigInteger 累计取整分配

- 状态：已决定并实现（阶段 3B-4）
- 背景：按比例拆分时若用 `double`/`decimal` 或先做乘法，长区间与大流量下会出现
  舍入误差；而"每日总量之和必须等于会话总量"是统计口径的硬要求。
- 决策：`NightlySplitter` 对下载字节、上传字节、连接毫秒**分别**做三次独立分配，
  每次用 `BigInteger` 累计：`分配_i = floor(总 * 累计时长_i / 总时长) - 已分配`，
  最后一段吃掉全部余数，保证 `Σ = 输入` 严格成立；`long.MaxValue` 边界由 BigInteger 承载。
- 影响：拆分结果不依赖浮点环境；单日段数超过 `maxSegments=400` 时抛异常而不是静默截断。

## D-042 本地日边界用 UTC 分段 + tick 二分求解

- 状态：已决定并实现（阶段 3B-4）
- 背景：直接用"本地时间加减 24 小时"在 DST 日会错一小时；而 Windows 不提供
  "某 UTC 时刻的本地日边界"查询。
- 决策：`LocalDayResolver` 先在区间前后各 2 天窗口内以 15 分钟粗采样发现 UTC 偏移变化，
  再在变化区间内按 Ticks 二分到 **1 tick** 精度，得到恒定偏移段；段内把边界换算成本地时间，
  求本地午夜对应的 UTC。重复午夜取最小 UTC；午夜落在前跳缺口时取跳变瞬间；
  整日被跳过的日期不产生片段。
- 影响：DST 23/25 小时日、Samoa 整日跳过等场景由测试固定；精度是 tick 而非秒。

## D-043 EffectiveTimePolicy 做事件级计数，DailyTraffic 仓储保持非幂等

- 状态：已决定并实现（阶段 3B-4）
- 背景：①墙钟可能被 NTP/用户连续小幅回拨，若按"每次差值"计数会统计出成百次调整；
  ②日汇总的幂等必须由"快照写入 + 日汇总"同一事务保证，若仓储层偷偷去重，
  反而会掩盖上游重复投递的问题。
- 决策：`EffectiveTimePolicy` 维护 `None/Backward/Forward` 状态机与 2 s 容差，
  同一方向的持续偏差只计一次事件，恢复后再次偏差才计下一次；单调时钟回退单独计数；
  所有推进与计数用 `checked`，越界抛 `OverflowException`。
  `DailyTrafficRepository.ApplyAsync` 明确**不幂等**：只做增量累加与 MIN/MAX 端点，
  不自开事务、不重试；调用方必须把它和快照插入放在同一事务里。
- 影响：测试锁定"重复应用翻倍"的语义；3B-5/3B-7 若忘记事务边界会被该测试立刻暴露。

## D-044 操作级幂等用独立 `TrafficOperations` 行承载，不扩宽 `TrafficSnapshots`

- 状态：已决定并实现 v3 表（阶段 3B-5a）；写入路径未接入
- 背景：`TrafficSnapshots` 的唯一键只有 `(OperationId, SegmentIndex)`，无法保存"这次操作请求的
  完整内容""应用后的水位终值""操作序号"等操作级事实；把这些字段塞进每个分段会重复 400 份。
- 决策：新增 `TrafficOperations`，一行对应一次逻辑写入，分段仍在 `TrafficSnapshots`；
  两表都以 `OperationId` 关联。`UNIQUE(OperationId)`、`UNIQUE(DbSessionId, OperationSequence)`
  分别防重复操作与会话内序号分叉；`SegmentCount 1..400` 与 `OperationType IN (1,2,3)` 由 CHECK 锁定。
  该表用复合外键 `(DbSessionId, ProfileId)` 与会话绑定，防止跨 Profile 挂接。
- 影响：请求去重、载荷核验、水位推进都围绕操作行完成；重置标记（Kind=4）不预埋，
  将来需要零分段操作时走 v4 迁移重建表，避免现在留下永远为 NULL 的列。

## D-045 UTC 时间戳用定长 33 字符文本 + 实测加固的 CHECK

- 状态：已决定并实现（阶段 3B-5a）
- 背景：SQLite 的日期函数比想象中宽松。实测 3.53.3：`2026-11-13T24:00:00.0000000+00:00`
  被当作合法时间且 `strftime` 往返相等；`29:00:00` 让 `strftime` 返回 NULL，
  而 `NULL = ...` 在 CHECK 中不算 FALSE，于是直接放行。
- 决策：所有时间列统一存 `yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'`（33 字符、零偏移）；
  CHECK 同时要求长度、GLOB 形状、年月日时范围、`strftime(...) IS NOT NULL` 与
  `strftime(...) = substr(x,1,19)`。C# 侧由 `UtcTimestamp` 统一格式化/解析并做往返校验。
- 影响：上述两个漏洞都有集成测试钉住；未来升级 SQLite provider 若语义变化会先失败。
  代价是时间列不可读性略差于 ISO 变体，但可比较、可索引、跨时区无歧义。

## D-046 摘要用二进制规范化编码，不用分隔符字符串

- 状态：已决定并实现（阶段 3B-5a）
- 背景：`TimeZoneId`、`OperationId` 等是自由文本；若用 `|` 拼接，
  构造含分隔符的字符串就可能让两组不同输入产生相同摘要。
- 决策：摘要输入按"版本字节 + 域串长度前缀 + 大端定长整数 + 长度前缀 UTF-8 字符串"编码，
  域前缀区分请求（`HMOPS-REQ`）与载荷（`HMOPS-PAY`）；输出 SHA-256 小写十六进制。
  请求摘要排除 `OperationId`/`OperationSequence`/`CapturedAtUtc`/分段（重试不应改变请求身份），
  载荷摘要覆盖全部落库字段（供 3D 从行重算比对）。
- 影响：编码是确定性的、与时区无关、字段边界无歧义；测试覆盖逐字段敏感性与分隔符注入。

## D-047 胶囊窗口定位用 DPI 感知的 Win32 工作区

- 状态：已决定并实现（阶段 4C-1）
- 背景：本机 150% 缩放。实测 `SystemParameters.WorkArea` 返回的是物理像素宽度（1707），
  而 WPF 的 `Left/Top` 单位是 DIP；把 1707 当 DIP 会把窗口推到屏幕右侧之外。
- 决策：用 `MonitorFromWindow` + `GetMonitorInfo` 取当前显示器 `rcWork`（物理像素），
  除以 `VisualTreeHelper.GetDpi` 的比例换算成 DIP；虚拟屏用 `GetSystemMetrics`
  （`SM_XVIRTUALSCREEN` 等）同样换算后再夹取；首次运行放工作区右下角，恢复记忆位置后仍夹取。
- 影响：1.5 DPI 下窗口落在 DIP (1362.7, 890.7)，对应物理 (2044, 1336)，在 2560×1600 屏内。
  多显示器、DPI 切换、任务栏位置变化后的重新定位仍需实测（见阶段 4 验收项）。

## D-048 目标热点暂取“已连接且信号最强”，不按 SSID 猜测

- 状态：临时策略（阶段 4C-1），待设置阶段替换
- 背景：设置与热点配置服务尚未实现，但胶囊需要真实数据才能开发。
- 决策：`MonitorSessionService` 只在“已连接”的候选中按 `SignalQuality` 取第一条建
  `HotspotTarget`；`ProfileId` 暂时 `Guid.NewGuid()`；不把其他 Wi-Fi 的流量计入，
  也不通过 SSID 匹配历史网络。
- 影响：当前只监控一个连接；用户切换热点时 SSID/GUID 变化会结束旧会话并开新会话。
  正式版必须由用户显式选择并持久化 Profile（阶段 4C-3/6）。

## D-049 开发期 UI 诊断用环境变量开关，不进入产品路径

- 状态：已决定并实现（阶段 4C-1）
- 背景：本次会话视觉引擎不可用（`read_image`/`modlens` 均失败），无法靠截图肉眼确认布局。
- 决策：`UiDump` 读取 `HOTSPOTMETER_UI_DUMP=<path>`，可选
  `HOTSPOTMETER_UI_DUMP_DELAY_MS`（默认 2500）与 `HOTSPOTMETER_UI_DUMP_EXIT=1`，
  在延迟后用 `DispatcherTimer` 写出 DPI、窗口位置、每个 `TextBlock` 的文本与相对边界。
  未设置变量时不注册定时器、不写文件。
- 影响：证据是“渲染元素的真实坐标”，不能替代人眼审美判断；截图改用 `PrintWindow`
  （分层窗口在部分环境不会被 `CopyFromScreen` 捕获）。该开关只用于开发验证，不属于产品功能。

## D-050 胶囊用代码后置 + ViewModel，不引入 UI 框架

- 状态：已决定并实现（阶段 4C-1）
- 背景：胶囊的视觉简单但窗口行为（拖动/固定/定位）必须可控；引入第三方 UI 库会扩大依赖面。
- 决策：圆角与图标用 `Border` + `Path`；右键菜单用 `ContextMenu`；拖动用 `DragMove()`；
  位置与固定状态存 `%LOCALAPPDATA%\HotspotMeter\ui-state.json`；业务状态只放在
  `CapsuleViewModel`，窗口不接触 WLAN/计数器/数据库。
- 影响：依赖最少、可在普通 WPF 上维护；统计与设置窗口后续复用 `Theme.xaml` 的色板与菜单样式。
## D-051 托盘用 WinForms NotifyIcon，不引入第三方托盘库

- 状态：已决定并实现（阶段 4C-2）
- 背景：WPF 没有系统托盘 API；第三方库会扩大依赖面，而托盘需求只是图标 + 右键菜单。
- 决策：App 项目启用 `UseWindowsForms`，用 `System.Windows.Forms.NotifyIcon`；菜单用
  `ContextMenuStrip` + 自绘 `ToolStripRenderer` 套深色主题；`Dispose` 时先 `Visible=false` 再释放。
- 影响：多一个框架开关与 `WFO0003` 抑制（DPI 由 WPF manifest 负责）；托盘与 WPF 窗口共享同一 UI 线程，
  菜单回调必须保持轻量。

## D-052 设置入口只放在托盘，胶囊菜单固定四项

- 状态：已决定并实现（阶段 4C-2）
- 决策：胶囊右键菜单为“流量统计（暂禁用）/ 固定位置或解除固定位置 / 调整大小… / 退出程序”；
  设置入口只在托盘与托盘菜单，避免两个入口并存造成状态不同步。
- 影响：隐藏/显示胶囊也只放在托盘；胶囊菜单保持“显示与退出”的语义。自检断言菜单不含“设置”。

## D-053 Window.Width/Height 不用绑定，改为代码赋值

- 状态：已决定并实现（阶段 4C-2，缺陷修复）
- 背景：把 `Width/Height` 绑定到 ViewModel 后，窗口实际高度变成 320×744.7，且 `ApplySize` 无法改变窗口尺寸；
  `GetBindingExpression` 返回空，说明窗口布局流程覆盖/脱离了该绑定。
- 决策：XAML 保留初始字面量 320×104；`DataContextChanged` 与 `ApplySize` 直接设置 `Window.Width/Height`，
  ViewModel 只提供尺寸与派生字号/边距。
- 影响：尺寸切换真实生效（280×92 / 320×104 / 400×130 / 自定义）；后续窗口尺寸调整沿用同一做法。

## D-054 图标程序化生成，图标用 DIB 帧而非 PNG 帧

- 状态：已决定并实现（阶段 4C-2）
- 背景：视觉引擎不可用，无法手工绘制/校对位图；`System.Drawing.Icon` 对 PNG 压缩帧兼容性差。
- 决策：`tools/IconGen/generate-icon.ps1` 按几何参数绘制深黑圆角底板 + 绿色 Wi-Fi 弧/圆点 + 上窄下宽梯形，
  输出 16/20/24/32/40/48/64/128/256 的 DIB 帧；同时输出 256px PNG 供窗口图标与文档使用。
- 影响：图标可重复生成；16px 下形状经像素统计确认可辨。脚本仅供开发，不参与构建。

## D-055 右键菜单闪退：容器样式不能指向 MenuItem，改为 Resources 隐式样式

- 状态：已决定并实现（阶段 4C-3，缺陷修复）
- 背景：真实右键打开胶囊菜单时进程崩溃，`%LOCALAPPDATA%\HotspotMeter\logs\crash-*.log` 记录
  `InvalidOperationException: 用于类型"MenuItem"的样式不能应用于类型"Separator"`，堆栈为
  `ItemsControl.ApplyItemContainerStyle` ← `Panel.GenerateChildren` ← `Popup.CreateWindow` ← `ContextMenu.OnIsOpenChanged`。
- 根因：`ContextMenu.ItemContainerStyle` 会把同一个 `TargetType=MenuItem` 的样式套到菜单里的所有容器，
  包括 `Separator`；菜单第一次布局就抛异常。这不是"偶发弹窗问题"，而是每次打开必崩。
- 决策：删除 `ItemContainerStyle`，把样式放进 `<ContextMenu.Resources>` 作为隐式 `MenuItem` 样式
  （`<Style TargetType="MenuItem" BasedOn="{StaticResource CapsuleMenuItemStyle}" />`），`Separator` 使用默认样式。
- 备选与否决：不改 `Separator` 为自定义控件（会改变菜单视觉）；不捕获菜单异常（会掩盖真实缺陷，违反"找根因"要求）。
- 影响：菜单三开三关无异常；新增 `CrashLog` 仅记录未处理异常堆栈，不吞异常。

## D-056 胶囊四档统一比例缩放，最小宽度 220 DIP

- 状态：已决定并实现（阶段 4C-3）
- 背景：用户要求保留现有胶囊布局，只做整体等比例缩小；原"小型 280×92"与 320×104 比例不一致（3.043 vs 3.077）。
- 决策：以 320×104 为基准比例；预设迷你 220×71.5 / 小型 280×91 / 标准 320×104 / 大型 400×130；
  自定义范围 220–480 × 71.5–156，宽高比容差 1.2%；缩放系数 `clamp(min(W/320, H/104), 0.65, 1.6)`。
  所有视觉量由同一系数派生（圆角 20s、内边距 18/15/18/13s、状态 13s、图标 17s、流量 34s/行高 40s、
  说明 11s、速度 11s、间距 7s/4s/10s），布局结构与元素集合不变。
- 备选与否决：不做一套独立"迷你单行布局"（用户明确禁止）；不做隐藏元素的响应式降级（会改变信息层级）。
- 影响：迷你档字号约 8.9–23.4，仍在窗口内且无重叠（自检 `layout=ok`）；220 DIP 是可用性与比例的下限。

## D-057 布局正确性用矩形断言，而不是肉眼/视觉模型

- 状态：已决定并实现（阶段 4C-3）
- 背景：本会话视觉引擎不可用，无法可靠目检截图；文字重叠/越界需要可重复证据。
- 决策：`UiDump.CheckLayout(window)` 枚举全部 `TextBlock`，计算相对窗口的矩形，断言
  ①所有矩形在窗口内（容差 0.5 DIP）；②任意两矩形交集面积不超过 0.5×0.5 DIP。结果写入自检日志。
- 影响：四档尺寸 + 自定义尺寸均输出 `layout=ok texts=7`；该检查与截图并存，截图仍作为人工观感依据。

## D-058 任务栏检测优先 Win32 AppBar，失败走工作区推断

- 状态：已决定并实现（阶段 4C-4）
- 背景：需要把胶囊自动停到任务栏附近，但任务栏可能位于四边、可能自动隐藏、可能跨多显示器且每屏 DPI 不同；
  用分辨率或固定经验值猜任务栏尺寸不可靠。
- 决策：`TaskbarDetector` 组合 `SHAppBarMessage(ABM_GETTASKBARPOS)` 取任务栏矩形与边缘、
  `SHAppBarMessage(ABM_GETSTATE)` 判自动隐藏、`MonitorFromWindow`/`MonitorFromRect` 定位显示器、
  `GetMonitorInfo` 取显示器与工作区、`GetDpiForMonitor`（失败退 `GetDpiForWindow`）取 DPI。
  任一步失败或返回非法矩形时，回退到"主显示器 + 工作区差异推断边缘"，并把来源写入检测描述，绝不因检测失败崩溃。
- 备选与否决：不用 `SystemParameters.WorkArea`（只给主屏、物理像素语义易混）；不读注册表任务栏配置（版本差异大且是内部细节）；
  不做 UI Automation 探测开始按钮（脆弱且与适配目标无关）。
- 影响：本机实测 `底部任务栏 · 150% · 工作区 1707×1019 DIP（SHAppBarMessage(ABM_GETTASKBARPOS)）`；
  修复了 `GetMonitorInfo` 未设 `cbSize` 导致恒失败的实现缺陷。

## D-059 纯几何计算放 Core，App 只负责取数与落地

- 状态：已决定并实现（阶段 4C-4）
- 背景：停靠/缩放规则需要覆盖四边、自动隐藏、多显示器、200% DPI、空间不足等组合，放在窗口代码后置里无法稳定测试。
- 决策：`HotspotMeter.Core/Layout/TaskbarLayout.cs` 定义 `TaskbarLayoutInput` / `CapsuleLayoutOptions` / `CapsulePlacement`，
  纯函数 `Compute` 完成全部像素/DIP 换算与放置决策；App 的 `TaskbarDetector` 只采集输入，`CapsuleWindow` 只应用结果。
  Tests 项目不引用 App，但可直接覆盖 12 个布局场景。
- 规则要点：`scale = min(1, 可用宽/(320·dpi), 可用高/(104·dpi))`；`scale ≥ 0.6875` 时贴任务栏并保持比例，
  否则退到对侧角使用最小尺寸 220×71.5；工作区连最小尺寸都放不下时标记 `MinimumForced` 并如实说明。
- 影响：新增 12 个单元测试（全套 364/364）；四边与 200% 场景在真机自检中与期望坐标完全一致。

## D-060 自动适配与手动配置的优先级：手动优先，开启时备份

- 状态：已决定并实现（阶段 4C-4）
- 背景：自动适配会改变尺寸与位置，不能悄悄覆盖用户已保存的手动尺寸/位置；固定状态又代表用户明确要求不动。
- 决策：①固定位置时自动适配只更新检测结果、不移动窗口；②开启自动适配时把当前手动 Left/Top/Width/Height
  备份到 `Manual*` 字段；③关闭自动适配时恢复 `Manual*`；④用户手动改尺寸或拖动胶囊会关闭自动适配并落盘；
  ⑤自动适配运行期间 `PersistState` 不写 Width/Height，避免把自动结果当成手动配置。
- 备选与否决：不做"自动适配写入 Width/Height"（会污染手动配置）；不在固定状态强行重排（违背用户意图）。
- 影响：自检验证 400×130 → 开自动适配变 320×104 → 关闭恢复 400×130；`AutoFitTaskbar` 正确写入 `ui-state.json`。

## D-061 速度区改为流量数字右侧的两行竖排

- 状态：已决定并实现（阶段 4C-5）
- 背景：用户要求下载/上传从"右下角横排"移到"流量数字右侧、上下竖排"，且四档尺寸都不能挤压或裁切。
- 决策：`CapsuleWindow.xaml` 的 Row1 改为两列；右列 `SpeedBlock` 是纵向 `StackPanel`，内含两个三列 `Grid`
  （方向符 / 标签 / 数值），两行用 `SharedSizeGroup` 共享列宽，保证两行标签与数字对齐。
  间距全部由 `SpeedBlockMargin`、`SpeedArrowMargin`、`SpeedLabelMargin`、`SpeedRowSpacing` 按缩放系数派生。
- 备选与否决：不用单个 `Grid` 两行共享列宽再叠加行间距（需要为每个元素组合边距，绑定更复杂）；
  不缩小/隐藏速度文字来适配迷你档（会丢信息）。
- 影响：自检四档均 `layout=ok texts=9`，且 `speedBlock ... stacked=True inside=True`；最小档 220×71.5 下
  速度块 44×22 DIP 仍完整落在窗口内。

## D-062 滚动条用完整 ControlTemplate，而不是只改颜色

- 状态：已决定并实现（阶段 4C-5）
- 背景：设置窗口内容高度超过可视区，系统默认滚动条会露出白色轨道/白色边角，与深色卡片冲突。
- 决策：在 `Theme.xaml` 定义 `DarkScrollBarStyle`（宽 10 DIP）+ 垂直/水平 `ControlTemplate`：轨道 `#1C1C20`、
  滑块圆角 3、悬停/拖动三档颜色，翻页按钮透明并显式绑定 `ScrollBar.PageUp/PageDown/PageLeft/PageRight`，
  隐式样式覆盖全部 `ScrollBar`。
- 影响：自检读取到 `width=10 visible=True thumbFill=#FF3A3A3F`，`ScrollableHeight=277.3`；
  滚轮、拖动、键盘与触控板行为仍由 `ScrollViewer`/`ScrollBar` 原生命令处理，未自定义输入逻辑。

## D-063 右键菜单白色区域：替换 ContextMenu 模板，而不是只改 Background

- 状态：已决定并实现（阶段 4C-5）
- 背景：仅改 `ContextMenu.Background` 仍会出现白底/白边——白色来自系统主题的 `ContextMenu` 默认模板
  （外框、Popup 边角与滚动区域）。
- 决策：`CapsuleContextMenuStyle` 替换整个 `ContextMenu` 模板：圆角 8 的 `Border`（`#1F1F23` 背景、
  `#3A3A3F` 1 DIP 边框）+ `ScrollViewer` + `ItemsPresenter`；`MenuItem` 与 `Separator` 用
  `ContextMenu.Resources` 中的隐式样式，继续避免 `ItemContainerStyle`（D-055）。
- 影响：自检连续开关 10 次无异常；菜单背景 `#FF1F1F23`、边框 `#FF3A3A3F`；菜单项文字/禁用态/分隔线均为深色。

## D-064 自动适配改为"靠任务栏左/上侧"，并修正遮挡

- 状态：已决定并实现（阶段 4C-5）
- 背景：4C-4 的自动适配确实执行了，但横向任务栏右对齐后位置约 (1378.7, 906.7)，与手动默认右下角
  (约 1363, 891) 几乎重合，且 `MaxScale=1` 在 150% 单屏下不改变尺寸，所以看起来"没有效果"。
- 决策：`PlaceNearEdge` 改为横向任务栏靠左（`work.Left + margin`）、纵向靠上（`work.Top + margin`）；
  与任务栏之间保留 12 DIP；这样自动结果与手动位置有明确区别，也不会压住任务栏中央的主要交互区。
- 备选与否决：不做"强制覆盖任务栏"的置顶（Shell 对任务栏层级有特殊规则，且用户明确要求不破坏系统）；
  不隐藏/缩小信息来"让位置更好看"。
- 影响：真机自动停靠到 (8, 902.7)；单元测试期望同步更新（底部 8/908、顶部 8/54.7、左侧 54.7/8、右侧 1332/8）。

## D-065 夹取用工作区，置顶用 SetWindowPos 且不抢焦点

- 状态：已决定并实现（阶段 4C-5）
- 背景：旧 `ClampToScreen` 用虚拟屏幕边界，窗口可以被拖到任务栏上，点击任务栏后就被任务栏盖住；
  旧 `ShowCapsule` 用 `Topmost=false→true` 重新激活，可能抢焦点。
- 决策：`ClampCurrentPosition` 以当前显示器工作区（`GetWorkAreaInDips`）为边界，拖动结束也夹取；
  `EnsureTopmost` 调 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`，
  只调 Z 序、不激活窗口；`ApplyPlacement` 不再二次夹取（避免跨屏被拉回主屏）。
- 影响：自检真实停靠 `left=8 top=902.7` 与 `Compute` 完全一致；拖动/显示后位置稳定，隐藏再显示位置尺寸不变。

## D-066 多屏检测以窗口所在显示器为准

- 状态：已决定并实现（阶段 4C-5）
- 背景：旧 `TaskbarDetector` 总是取"任务栏所在显示器"（主屏），副屏上的胶囊会被算成主屏布局，
  多屏下自动适配位置错误。
- 决策：`GetTargetMonitor` 先用 `MonitorFromWindow` 取胶囊所在显示器；只有当任务栏矩形确实属于该显示器
  （矩形相交）时才采用，否则走工作区推断；自动隐藏判断仅在任务栏属于当前显示器时置位。
- 影响：自检 `secondMonitor` 合成场景 expected 与 actual 完全一致；真实单屏行为不变。

## D-067 速度块与流量数字用同一行 + VerticalAlignment 对齐，而不是靠 Margin 微调

- 状态：已决定并实现（阶段 4C-6）
- 背景：用户要求速度块整体的垂直中心与**流量数字控件本身**的中心对齐；基准不是"数字 + 已使用流量"的
  整个左侧区域，也不是胶囊中心。旧实现把左侧做成一个 `StackPanel`，速度块相对这个整体居中，天然偏低。
- 决策：Row1 改成两行两列的 `Grid`：流量数字与速度块都放在第 0 行、都用 `VerticalAlignment="Center"`。
  行高由两者中较高者决定（数字的行高 40s 通常更高），两个控件的中心因此由布局保证相等；说明文字独占第 1 行，
  不再参与速度块的对齐基准。没有为对齐写任何固定 Margin。
- 细节：速度块设置 `UseLayoutRounding="False"`，避免在 150% 等缩放比下 `5 DIP` 的居中偏移被舍入成
  `5.33 DIP`，从而保证控件矩形中心严格相等（禁用舍入只影响速度块的亚像素排布，不影响字号与主题）。
- 验证：`UiDump.CheckSpeedAlignment` 输出 `trafficCenterY / speedCenterY / delta`，四档与自定义尺寸
  实测 `delta=0`；`layout=ok texts=9` 仍通过（速度文本未与其他文本重叠）。
- 备选与否决：不做"给速度块加负数/经验 Margin"（缩放档位变化即失效，也违反用户约束）；不把速度块改成
  绝对定位（不随 DPI 与尺寸自适应）。

## D-068 不把胶囊嵌入任务栏内部：保留独立置顶窗口

- 状态：已决定（阶段 4C-7 可行性验证；未集成到产品）
- 背景：用户希望胶囊显示在任务栏矩形内部。查公开文档：`Taskbar Extensions` 只覆盖 Jump List、
  缩略图工具栏、图标叠加、进度条、通知区域；DeskBand（`IDeskBand2`）是旧机制，文档未声明 Windows 11
  支持，且需要把 COM DLL 加载进 Explorer。没有公开的"第三方任意 UI 嵌入任务栏"接口。
- 实验（`tools/TaskbarOverlayProbe`，独立项目、不引用产品项目）：2560×1600、150% DPI 下任务栏为
  `0,1528,2560,1600`（高 72 px），把 197×64 px（131.3×42.7 DIP）的置顶透明窗口放在 `(249,1532)`：
  `WindowFromPoint` 命中探针、z 序在任务栏之上、点击/拖拽/右键菜单均可用；切到 `Progman` 后仍在任务栏之上。
- 但可靠性证据不足：①层级依赖 `SetWindowPos(HWND_TOPMOST)` 反复重断言，Explorer 重启后旧任务栏 hwnd
  失效，约 0.8 s 内会被新任务栏盖住；②任务栏按钮位置靠 UIA 枚举，布局/更新变化即可能失效，空隙不稳定
  （同一次枚举 37 个元素时找不到空隙，过滤为 22 个可见按钮后空隙为 237..781）；③没有 API 契约，
  Windows 更新可能改变 Shell 层级与任务栏结构；④覆盖任务栏区域会遮挡按钮/托盘，用户拖动或改变任务栏
  宽度时可能压住系统控件。
- 决策：不把该实验模式并入产品；继续使用"独立透明置顶窗口 + 自动停靠任务栏附近 + 手动拖动"，界面与文档
  不宣称"任务栏内部显示"。若未来仍要做，必须单独立项，带开关、兼容性警告与手动回退，且不得注入 Explorer。
- 影响：`PROJECT_CONTEXT` 既有事实第 6 条得到实测支持；统计窗口、设置窗口、托盘与既有胶囊行为均不变。

## D-069 自动适配任务栏只负责尺寸，位置始终由用户决定

- 状态：已决定（阶段 4C-8）
- 背景：4C-4/4C-5 的自动适配会同时计算尺寸与停靠位置（右侧/左侧对齐、对侧角落兜底），并曾用 UIA 找空隙。
  用户明确要求本次只按显示器环境自动算尺寸，位置由用户拖到任务栏，不自动扫描按钮、不自动停靠。
- 决策：删除自动位置计算与 UIA 空隙逻辑；`SetAutoFitTaskbar(true)` 只调用 `ApplyAutoFitSize`，只改
  `Width/Height` 与 ViewModel 尺寸，`Left/Top` 保持用户当前值。`ClampCurrentPosition` 在自动模式下使用
  "显示器矩形"而不是工作区，允许用户把胶囊拖进任务栏区域。
- 理由：避免窗口自己跳动、避免与用户拖动争夺控制权；也避免依赖不稳定的任务栏按钮布局。
- 影响：设置窗口说明文案改为"位置仍由你拖动"；旧"停靠"实现保留在 `TaskbarLayout` 单元测试中，但不再被
  App 调用（未删除，避免无关重构）；`ApplyAutoLayout*` 路径改为 `ApplyAutoFit*`。

## D-070 任务栏高度优先的尺寸算法：40–104 DIP + 三档密度

- 状态：已决定（阶段 4C-8）
- 背景：普通四档最小高度 71.5 DIP，而 Windows 11 任务栏常见厚度约 48 DIP；沿用普通下限会导致胶囊塞不进
  任务栏。用户要求独立的任务栏适配算法，且不允许无限缩小字体/图标。
- 决策：`TaskbarSizingCalculator` 以"任务栏厚度 − 4 DIP 呼吸空间"为高度基准（横向任务栏取实际厚度，
  纵向取工作区高度），夹在 `MinReadableHeight=40` 与 `MaxReadableHeight=104` 之间；宽度 = 高度 × 3.0769，
  以工作区宽减 16 DIP 封顶且不低于 132 DIP。密度按厚度分档：≥88 DIP 正常、≥56 DIP 紧凑、
  <56 DIP 最简（隐藏"下载/上传"文字标签，保留 ↓/↑ 与数值）。厚度 <44 DIP 时 `FitsTaskbar=false`，
  说明文字明确写出"低于 44 DIP 可读下限"。
- 理由：高度是能否放进任务栏的硬约束；三档密度把"可读性"量化为字号/标签策略，而不是无限缩放。
- 影响：自动模式尺寸不受 `CapsuleSize` 的 220×71.5 下限限制；`CapsuleWindow` 的 `MinWidth/MinHeight`
  放宽到 132/40，普通模式仍由设置校验限制在 220–480 × 71.5–156。

## D-071 胶囊信息层级：删除 Wi-Fi 状态与说明文字

- 状态：已决定（阶段 4C-8，用户明确要求）
- 决策：胶囊内删除 Wi-Fi 图标、"已连接/未连接"文字、"已使用流量"说明；左侧只保留会话流量数字，
  标准档字号从 34 增至 46，并放进 `Viewbox(Stretch=Uniform, StretchDirection=DownOnly)`，超长文本只缩小
  不裁切；右侧速度字号从 11 增至 14（按缩放系数派生），仍为下载在上、上传在下，箭头 ↓/↑ 保留。
- 对齐：流量数字与速度块仍在同一 Grid 行且 `VerticalAlignment=Center`，4C-6 的基准不变。
- 连接状态语义：底层检测与红色/绿色背景不变，只是不再用文字/图标表达；断开时左侧显示"上次 {流量}"。
- 影响：`CapsuleViewModel` 删除 StatusText/CaptionText/图标相关属性；`UiDump.CheckSimplifiedContent`
  断言无这些元素、并记录实际字号；自检 `texts` 从 9 降为 7（数字 + 两个箭头 + 两个标签 + 两个数值）。

## D-072 最简密度隐藏速度标签，是任务栏 48 DIP 下的显式取舍

- 状态：已决定（阶段 4C-8）
- 背景：48 DIP 任务栏扣掉呼吸空间后只有 44 DIP；完整显示"下载/上传"标签需要更高行高，强行保留会让
  字号小到不可读或文字重叠。
- 决策：Minimal 密度保留 ↓/↑ 方向符、数值与单位，隐藏"下载/上传"两个文字标签；`ShowSpeedLabels`
  暴露给 ViewModel 控制可见性。Compact/Normal 保留标签。
- 验证：48 DIP 合成与真机均得到 135.4×44、Minimal；`DescribeSpeedBlock` 在标签隐藏时仍能通过 ↓/↑ 识别
  两行，`stacked=True inside=True`。
- 限制：8.5 DIP 速度字号明显小于普通档（标准 14 DIP），且这是"能放进 48 DIP 任务栏"的必要代价；
  不宣称"完整信息在任何任务栏高度都可读"。低于 44 DIP 时只保证不裁切，不保证可读。

## D-073 自动尺寸用"几何单元测试 + 合成输入自检"双重验证

- 状态：已决定（阶段 4C-8）
- 决策：纯计算放在 `HotspotMeter.Core/Layout/TaskbarSizing.cs`，由 12 个 xUnit 用例锁定（DPI、四边、
  窄屏、无任务栏矩形、比例守恒、40 DIP 下限）；App 层用 `ApplyAutoFitForTest(TaskbarLayoutInput)`
  以合成矩形驱动真实 ViewModel/窗口尺寸路径，并断言 `moved=False`（绝不改位置）与 `layout=ok`。
- 理由：真实环境只有 150% 单屏与 48 DIP 任务栏，无法覆盖 200%、二屏、纵向任务栏；把"计算"与"应用"
  分开，才能在没有对应硬件时仍验证规则。
- 限制：合成坐标不是实机多屏证据；真实自动隐藏、混合 DPI、拖入任务栏后的层级仍列为人工验收项。

## D-074 自动定位只支持底部任务栏，X=左边界+120 DIP，Y 垂直居中且不扣边距

- 状态：已决定（阶段 4C-9）
- 决策：新增 `HotspotMeter.Core/Layout/TaskbarAnchor.cs`，`TaskbarAnchorCalculator.Compute` 仅处理
  `TaskbarEdge.Bottom`；`left = monitor.Left + 120 * dpi` 并夹在 `[monitor.Left + 8dpi, monitor.Right - width - 8dpi]`；
  `top = taskbar.Top + (taskbar.Height - height)/2`，上限 `monitor.Bottom - height`，**不再扣 8 DIP**。
  顶部/左侧/右侧任务栏、缺少任务栏矩形、胶囊高于任务栏均返回 `Supported=false` 与中文原因。
- 理由：用户要求"底部偏左、位于任务栏内部、不覆盖屏幕边界"；8 DIP 的垂直边距会把 44 DIP 高的胶囊顶回任务栏上方，
  这是首轮单测失败与实机 top=1018.7 的直接原因（去掉后为 1020.7，完整落在 1528–1600 px 内）。
- 限制：仅底部任务栏做了定位；其它方向不猜测、不换边，只写日志并在设置说明中保持"当前位置"。

## D-075 自动适配开启时拖动边界用显示器物理边界，关闭恢复工作区 + 8 DIP

- 状态：已决定（阶段 4C-9）
- 决策：`CapsuleWindow.ClampCurrentPosition` 在 `_autoFit=true` 时以当前显示器矩形为界、margin=0，允许窗口进入
  任务栏矩形；普通模式仍用 `GetWorkAreaInDips` + `ScreenMargin=8`。`RestorePosition` 同样按模式选择 margin。
- 理由：工作区天然排除任务栏，只要还用它做下边界，胶囊就永远进不了任务栏；而完全去掉边界又会把窗口拖丢。
  用"显示器边界"既允许进入任务栏，又保证还能拖回来。
- 限制：任务栏不在当前显示器、或任务栏自动隐藏时，边界保护仍按显示器矩形，不做任务栏感知的额外限制。

## D-076 自动定位只在"关→开"执行；启动/拖动结束只恢复与夹取，不重新定位

- 状态：已决定（阶段 4C-9）
- 决策：`SetAutoFitTaskbar(true)` 调 `ApplyAutoFitSize(positionAtAnchor: true)`；`ApplyAutoFitSize` 默认
  `positionAtAnchor=false`，供启动恢复、`TaskbarCreated`、DPI/分辨率变化与拖动结束使用（只改尺寸、夹取、置顶、持久化）。
  固定状态跳过定位。启动 `RestorePosition` 只读取并夹取保存位置。
- 理由：用户明确"首次开启自动定位；拖动后以用户位置为准；重启恢复上次位置；再次开关可重新定位"。
  若在每次尺寸重算时都定位，用户拖到任务栏右侧后会被反复拉回左侧。
- 限制：位置"是否曾由用户拖动"没有单独标记；依赖"开关从关到开"这一事件作为重新定位的唯一触发。

## D-077 层级恢复只做事件驱动的一次性重断言，并记录 app.log

- 状态：已决定（阶段 4C-9）
- 决策：新增 `AppLog`（`%LOCALAPPDATA%\HotspotMeter\logs\app.log`，1 MB 轮转，IO 失败静默）记录自动定位几何与
  `TaskbarCreated`；`EnsureTopmost()` 统一用 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`；
  收到 `TaskbarCreated` 后重检测、按当前模式重定位/改尺寸、再置顶，不做定时轮询。
- 理由：4C-7 探针实测 Explorer 重启后旧 hwnd 失效、层级被新任务栏盖住，约 0.8 s 重断言即可恢复；高频抢 Z 序
  会抢焦点且掩盖真实问题。事件驱动 + 一次性重断言是可解释、可复现的最小方案。
- 限制：任务栏激活、全屏应用、系统更新仍可能短暂遮挡；不承诺原生任务栏组件的层级稳定性，不注册 AppBar。

## D-078 启动恢复位置前先应用自动尺寸

- 状态：已决定（阶段 4C-9 复验修正）
- 决策：`OnSourceInitialized` 在 `RestorePosition()` 之前调用 `ApplyAutoFitSizeOnly()`：只检测任务栏、
  计算 `TaskbarSizingCalculator` 结果并设置 `ViewModel` 尺寸与窗口 `Width/Height`，不夹取、不持久化。
- 理由：持久化宽度 135.4 会被 `CapsuleSize.FromPersisted` 回退为 Mini 220×71.5。若此时先恢复位置，
  夹取用的是 71.5 高度，`1066.7 - 71.5 = 995.3`，保存的 1020.7 被永久改写；之后尺寸缩到 44 也无法复原。
- 备选与否决：在 `RestorePosition` 里按最终尺寸二次夹取——会把用户位置再次移动，且无法区分旧高度来源，否决。
- 验证：UiDump `left=120 top=1020.7`、截图绿色区域 y=1531..1596（任务栏 1528..1600）。

## D-079 说明标签固定在流量数字上方，并在自动适配的紧凑密度隐藏

- 状态：已决定（阶段 4C-10）
- 决策：XAML 改为两行两列：第 0 行只放"已使用流量"标签（第 0 列，水平居中），第 1 行放流量数字
  （第 0 列，水平居中）与速度块（第 1 列，`VerticalAlignment=Center`）。标签 `TextSecondary`、字号 11×s、
  下边距 2×s。`ShowCaption` 在普通四档/自定义与自动适配 Normal 密度为 true；Compact/Minimal 为 false。
- 理由：速度块与数字同在第 1 行，天然保持 4C-6 的"速度块中心 = 数字中心"，不会因为上方多一行标签而整体下移；
  两列各自居中即可保证标签中心与数字中心重合，不需要手工 Margin。
- 备选与否决：把标签与数字放进同一个 StackPanel 再整体与速度块居中——速度块会对齐"标签+数字"整组，
  数字中心相对速度块下移，破坏 4C-6 的验收项，否决。
- 限制：44 DIP（Minimal）与 62.7 DIP（Compact）的自动适配放不下第三行文字，刻意隐藏标签；
  普通四档/自定义尺寸全部可见。标签不参与连接状态表达。

## D-080 标签与数字同容器、同字体、左对齐

- 状态：已决定（阶段 4C-11，覆盖 D-079 的对齐部分）
- 决策：左侧改为一个 `StackPanel`：先 `TrafficCaption`、后数字+速度的内层 `Grid`；标签与数字都
  `HorizontalAlignment=Left`，左边缘由 StackPanel 的同一布局基准决定。新增 `TrafficFontFamily` 资源，
  标签与数字共用；标签 `FontWeight=SemiBold` 与数字一致，仅字号 11×s 和 `TextSecondary` 颜色形成层级。
- 理由：用户要求"统一视觉元素、左边缘严格对齐、不得用固定 Margin 模拟"；StackPanel 顺序布局天然保证
  两行同左基准，字体资源消除两处硬编码字符串的漂移风险。
- 速度对齐：数字与速度块仍在内层 Grid 的同一行、速度块 `VerticalAlignment=Center`，因此 4C-6 的
  "速度块中心 = 数字中心"不受标签行影响。
- 备选与否决：把标签与数字各自 `HorizontalAlignment=Center`（4C-10 方案）——不满足左对齐要求；
  给标签加固定左边距对齐数字——用户明确禁止，且缩放后会失配。
- 限制：自动适配 Compact/Minimal 仍隐藏标签（高度不足）；标签左边缘与数字的"版面左边缘"严格相等，
  不同字号的字形左侧留白（bearing）可能有亚像素差异，不属布局错位。

## D-081 说明标签用自身 Opacity 弱化，不触碰数字与父容器

- 状态：已决定（阶段 4C-12）
- 决策：`TrafficCaption` 加 `Opacity="0.65"`，颜色仍为 `TextSecondary #A1A1A6`；`TrafficValue`、`SpeedBlock`、
  外层 `StackPanel` 的 `Opacity` 全部保持 1.0。UiDump 新增 `captionOpacity/trafficOpacity/opacityIsolated`
  三项断言，防止以后把透明度误加到父容器。
- 理由：用户要求"只弱化标签、保持灰色调、不能影响数字与其他元素"；在标签自身设 `Opacity` 是最小改动，
  且不会因继承而波及子元素（标签没有子内容）。相比改成带 alpha 的 Foreground 画刷，`Opacity` 更直观、
  可在 UiDump 里直接读到数值。
- 可读性取舍：65% 灰在绿底上的合成色为 `#739A82`，WCAG 对比度 1.34:1；实心 `#A1A1A6` 本身也只有 1.63:1。
  这是"次级说明、非正文"的刻意弱化，不宣称满足 AA。若人工验收觉得过淡，上调到 0.75–0.8；
  不允许通过调低父容器或数字透明度来"统一"，也不改字号/字重/位置。
- 备选与否决：把 `Foreground` 改为 `#A1A1A6` 加 alpha——效果等价但透明度藏在画刷里，UiDump 难以断言，
  且容易被后续样式覆盖；给标签加背景底板——改变视觉设计，且用户未要求。

## D-082 胶囊文字统一用 Microsoft YaHei UI + Bold，消除中英混排回退

- 状态：已决定（阶段 4C-13，覆盖 D-080 的字体家族与字重部分）
- 事实依据（`Fonts.SystemFontFamilies` + `FamilyTypefaces` 实测）：
  - `Microsoft YaHei UI` 仅有 Light(290)/Normal(400)/Bold(700)，**没有 SemiBold**；
  - `Segoe UI Variable Display/Text/Small` 有 Light/350/Normal/SemiBold/Bold，但**不含 CJK 字形**。
- 根因：原资源 `Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI` 先命中 Segoe，中文再逐字符回退雅黑，
  数字留在 Segoe；同时标签 SemiBold 在雅黑里被就近匹配为 Bold，数字却是真 SemiBold，故"标签更粗、两套字形"。
- 决策：`TrafficFontFamily = Microsoft YaHei UI, Microsoft YaHei`；标签与数字都显式 `FontWeight=Bold`，
  `FontStyle=Normal`。数字字号仍 46×s，视觉主体地位不变；标签仍 11×s、`Opacity=0.65`、在数字上方且左对齐。
- 理由：唯一能同时覆盖中文与拉丁、且在本机稳定存在的 UI 字体是雅黑；拉丁字形本身源自 Segoe 家族，
  统一后不会出现"中文一个字体、数字另一个字体"。显式 Bold 避免 SemiBold→Bold 的隐式映射继续误导。
- 代价：数字同字号下更宽、行高更矮（标准档 `18.26 KB` 183.3×48.7 vs 原 146.7×54），`Viewbox` 仍保证不裁切；
  若未来允许内嵌字体（如思源黑体 SemiBold），可重新评估。
- 备选与否决：只把标签改成雅黑 Bold、数字保留 Segoe——不满足"同字体家族"，且宽度差异仍在；
  用 `Typography` 或透明度掩盖——用户明确禁止掩盖；内嵌第三方字体——增加体积与授权问题，超出本次范围。

## D-083 自动适配任务栏时流量数字放大 18%，最简档收窄水平内边距

- 状态：已决定（阶段 4C-14）
- 背景：任务栏内胶囊高度只有 40–104 DIP，流量数字是唯一核心信息，用户要求自动适配模式下更醒目。
- 决策：
  1. 自动适配路径（`ApplyDensityMetrics`）的 `TrafficFontSize` 与 `TrafficLineHeight` 统一乘 `1.18`
     （`CapsuleViewModel.AutoFitTrafficFontScale`）；普通四档/自定义路径 `ApplyNormalMetrics` 保持 1.0。
  2. 最简密度（44 DIP 高，48 DIP 任务栏的默认结果）把 `CapsuleContentMargin` 的水平值 7→5、
     `SpeedBlockMargin` 左值 6→4。原因：数字放在 `Viewbox StretchDirection=DownOnly` 中，列宽不足时
     会把放大后的字号又缩回去；省下的 6 DIP 让 `renderScale` 从 0.91 回到 1.00，18% 才真正可见。
  3. 胶囊宽高、定位、置顶、拖动、速度字号与颜色均不变。
- 修复的隐性缺陷：`SetSize` 原先在 `_density == Normal && size.IsNearly(_size)` 时提前返回；
  自动适配的 Normal 密度也标记为 Normal，若关闭自动适配时尺寸同为 320×104，会保留 1.18 倍率，
  造成"关闭后数字仍偏大"。现在提前返回还需 `TrafficFontScale == 1.0`。
- 验证：`artifacts/ui-selftest34/selftest.txt` 最简档 `trafficFont=18.9 fontScale=1.18`、
  `renderScale=1.000`、`traffic rect=72×22.7`、`layout=ok`；关闭自动适配后 `trafficFont=46 fontScale=1.00`；
  四档/自定义字体仍为 31.6/40.3/46/57.5，未受影响。
- 代价与限制：紧凑/正常密度的数字列仍受胶囊宽度与速度区挤压，7–8 字符长数值的 `renderScale` 可低至
  0.56–0.83，声明字号变大但渲染尺寸受几何上限约束；要在这两档强制放大只能加宽胶囊或压缩速度区，未做。
- 备选与否决：把倍率做成设置项——需求未要求，增加配置面；直接给数字设固定大字号——脱离尺寸体系，
  小任务栏会溢出；改 `Viewbox` 为 `Both`——短数字会被拉到满宽，破坏字号一致性。
