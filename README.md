# HotspotMeter

Windows 手机 Wi-Fi 热点流量监控工具：常驻桌面的一枚极简胶囊状态栏，实时显示当前热点连接会话的流量与速度；右键可打开独立的流量统计窗口。

> 当前状态：**阶段 0（项目初始化与知识库建立）已完成**。尚未实现流量采集、网络 API 或正式 UI。
> 开发路线与阶段门禁见 [docs/08_Development_Roadmap.md](docs/08_Development_Roadmap.md)。

## 它解决什么问题

用电脑连手机热点时，Windows 本身只显示"已连接"，不告诉你这个热点用掉了多少流量。HotspotMeter 只统计**电脑这块目标 Wi-Fi 网卡**的收发字节，按会话累计并落到 SQLite，供你查看当天/每周/每月用量，避免超额。

统计口径是**网卡字节数**，不等于运营商计费流量（协议开销、手机自身流量、其他设备、运营商计费规则都会造成差异），界面与文档中不得宣称两者一致。详见 [docs/05_Network_Monitoring.md](docs/05_Network_Monitoring.md)。

## 技术栈

| 方面 | 选择 |
| --- | --- |
| 语言 / 框架 | C# / WPF |
| 目标框架 | .NET 10（`net10.0-windows`） |
| 系统 API | Windows WLAN API、IP Helper API（`GetIfTable2`）、必要 Win32 |
| 存储 | SQLite + `Microsoft.Data.Sqlite` |
| 架构 | 分层 + MVVM |
| 日志 | `Microsoft.Extensions.Logging`（抽象已引入，具体提供程序在阶段 3 接入） |
| 测试 | xUnit |

## 项目结构

```
HotspotMeter/
├─ HotspotMeter.sln
├─ HotspotMeter.App/             # WPF 入口、视图、视图模型（UI 层）
├─ HotspotMeter.Core/            # 领域模型、业务规则、接口（不依赖 WPF / Win32）
├─ HotspotMeter.Infrastructure/  # Win32/WLAN/IP Helper 互操作、SQLite 仓储、系统集成
├─ HotspotMeter.Tests/           # 单元测试与集成测试
├─ docs/                         # 项目知识库（先读这里）
├─ PROJECT_CONTEXT.md            # 知识库入口
└─ AGENTS.md                     # 对 AI 协作与开发者的项目规则
```

依赖方向：`App → Infrastructure → Core`，`App → Core`。Core 不反向依赖任何项目，也不引用 WPF / Win32，保证业务规则可以脱离界面测试。

## 环境要求

- Windows 10 / Windows 11（本机验证：Windows 11 专业版 10.0.26200，x64）
- .NET SDK 10.0.402 或同一 feature band 的更高版本（见 `global.json`）
- 开发/运行都不需要管理员权限；不注入 Explorer、不修改系统文件

## 构建与测试

```powershell
dotnet build HotspotMeter.sln -c Debug
dotnet test  HotspotMeter.sln -c Debug
```

阶段 0 验证结果：构建 0 警告 0 错误；13 个测试全部通过（含一次 SQLite 原生库冒烟测试）。运行 `HotspotMeter.App` 时因为还没有窗口，会立即正常退出（退出码 0），这是阶段 0 的预期行为。

## 数据与隐私

所有数据只保存在本机 SQLite 文件中，不上传、不联网。程序不读取运营商账号，不破解任何官方 App。手机套餐余量同步属于后续版本（第一版不实现）。

## 文档

从 [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) 开始，它列出全部知识库文件、阅读顺序和当前阶段的"已确认事实 / 未验证假设"。