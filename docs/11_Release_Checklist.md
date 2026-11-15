# 11 · 发布检查清单（Release Checklist）

> 本清单对应版本 **0.1.0**，检查日期 2026-11-15。  
> 原则：只记录实际执行过的检查；“代码/自动化通过”不等于“实机通过”，两者分列。  
> 相关文档：`13_Localization.md`、`12_Release_Notes.md`、`07_Test_Plan.md`、`09_Decisions.md`。

## 1. 项目冻结基线

| 项目 | 值 |
|---|---|
| 分支 | `main` |
| 检查开始时 HEAD | `6ce4f73`（之后有合并提交 `2b0ceae`，工作区干净） |
| 解决方案 | `HotspotMeter.sln`（App / Core / Infrastructure / Tests） |
| 目标框架 | App `net10.0-windows10.0.26100.0`；类库 `net10.0` |
| 最低系统 | Windows 10 1809（10.0.17763），x64 |
| SDK / 运行时 | .NET SDK 10.0.402；Windows Desktop Runtime 10.0.13 |
| 发布版本 | 0.1.0（`Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion`） |
| 发布模式 | self-contained，win-x64，文件夹（非单文件） |
| 数据库 Schema | v3，本阶段未修改 |

## 2. 功能完整性清单

“自动化”列指 xUnit；没有 xUnit 覆盖的 UI 功能使用应用内自检/实机检查，并明确标注。

| 功能 | 当前实现 | 对应测试/验证 | 发布前状态 |
|---|---|---|---|
| 桌面悬浮胶囊（连接态/断开态、会话流量、速度） | `HotspotMeter.App/Views/CapsuleWindow.xaml(.cs)`、`ViewModels/CapsuleViewModel.cs` | `HOTSPOTMETER_UI_TEST` 自检：四档+自定义+自动适配；`layout=ok`、`caption*` 断言 | 通过（实机自检） |
| 实时流量监控（WLAN + GetIfTable2） | `HotspotMeter.Infrastructure/Monitoring/MonitorEngine.cs`、`HotspotMeter.Core/Monitoring/TrafficAccumulator.cs` | `MonitorEngineTests`、`TrafficAccumulatorTests`、`BaselineResumeEvaluatorTests` | 通过（自动化 + 实机采样） |
| 当前会话流量 / 上下行速度 | `CapsuleViewModel`、`MonitorSessionService` | UI 自检 + 真机运行字节对比（历史阶段 2） | 通过（实机） |
| 多档尺寸与自定义尺寸 | `Core/Layout/CapsuleSize.cs`、`SettingsViewModel` | UI 自检覆盖 Mini/Small/Default/Large/自定义 | 通过（实机自检） |
| 位置固定 / 解除固定 | `CapsuleWindow.xaml.cs`（`IsPinned`、拖动、菜单） | 阶段 5 自检 + UI dump；真实鼠标拖拽未自动化 | 部分验证（拖拽人工） |
| 任务栏适配（尺寸/停靠/锚点） | `Core/Layout/TaskbarSizing.cs`、`TaskbarAnchor.cs`、`App/Services/TaskbarDetector.cs` | `TaskbarSizingCalculatorTests`、`TaskbarAnchorCalculatorTests`、`TaskbarLayoutCalculatorTests` | 通过（自动化 + 实机） |
| 任务栏交互层级恢复 | `App/Services/ShellForegroundWatcher.cs` | `tools/ShellZOrderProbe` 实机 Z 序/像素验证；Stage5 自检 | 通过（实机，含 band 限制） |
| 系统托盘 | `App/Services/TrayIconService.cs` | Stage5 自检（菜单存在/重建逻辑）；未自动展开菜单 | 部分验证（菜单观感人工） |
| 设置窗口 | `App/Views/SettingsWindow.xaml(.cs)`、`SettingsViewModel` | `HOTSPOTMETER_UI_SETTINGS` 布局/本地化 dump；语言测试 | 通过（实机自检） |
| 开机自启动 | `App/Services/StartupRegistrationService.cs` | Stage5 自检真实读写 HKCU Run；状态恢复 | 通过（实机） |
| 统计窗口任务栏显示开关 | `WindowStateStore`、`ISettingsHost`、`SettingsWindow` | Stage5 自检 `taskbar.value` 三态 | 通过（实机） |
| 今日流量 / 最近 7 天 / 指定日期 | `App/Services/TrafficStatsService.cs`、`StatsViewModel` | `HOTSPOTMETER_UI_STATS` 实机 dump；日期轴/空值逻辑 | 通过（实机自检） |
| 上下行流量 | 同上（`DailyTraffic` 字段） | UI dump + SQLite 实测 | 通过（实机） |
| 每日连接次数 / 连接时长 | `TrafficHistoryStore.BeginSessionAsync`、`record` 结算 | 持久化集成测试 + SQLite 实测 | 通过（自动化 + 实机） |
| 历史连接会话记录 | `ConnectionSessions`、`SessionRunMappings` | `SessionRepositoryTests`、`SchemaV2Tests`、`SchemaV3Tests` | 通过（自动化） |
| 逐程序流量归属 | `App/Services/AttributedUsageReader.cs` | `AttributionDisplayNameTests`（名称解析）；真实 API 探针；统计窗实机 | 通过（实机；数据受 Windows 归属限制） |
| 未归属流量单独展示 | `AttributionDisplayName`、`StatsViewModel` | 统计窗实机截图/状态文案 | 通过（实机） |
| SQLite 持久化与恢复 | `Infrastructure/Persistence/*`、`TrafficHistoryStore` | 持久化单元/集成测试；异常退出/清理逻辑 | 通过（自动化 + 实机检查点） |
| 后台持续监控（统计窗关闭后） | `App.xaml.cs`、`MonitorSessionService` | Stage5 自检 `monitor.afterClose` 增长 | 通过（实机） |
| 统计窗口单实例 / 关闭不退出 | `App.xaml.cs` `OpenStats()` | Stage5 自检 `stats.singleInstance=True` | 通过（实机） |
| 中英双语（首次选择/即时切换） | `Core/Localization`、`App/Localization`、`LanguageWindow` | `LanguageCatalogTests` 18 项；UIAutomation 实机切换 | 通过（自动化 + 实机） |
| 重复启动保护 | `Infrastructure/Persistence/SingleInstanceGuard.cs` | `SingleInstanceGuardTests`；Stage5 自检 | 通过（自动化） |
| 用户配置兼容（旧 `ui-state.json`） | `WindowStateStore` 可空字段 | 旧文件缺失语言/任务栏字段的实机启动 | 通过（实机） |
| 数据不写入安装目录 | `DatabasePaths`、`WindowStateStore` | 发布目录检查 + `%LOCALAPPDATA%` 实测 | 通过（实机） |
| 安装 / 升级 / 卸载 | 便携 ZIP + 手动删除 | 无安装程序；ZIP 解压运行已验证，安装/卸载程序未验证 | 未验证（工具缺失） |

## 3. 构建与测试

| 命令 | 结果 |
|---|---|
| `dotnet clean HotspotMeter.sln -c Release` | 通过（需先退出运行中的程序，否则 DLL 被占用） |
| `dotnet restore HotspotMeter.sln` | 通过 |
| `dotnet build HotspotMeter.sln -c Release` | **0 警告 / 0 错误** |
| `dotnet test HotspotMeter.sln -c Release --no-build` | **408 通过 / 0 失败 / 0 跳过** |
| `dotnet publish ... -r win-x64 --self-contained true` | 通过 |

未执行：安装包构建（Inno Setup/NSIS 不可用）、代码签名、干净虚拟机/新 Windows 用户环境测试、真实用户鼠标拖拽与托盘菜单人工目检。

## 4. 发布产物

| 产物 | 路径 | 说明 |
|---|---|---|
| 自包含文件夹 | `artifacts/release/HotspotMeter-win-x64/` | `HotspotMeter.exe` + 运行时/原生库；无 `.pdb`、无数据库/日志 |
| 便携 ZIP | `artifacts/release/HotspotMeter-win-x64.zip` | 解压后运行 |
| 安装包 | 未生成 | 缺少可靠打包工具；不以空壳替代 |
| 校验文件 | `artifacts/release/SHA256SUMS.txt` | ZIP 与目录内主程序 |

大小与 SHA-256：ZIP 81,890,833 B（78.10 MiB），`2753fb8a…98708f01`；`HotspotMeter.exe` 544,768 B，`0c766d93…cb42931`。完整值见 `12_Release_Notes.md` 与 `artifacts/release/SHA256SUMS.txt`。

## 5. 发布包独立运行检查

在 `artifacts/release/HotspotMeter-win-x64/` 直接运行：

| 检查 | 结果 |
|---|---|
| 启动（无开发机 PATH/文件依赖） | 通过 |
| 胶囊显示（位置/尺寸/DPI/文本） | 通过：`left=120 top=1020.7 actual=135.3x44 dpi=1.5` |
| 统计窗口 | 通过：`980×700`，真实数据与热点名正常 |
| SQLite 创建/写入 | 通过：读取 `%LOCALAPPDATA%` 既有库，检查点增长 |
| 退出（无残留进程） | 通过：退出码 0 |
| 权限 | 普通用户（未提升）运行 |
| 多显示器/混合 DPI | 未验证 |
| 网络断开/重连 | 未在本轮重复验证（阶段 5 已有自检） |
| 重复启动 | 自动化 + Stage5 自检通过 |

## 6. 敏感信息审查

| 检查项 | 方法 | 结果 |
|---|---|---|
| 工作区凭证 | `git grep`（key/token/secret/password/ghp_/sk-/AKIA/PRIVATE KEY） | 无命中（仅 `.gitignore` 注释含 “passwords”） |
| 个人路径 | 搜索 `C:\Users\`、开发盘路径 | 无命中 |
| 外联/遥测 | 搜索 `HttpClient/WebClient/Socket/Telemetry` | 产品代码无外联（仅 XAML 命名空间 URL） |
| 依赖漏洞 | `dotnet list package --vulnerable --include-transitive` | 四个项目均无已知漏洞 |
| 第三方来源 | 仅 NuGet 官方源（Microsoft.Data.Sqlite、Logging.Abstractions、xUnit 等） | 无来源不明二进制 |
| 数据库/日志入库 | `git ls-files` + `.gitignore` | 无 `.db/.log/ui-state.json` 被跟踪 |
| 截图 | Windows.Media.Ocr 全仓扫描 | `Traffic Statistics.png` 含真实 SSID；已生成脱敏副本并替换 README 引用；原图保留在本地但已从版本库移除并 `.gitignore`，历史提交仍可恢复 |
| 代码/文档 SSID/BSSID/GUID | 全局替换为 `TestHotspot` / `02:00:00:00:00:01` / `11111111-...` | 当前工作区已清理 |
| Git 历史 | `git log -p --all` 凭证模式扫描 | 无凭证；但历史含真实 SSID/BSSID/GUID 与含 SSID 的截图（`d4234e5` 起） |

分类结论：**当前工作区与发布包为“通过”；Git 历史存在“高”级隐私残留（真实热点名/设备标识）**。未重写历史，需在公开推送前由仓库所有者决定是否清理（见 `12_Release_Notes.md` 与最终报告）。

## 7. 发布阻断条件对照

| 阻断项 | 状态 |
|---|---|
| 核心功能丢失 | 无 |
| 原有测试失败 | 无（408/408） |
| Release 构建失败 | 无 |
| 发布包无法启动 | 无（文件夹发布实测） |
| 数据库被覆盖/历史丢失风险 | 未发现；Schema 未改，数据仍写 `%LOCALAPPDATA%` |
| 未处理真实密钥 | 无 |
| 未处理个人隐私（当前工作区） | 已处理 |
| Git 历史严重隐私残留 | **存在，未处理**（不擅自重写历史） |
| 安装/卸载明显破坏性 | 安装程序未制作，不适用；便携版手动卸载 |

## 8. 最终验收表

| 验收项目 | 结果 | 说明 |
|---|---|---|
| 项目功能完整性 | 通过 | 功能逐项映射，未删功能/改默认行为 |
| Release 构建 | 通过 | 0 警告 0 错误 |
| 自动化测试 | 通过 | 408/408 |
| WPF 发布运行 | 通过 | 自包含目录实测 |
| SQLite 数据持久化 | 通过 | 检查点/退出结算/历史保留 |
| 用户配置兼容性 | 通过 | 旧 `ui-state.json` 读取 |
| 安装验证 | 未验证 | 无安装程序 |
| 升级验证 | 部分验证 | 覆盖程序目录不丢 `%LOCALAPPDATA%` 数据；未做安装器升级 |
| 卸载验证 | 部分验证 | 便携版手动卸载路径已定义；未做安装器卸载 |
| 敏感信息扫描 | 通过（当前工作区） | Git 历史有隐私残留，单列 |
| Git 历史审查 | 存在风险 | 无凭证；有 SSID/BSSID/GUID/截图 |
| 截图隐私审查 | 通过（当前引用） | 已脱敏并更新 README |
| 发布文件校验 | 通过 | SHA-256 已生成 |
| 中英文 README | 通过 | 两份均更新安装/数据/卸载/限制 |

## 9. 结论

**有条件进入发布**：便携 ZIP 可交付并已实测；安装包未制作、安装/卸载程序未验证；Git 历史隐私残留需在公开推送前处理。详见 `12_Release_Notes.md` 与最终报告。