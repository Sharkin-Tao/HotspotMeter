# 08 开发路线

每个阶段都必须：读知识库 → 检查代码 → 分析 → 给计划 → **等用户确认** → 小步实现 → 构建测试 → 更新知识库 → 汇报。不允许跳阶段或合并阶段。

## 阶段总览

| 阶段 | 主题 | 状态 |
| --- | --- | --- |
| 0 | 项目初始化与知识库 | **已完成（2026-11-12 用户验收通过）** |
| 1 | Windows API 与技术可行性验证 | **已完成（2026-11-12 用户验收通过）** |
| 2 | 热点识别与实时流量引擎 | **已完成（58 项测试 + 真机比对，含 F-1 收尾修正）** |
| 3 | SQLite、会话管理与异常恢复 | **已完成（schema v3、检查点、结算、启动清扫）** |
| 4 | 悬浮状态栏 | **已完成（四档/自定义、任务栏自动适配、置顶、位置记忆）** |
| 5 | 独立流量统计窗口 | **已完成（含启动管理、逐程序归属、历史持久化）** |
| 6 | 发布前审查与稳定性加固 | **已完成（2026-11-14，390 测试，已知限制见 05/07）** |
| 7 | 完整测试、优化与发布 | 未开始（待用户验收阶段 6 后进入） |

---

## 阶段 0：项目初始化与知识库（已完成）

**目标**：环境可用、骨架可编译、知识库完整、边界写清楚，不写任何业务代码。

**交付物**

- 解决方案与 4 个项目、引用关系、CPM、`global.json`、`Directory.Build.props`、`.editorconfig`、`.gitignore`、`app.manifest`（PerMonitorV2）。
- 知识库：`PROJECT_CONTEXT.md` + `docs/00`–`10`。
- `README.md`、`AGENTS.md`。
- 最小验证：`ByteSizeFormatter` + 13 个测试（含 SQLite 原生库冒烟）。

**退出标准**：`dotnet build` 0 警告 0 错误；`dotnet test` 全绿；文档覆盖全部已确认设计；用户确认。

**风险**：无（仅环境与文档）。

---

## 阶段 1：Windows API 与技术可行性验证（已完成）

**目标**：用一次性原型证明 WLAN API + IP Helper 能在本机正确识别目标热点并读到可信字节数，**不碰主解决方案**。

**实际做法**：`tools/ApiProbe/`（net10.0-windows 控制台，不加入解决方案），自写 P/Invoke 与参数解析，零第三方依赖；验证项从 8 项扩展为 11 项（新增启动即连接、退出期连续性、跨重启）。

**结果**：11 项全部通过，关键结论见 `docs/05` 第 9 节与 `docs/09` D-017～D-024。要点：

- 同一网卡在接口表中有多行同值（实测按 Type 求和为真值 6 倍）→ 只能按 GUID 取唯一行。
- 会话流量必须累加 delta；不能用网卡累计计数器（重置后差 267,124 B）。
- 睡眠时 Stopwatch 继续走，循环要按有效时长计时；接口拆除瞬间可能读到 0，恢复后需两帧确认。
- 测速只用单调时钟；墙钟 +2 h 跳变不影响速率。
- 重启可由 TickCount64 + 开机近似时刻识别，判定后拒绝恢复。

**退出标准达成情况**：11 项验证均有明确结论；GUID↔接口映射唯一可重复；下载 150 MB（原计划 100 MB，用户批准放大）偏差 1.0295 且可解释（协议开销 + 后台流量 + 读取时机）。

**证据**：`tools/ApiProbe/out/`（不入库，可按 `docs/10` 阶段 1 第 3 节复现）。

---

## 阶段 2：热点识别与实时流量引擎（已完成实现，等待用户验收）

**目标**：把阶段 1 的结论落成 Core 接口 + Infrastructure 实现，形成可测试的采样引擎（无 UI）。

**实际做法**

1. Core（纯逻辑，无 Windows API）：`HotspotTarget`/`HotspotConnection`/`InterfaceReading` 等模型，`IHotspotDetector`、`ITrafficCounter`、`IMonotonicClock`/`IWallClock`/`IUptimeProvider`、`IConnectionContinuityProvider`、`IMonitorEngine`；`TrafficAccumulator` 负责会话累计与状态机，`BaselineResumeEvaluator` 只做恢复判定与增量计算，`HotspotMatcher` 只做目标匹配。
2. Infrastructure（平台实现）：`WlanHotspotDetector`、`InterfaceTrafficCounter`、`MonitorEngine`（循环 + 事件 + 恢复幂等）、`SystemClocks`、两组 interop。
3. 测试：`HotspotMeter.Tests` 用假时钟/假检测器/假计数器覆盖采样、回退、长暂停、断开重连、BSSID 变化、墙钟跳变、恢复六种结局、恢复幂等与并发、疑似增量的告警/丢弃。
4. 控制台宿主 `tools/MonitorHost`（不入解决方案）：`--list`、`--ssid`、`--guid`、`--seconds`、`--json`，打印每秒快照与结束汇总，供人工比对。

**结果**

- `dotnet test`：**58/58 通过**（2 s，含 F-1 收尾修正新增用例）；`dotnet build HotspotMeter.sln`：0 警告 0 错误。
- 真机 45 s（其中 60 MB 限额下载）：引擎会话累计 61.6 MB / 715.06 KB，`Get-NetAdapterStatistics` 同窗口计数差 64,637,304 / 746,352 B，差异 <0.1%，来自采样边界与后台流量。
- 启动时热点已连接：第一帧 `inDelta=0`，会话从 0 开始，不把历史累计值算进会话。
- 关键语义：会话累计 = Σ 有效 delta；计数回退/长暂停/接口缺失一律重建基线并两帧确认；`ResumeAsync` 只在"同开机 + 同接口 + 计数未回退 + 仍连目标 SSID"时计算 gap，且默认证据源返回 `Unverified`。
- **F-1 收尾修正**：Unverified/Interrupted 恢复时旧会话在 `gap.FromUtc` 结束（`RecoveryUnconfirmed`）、同帧以新 SessionId 开新会话（累计 0），gap 只作独立候选；其余非 Continued 结局在最后检查点结束旧会话（`RecoveryRejected` + 具体 `ResumeOutcome`）；`_lastClosedSessionId` 水位保证同一 SessionId 只结束一次；Continuous 路径保持 `SessionResumed` 不变。见 D-035。

**退出标准达成情况**：真机累计与系统统计同量级且差异可解释；异常路径均有测试；30 分钟稳定性与内存增长留到阶段 7 的长时间运行测试（本阶段只做了分钟级真机运行），**未验证项不写成已验证**。

---

## 阶段 3：SQLite、会话管理与异常恢复

**目标**：持久化与恢复闭环，仍无 UI。

### 阶段 3A：SQLite 基础设施（已完成实现，2026-11-12）

**范围**：只建立数据库基础设施与最小元数据，不冻结业务表、不接入监控数据流。

**实际做法**

1. `DatabasePaths`：默认 `%LOCALAPPDATA%\HotspotMeter\Database`，拒绝相对路径与程序目录内的路径；目录与隔离目录统一创建。
2. `SqliteConnectionFactory`：`Pooling=false`；每连接执行 `foreign_keys=ON`、`busy_timeout=5000`、`synchronous=NORMAL`（阶段 6 已改为 `FULL`），并对 `journal_mode` 校验必须为 `wal`。
3. `SchemaMigrator`：版本唯一权威 `PRAGMA user_version`；v1 只建 `SchemaInfo(Key, Value)`；每个版本的 DDL、种子数据与版本号写入同一事务；高版本库直接抛 `DatabaseVersionTooNewException` 拒绝写入。
4. `DatabaseHealth`：`PRAGMA quick_check`；文件不存在视为"待创建"而非损坏；异常转成结构化结果，不吞错误。
5. `DatabaseQuarantine`：把 `db`、`-wal`、`-shm`、`-journal` 整体移入 `corrupt-yyyyMMdd-HHmmss-fff`；失败时尽量还原并抛异常，禁止在新位置建库覆盖证据。
6. `DatabaseBootstrapper`：健康检查 → 隔离 → 迁移 的顺序编排，结果带 `HealthBefore` 与隔离目录。
7. `SingleInstanceGuard`：`Local\HotspotMeter.SingleInstance.v1` 命名 Mutex，处理 `AbandonedMutexException`，并额外做进程内去重。
8. `SqliteWriter`：单消费者、FIFO、每项独立事务的骨架；`StopAsync` 先排空再释放连接，可重复调用。**未实现**优先级、多队列、检查点合并与业务命令。

**结果**

- 新增 43 个测试（58 → **101/101 通过**，2 s）：路径 5、连接工厂 5、迁移 6、健康 4、隔离 5、编排 4、Writer 9、单实例 5。
- `dotnet build HotspotMeter.sln`：0 警告 0 错误；`dotnet build tools/MonitorHost`：0 警告 0 错误。
- 独立提交：`阶段3A: SQLite 基础设施与迁移骨架`。
- 未做（留给 3B+）：业务表 DDL、`SessionManager`、检查点与跨午夜拆分、启动恢复与 `DailyTraffic` 重算、日志文件提供程序。

### 阶段 3B（拆成 8 个子步骤，按顺序审批）

1. **3B-1 Schema v2 迁移与约束验证** — 已完成（`d08e036`）：9 张表、复合外键、部分唯一索引、三态与日期 CHECK、基线六字段整体约束。
2. **3B-2 基础仓储** — 已完成（`3b73089`）：`ProfileResolver`、`SessionRepository`（会话+映射同事务、关闭、清理陈旧映射）、`BssidRepository`（观察/确认分离）。
3. **3B-3 操作键与类型转换** — 已完成（`5c40984`）：`OperationIdFactory`（`op:{dbSessionId}:{kind}:{fromTicks}:{toTicks}`）、`SqliteTypeConversion`（ulong 溢出显式拒绝，不写 REAL）。
4. **3B-4 时间切分与日汇总** — 已完成（本阶段）：`LocalDayResolver`（UTC 分段 + tick 二分）、`NightlySplitter`（BigInteger 累计分配，严格守恒）、`EffectiveTimePolicy`（事件级墙钟调整计数）、`DailyTrafficDelta`、`DailyTrafficRepository`（非幂等 UPSERT，MIN/MAX 端点）。
5. **3B-5 StepBuffer / StepPump / Journal** — 部分完成：3B-5a（Schema v3、`UtcTimestamp`、摘要编码、91 例测试）已实现待验收；3B-5b 起（Settlement 入口、Writer 状态机、午夜调度、StepBuffer/Pump/Journal）待批准。
6. **3B-6 Core 扩展** — 待批准：`MonitorStep.Baseline`、`TargetProfileId`、`StepProduced`、BSSID 未确认暂停、采样背压。
7. **3B-7 SessionPersistenceService** — 待批准：把 step 落成会话/快照/日汇总/恢复候选，单事务与提交不确定重读。
8. **3B-8 集成测试、文档与回归** — 待批准。

后续阶段：

1. 3C：`SessionManager` / `SessionPersistenceService` 接入引擎：建会话、断开结束、10 秒检查点、跨午夜拆分、恢复事件落库。
2. 3D：启动恢复：未结束会话收尾、连续性候选处理、一致性校验、`DailyTraffic` 重算。
3. 3E：崩溃/断电/唯一约束/重复结束的集成测试与真机演练。
4. 3F：日志接入 `Microsoft.Extensions.Logging` 文件提供程序，完成阶段 3 验收。

**退出标准**：06 文档 R-01～R-08 中可在无 UI 环境验证的场景全部通过；数据库可被外部工具打开检查。

---

## 阶段 4：悬浮状态栏

**目标**：可常驻、可拖动、可固定、状态正确的胶囊窗口。

**计划**

1. 先做技术验证窗口：`AllowsTransparency` vs `WindowChrome` 对比 CPU/拖动/命中测试；PerMonitorV2 下的清晰度。
2. 布局与状态绑定（绿色/红色、大数字、速度、上次会话标注）。
3. 拖动/固定/位置持久化；右键菜单四项；打开统计窗口的入口（先占位）。
4. `WindowPositioner`：任务栏矩形、开始按钮定位尝试、失败回退、多屏/DPI 变化重算。
5. `ShutdownMode=OnExplicitShutdown`；关闭统计窗口不影响进程。
6. 性能测量：空闲 CPU、内存、GDI 句柄。

**退出标准**：02 文档 M-10～M-14 通过；150% DPI 与双屏可用；定位失败时手动拖动可靠。

---

## 阶段 5：独立流量统计窗口

**目标**：完整统计视图，与悬浮窗共享服务与数据库。

**计划**

1. 窗口骨架与 MVVM（今日摘要、每日表格、周/月聚合、历史查询）。
2. 查询服务与分页/排序；空状态、错误状态。
3. 与后台事件联动刷新（低频）；重复打开激活已有窗口。
4. 单位/时长格式统一（复用 Core 格式化）。
5. UI 测试（手工矩阵）+ 截图存档。

**退出标准**：今日数字与悬浮窗一致；关闭窗口后监控继续；历史查询边界正确。

**2026-11-14 实际交付（范围按用户阶段 5 指令调整）**：任务栏层级事件恢复、HKCU 开机自启、任务栏显示默认开启、
历史持久化接入、WinRT 逐程序归属、统计窗口（今日/最近 7 天/单日 + 程序排行）。周/月聚合、程序图标、
逐应用入库未做，留待后续；因此阶段 6 中的“开机启动”已提前完成，后续只剩系统集成与发布项。

---

## 阶段 6：发布前全面审查、质量优化与稳定性加固（已完成）

> 2026-11-14 用户重新定义：阶段 6 不再新增“设置/系统集成”功能（这些已在阶段 5 交付），
> 而是对现有实现做发布前审查与加固；阶段 7 负责打包与发布。

**目标**：在完整保留现有功能的前提下，修复数据安全、生命周期、并发、容错、UI 细节问题，使项目达到可打包的质量基线。

**实际执行**
1. 建立现有功能清单与 Git 基线（`2498c4a`），4 个只读子代理分模块审查。
2. 修复 P0：退出时 UI 线程同步等待导致 3 秒冻结、历史写入器可能未关闭。
3. 修复数据正确性：统计按目标热点 `ProfileKey` 过滤、逐程序 profile 精确匹配、遗留会话映射标 Abandoned、睡眠不计连接时长。
4. 修复数据库健康分类与 `synchronous=FULL`，避免安全软件短暂占用导致历史被隔离。
5. 修复统计窗口并发/取消、右键菜单统计入口、设置窗低分辨率出屏、配置原子写、托盘 GDI 泄漏与 Explorer 重启恢复。
6. 修复目标热点晚于程序启动时永久 0 字节的问题（5 秒重试）。
7. Debug/Release 构建与 390 项测试全部通过；运行时验证检查点、结算、统计布局与退出。

**退出标准完成情况**：功能清单、审查、低风险修复、Release 构建、测试、文档更新均完成；
仍有已记录的技术限制（WLAN 句柄永久错误、>3 s 间隔丢字节、睡眠编排、Alt+F4 胶囊重开、同用户双实例清扫），
不阻塞阶段 7 打包，但必须在发布说明与阶段 7 验收中保留。

---

## 阶段 7：完整测试、优化与发布

**已具备**
- Debug/Release 构建 0 警告 0 错误，390 项自动化测试通过。
- 数据库 schema v3、旧配置兼容、统计查询按目标热点过滤。
- 深色统计窗口（含标题栏）、胶囊/托盘/设置/开机自启/任务栏显示。

**计划**
1. 跑完 07 的全部手工矩阵与 06 的恢复矩阵；至少 24 小时长稳（当前只有分钟级）。
2. 发布：`dotnet publish -c Release -r win-x64`，评估 self-contained vs framework-dependent；单文件与 SQLite 原生库兼容性。
3. 安装/卸载说明、数据目录说明、版本号与变更日志；确认升级保留数据库与 `ui-state.json`。
4. 清理开发期探针工具与 `artifacts/`，确认 `.gitignore` 不提交数据库/日志/构建产物。
5. 知识库定稿：把“未验证假设”逐条改为结论；补发布检查清单。

**退出标准**：00 文档第 8 节六条成功标准全部满足；发布包在干净机器上可运行；用户完成人工验收。

### 阶段 7 交接清单

- **Release 配置**：`net10.0-windows10.0.26100.0`，x64；需决定 self-contained 还是 framework-dependent（目标机是否预装 .NET 10 Desktop Runtime）。
- **图标/版本**：`HotspotMeter.App/Assets/HotspotMeter.ico` 已生成；Assembly/File 版本号仍待统一。
- **依赖**：`Microsoft.Data.Sqlite`（含 `e_sqlite3.dll` 原生库）、WinForms `NotifyIcon`、WinRT 网络归属 API（要求 Windows 10 1809+）。
- **数据位置**：`%LOCALAPPDATA%\HotspotMeter\Database\hotspotmeter.db`；日志 `%LOCALAPPDATA%\HotspotMeter\logs\`；配置 `%LOCALAPPDATA%\HotspotMeter\ui-state.json`。
- **数据保留**：升级不得删除/重建数据库；schema 变更必须迁移且可回滚；不自动清库。
- **首次启动**：默认显示胶囊，开机自启默认关闭，统计窗口“出现在任务栏”默认开启。
- **安装/卸载**：需决定安装包（MSIX/Inno/便携 zip）；卸载是否保留用户数据需用户确认。
- **便携版**：SQLite 与配置均写 `%LOCALAPPDATA%`，便携版需明确数据是否随目录走。
- **发布前安全检查**：不注入 Explorer、不修改系统文件、不请求管理员权限、不上传数据、不含密钥。
- **最终实机验收**：见 07“仍需人工验收”7 项。

---

## 依赖与顺序说明

- 阶段 1 的结论会反过来修正 05/03 的设计，因此不能跳过。
- 阶段 4 依赖阶段 2/3 提供的可观察状态，否则只能写假数据 UI，返工风险高。
- 阶段 5 依赖阶段 3 的查询能力。
- 阶段 6 的单实例与电源事件其实在阶段 3/4 就会部分实现，阶段 6 只做审查与加固；电源事件仍未完整接入，见 05 §15。

## 当前阻塞/待确认

1. 阶段 6 已完成代码与文档，等待用户验收 Debug 目录中的实际效果；未确认前不开始阶段 7 打包。
2. 阶段 7 前必须确认：安装包形式（MSIX/Inno/便携 zip）、self-contained 还是 framework-dependent、卸载是否保留 `%LOCALAPPDATA%` 数据。
3. 04 文档第 9 节的保留期与导出问题仍未定。
4. 07 文档列出的 7 项人工验收（开始菜单/任务栏、Explorer 重启、睡眠、多 DPI、Alt+F4、长稳）需在阶段 7 完成。
5. `E:\hotspotmeter\HotspotMeter` 旧位置残留目录与 `E:\hotspotmeter-clone-git-backup` 的最终处置（见 `docs/09` D-025）。