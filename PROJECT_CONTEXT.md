# PROJECT_CONTEXT.md — HotspotMeter 知识库入口

> 每次开始任务先读本文件，再按任务读取对应文档。
> 本文件只做导航、状态与"事实/假设"清单，详细内容在 `docs/` 下。

## 1. 项目一句话

Windows 原生 WPF 工具，监控电脑连接手机 Wi-Fi 热点时的网卡流量：一个极简悬浮胶囊显示**当前会话**流量与实时速度，右键打开**独立统计窗口**查看日/周/月/历史数据。

## 2. 当前状态

| 项目 | 状态 |
| --- | --- |
| 当前阶段 | **阶段 2 已完成实现（58 项测试通过 + 真机比对，含 F-1 收尾修正），等待用户验收确认** |
| 代码状态 | Core：模型/接口/累积器/恢复判定；Infrastructure：WLAN 检测、计数器、`MonitorEngine`、系统时钟；`tools/ApiProbe`（阶段 1 原型）与 `tools/MonitorHost`（阶段 2 宿主），均不入解决方案 |
| 未实现 | SQLite 表结构与持久化、会话收尾与跨日拆分、两个窗口、设置页、开机启动 |
| 下一次要做的事 | 用户确认阶段 2 后，按 `docs/08` 阶段 3 先给计划（表结构 + 检查点 + 恢复持久化），再动手 |

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

## 4. 已确认的事实（可依赖）

1. 产品形态、两个窗口的职责边界、右键菜单项、"悬浮窗只显示会话流量"等来自用户总指令，**属于已确认设计，不得擅自变更**。
2. 统计口径是"电脑目标 Wi-Fi 网卡的收发字节"，不等于运营商计费；界面与文案必须如实说明差异。
3. 一天内多次连接/断开，全部会话流量按本地自然日汇总；跨午夜会话按采样时间拆到两天。
4. 程序启动时若存在未完成会话，用最后一次有效检查点恢复；网卡/电脑重启后不得重复累计历史字节。
5. 突然断电可能丢失最后一次检查点之后的数据，不承诺绝对零丢失；`DailyTraffic` 必须能由基础记录重算。
6. 普通 WPF 应用无法通过公开 API 融入 Windows 11 原生任务栏，因此是"独立透明窗口 + 自动定位 + 手动拖动兜底"。
7. 环境事实：`.NET 10.0.402` SDK、WindowsDesktop 10.0.13 运行时、x64、Windows 11 26200 均已在本机确认。
8. 本机 `Microsoft.Data.Sqlite 10.0.15` 能加载原生 SQLite（已由测试验证），`Microsoft.Extensions.Logging.Abstractions 10.0.15` 可还原。
9. WLAN API 可稳定取得 SSID/BSSID/GUID；`MIB_IF_ROW2.InterfaceGuid` 与之一一对应。`InOctets/OutOctets` 反映真实收发字节（150 MB 下载实测比值 1.0295）。
10. 结构体布局（x64）：`MIB_IF_ROW2`=1352、`WLAN_CONNECTION_ATTRIBUTES`=604、`WLAN_INTERFACE_INFO`=532。
11. 测速用 `Stopwatch` 不受墙钟跳变影响；重启可由 `TickCount64` + 开机近似时刻识别。
12. 引擎行为已被 58 项自动化测试锁定：会话累计=Σ delta、两帧确认、六状态、恢复六种结局、恢复幂等（含并发）、墙钟跳变不影响速度、F-1 恢复会话切分与单次结束。
13. 恢复 gap 的连续性默认 `Unverified`：`ResumeAsync` 算出候选但**不**并入原会话，必须由证据源给出 `Continuous` 才合并。
14. `MonitorEngine` 位于 Infrastructure；Core 不引用 Windows API、WPF、SQLite（靠项目引用方向保证）。
15. 采样间隔默认 1000 ms、最小 500 ms，`MonitorOptions.Validate()` 强制。

## 5. 未验证的假设（禁止当结论使用）

| 假设 | 计划验证阶段 |
| --- | --- |
| 1000ms 采样间隔下速度数字可读；但 UI 上的平滑度与动画效果 | 阶段 2 已验证数字可读，平滑度阶段 4 定 |
| 引擎连续运行 30 分钟无内存增长/句柄泄漏 | 阶段 7（阶段 2 只做了分钟级真机运行） |
| `AllowsTransparency=True` + 圆角 Border 在目标机器上性能/命中测试可接受 | 阶段 4 |
| 能通过任务栏窗口 + UI Automation 稳定定位"开始按钮"附近空位 | 阶段 4 |
| 通过 `PowerModeChanged`/`SystemEvents` 可靠捕获睡眠唤醒 | 阶段 3/4 |
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
dotnet run --project tools/MonitorHost -- --ssid "OPPO Find X9" --seconds 60
dotnet run --project tools/MonitorHost -- --ssid "OPPO Find X9" --seconds 20 --json
```

## 8. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-12 | 建立知识库；完成阶段 0（骨架、构建、测试、文档） |
| 2026-11-12 | 完成阶段 1（11 项 API 验证）；项目迁移到 `E:\hotspotmeter\HotspotMeter`；新增 D-017～D-025 |
| 2026-11-12 | 完成阶段 2（Core 模型/累积器/恢复判定、Infrastructure 检测器/计数器/引擎、54 项测试、MonitorHost 真机比对）；新增 D-026～D-034 |
| 2026-11-12 | F-1 收尾修正：恢复时切分会话、非 Continued 不再静默丢会话、单次结束水位；58 项测试通过；新增 D-035 |