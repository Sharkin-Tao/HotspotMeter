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