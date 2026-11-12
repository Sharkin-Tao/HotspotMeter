# PROJECT_CONTEXT.md — HotspotMeter 知识库入口

> 每次开始任务先读本文件，再按任务读取对应文档。
> 本文件只做导航、状态与"事实/假设"清单，详细内容在 `docs/` 下。

## 1. 项目一句话

Windows 原生 WPF 工具，监控电脑连接手机 Wi-Fi 热点时的网卡流量：一个极简悬浮胶囊显示**当前会话**流量与实时速度，右键打开**独立统计窗口**查看日/周/月/历史数据。

## 2. 当前状态

| 项目 | 状态 |
| --- | --- |
| 当前阶段 | **阶段 1 已完成（11 项验证全部通过），等待用户验收确认** |
| 代码状态 | 解决方案 + 4 个项目骨架；`ByteSizeFormatter` + 13 个测试；验证原型 `tools/ApiProbe/`（不入解决方案） |
| 未实现 | 正式网络采集服务、SQLite 表结构、会话管理、两个窗口、设置页、开机启动 |
| 下一次要做的事 | 用户确认阶段 1 后，按 `docs/08` 阶段 2 先给计划（Core 接口 + 采样引擎），再动手 |

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

## 5. 未验证的假设（禁止当结论使用）

| 假设 | 计划验证阶段 |
| --- | --- |
| 1000ms 采样间隔下速度显示足够平滑、CPU 占用可接受 | 阶段 2 |
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
```

## 8. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-12 | 建立知识库；完成阶段 0（骨架、构建、测试、文档） |
| 2026-11-12 | 完成阶段 1（11 项 API 验证）；项目迁移到 `E:\hotspotmeter\HotspotMeter`；新增 D-017～D-025 |