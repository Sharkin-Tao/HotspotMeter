# HotspotMeter

Windows 手机 Wi-Fi 热点流量监控工具：桌面常驻一枚极简胶囊，实时显示**当前连接会话**的流量与上下行速度；右键或托盘打开独立的流量统计窗口，查看今日、最近 7 天与指定日期的历史。

> 当前状态：**阶段 6（发布前审查与稳定性加固）已完成，进入阶段 7 打包前的验收**。
> 构建与测试：Debug/Release 均 0 警告 0 错误，390 项自动化测试通过。
> 详细状态、已知限制与下一步见 [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) 与 [docs/08_Development_Roadmap.md](docs/08_Development_Roadmap.md)。

## 功能

- **悬浮胶囊**：无边框圆角、可拖动/固定、位置记忆、四档尺寸与自定义、自动适配任务栏高度；显示当前会话已用流量与实时 ↓/↑ 速度。
- **独立统计窗口**：今日/最近 7 天/指定日期、总/下载/上传、每日连接次数与时长、7 天堆叠柱状图、逐程序流量排行（含“未归属流量”）。
- **系统托盘**：设置、显示/隐藏胶囊、流量统计、退出；Explorer 重启后尝试重新挂载图标。
- **设置**：开机自启（HKCU Run，默认关闭）、统计窗口是否出现在任务栏（默认是）、自动适配任务栏。
- **数据**：10 秒检查点、会话结算、跨午夜按本地自然日拆分、异常退出按最后检查点收尾。

统计口径是**电脑目标 Wi-Fi 网卡的收发字节**，不等于运营商计费流量；逐程序数据来自 Windows 归属 API，缺失部分单列“未归属”，不做分摊。详见 [docs/05_Network_Monitoring.md](docs/05_Network_Monitoring.md)。

## 技术栈

| 方面 | 选择 |
| --- | --- |
| 语言 / 框架 | C# / WPF（`net10.0-windows10.0.26100.0`） |
| 系统 API | Windows WLAN API、IP Helper `GetIfTable2`、必要 Win32/WinRT |
| 存储 | SQLite + `Microsoft.Data.Sqlite`（schema v3） |
| 架构 | 分层 + MVVM（App / Core / Infrastructure / Tests） |
| 日志 | `Microsoft.Extensions.Logging` 抽象 + 本地 `app.log`/崩溃日志 |
| 测试 | xUnit |

## 项目结构

```
HotspotMeter/
├─ HotspotMeter.sln
├─ HotspotMeter.App/             # WPF 入口、胶囊/统计/设置窗口、托盘、应用服务
├─ HotspotMeter.Core/            # 领域模型、累积器、恢复判定、布局与归属名称规则
├─ HotspotMeter.Infrastructure/  # WLAN/IP Helper 互操作、监控引擎、SQLite 基础设施
├─ HotspotMeter.Tests/           # 单元与集成测试
├─ tools/                        # 开发期探针（不在解决方案内）
├─ docs/                         # 项目知识库
├─ PROJECT_CONTEXT.md            # 知识库入口
└─ AGENTS.md                     # 协作规则
```

依赖方向：`App → Infrastructure → Core`、`App → Core`；Core 不引用 WPF、Win32 或 SQLite。

## 构建、测试与运行

```powershell
dotnet build HotspotMeter.sln -c Debug
dotnet test  HotspotMeter.sln -c Debug

# 直接运行 Debug 版本
.\HotspotMeter.App\bin\Debug\net10.0-windows10.0.26100.0\HotspotMeter.exe
```

Release 验证：

```powershell
# 先退出正在运行的程序，避免 MSB3061 文件占用
dotnet clean  HotspotMeter.sln -c Release
dotnet build  HotspotMeter.sln -c Release
dotnet test   HotspotMeter.sln -c Release --no-build
```

环境：Windows 10 1809+ / Windows 11（本机验证 Windows 11 26200 x64）、.NET SDK 10.0.402；运行需要 .NET 10 Desktop Runtime。不需要管理员权限，不注入 Explorer、不修改系统文件。

## 数据与隐私

- 数据库：`%LOCALAPPDATA%\HotspotMeter\Database\hotspotmeter.db`
- 配置：`%LOCALAPPDATA%\HotspotMeter\ui-state.json`
- 日志：`%LOCALAPPDATA%\HotspotMeter\logs\`
- 所有数据只保存在本机，不上传、不联网；不读取运营商账号，不破解官方 App。

## 已知限制

- 开始菜单/搜索展开期间被 Shell 提升到更高窗口 band，胶囊可能被覆盖；关闭后自动恢复（Windows 11 公开 API 边界）。
- 采样间隔 >3 秒会被当作睡眠并丢弃该段字节；尚无睡前检查点。
- WLAN 句柄进入永久错误时不会自动重开，可能需要重启程序。
- 逐程序数据按查询日实时读取，不落库；程序图标暂未提取；周/月汇总未实现。
- 睡眠/唤醒、Explorer 重启、多显示器混合 DPI、24 小时长稳仍需人工验收。

## 文档

从 [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) 开始；架构、数据、网络、恢复、测试、路线与决策分别在 `docs/00`～`docs/10`。