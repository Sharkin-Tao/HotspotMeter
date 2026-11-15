# HotspotMeter

### Windows Hotspot Traffic Monitor

**Know your hotspot traffic at a glance.**

![Windows](https://img.shields.io/badge/Platform-Windows-0078D4?style=flat-square&logo=windows&logoColor=white)
![CSharp](https://img.shields.io/badge/Language-C%23-239120?style=flat-square&logo=csharp&logoColor=white)
![WPF](https://img.shields.io/badge/Framework-.NET%20%2F%20WPF-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![SQLite](https://img.shields.io/badge/Database-SQLite-003B57?style=flat-square&logo=sqlite&logoColor=white)

**简体中文** | [English](README_EN.md)

---



HotspotMeter 是一款面向 Windows 用户的桌面流量监控工具，帮助你直观了解手机热点连接状态、实时网络流量以及各应用程序的流量消耗。

无需频繁打开系统设置，通过桌面悬浮窗口即可掌握热点流量使用情况。

## ✨ 功能特性

- **实时流量监控**：显示当前热点连接状态、会话流量和实时上传/下载速度。
- **桌面悬浮胶囊**：轻量、简洁的悬浮窗口，方便随时查看流量。
- **灵活调整尺寸**：提供多种预设尺寸，支持自定义窗口大小。
- **位置固定**：支持固定悬浮窗口位置，避免误操作。
- **任务栏适配**：提供任务栏显示模式，优化桌面使用体验。
- **历史流量统计**：支持今日、最近 7 天和指定日期的流量查询。
- **应用程序流量统计**：展示 Windows 网络 API 提供的逐应用程序流量归属数据。
- **系统托盘管理**：通过托盘菜单访问统计窗口和管理程序。
- **开机自启动**：支持设置程序随 Windows 启动。
- **后台持续监控**：关闭统计窗口后，后台流量监控仍会继续运行。

> 注意：逐应用程序流量统计依赖 Windows 提供的网络归属数据，部分系统服务或应用程序的流量可能无法准确归属。因此，逐应用程序统计之和可能与总流量存在差异。HotspotMeter 不会人为分摊或伪造流量数据。

## 🖥️ 界面预览

项目截图：

| 桌面悬浮胶囊 | 流量统计窗口 |
| --- | --- |
| 待添加截图 | 待添加截图 |

## 🛠️ 技术栈

- **开发语言**：C#
- **桌面框架**：WPF
- **运行平台**：Windows
- **数据存储**：SQLite
- **网络数据**：Windows Network APIs、WinRT Network Usage API
- **系统交互**：Win32 API

## 📋 系统要求

- Windows 系统
- .NET Desktop Runtime（具体版本要求以正式发布版本为准）

## 🚀 获取与构建

项目目前可通过源码获取和构建。

```bash
git clone https://github.com/Sharkin-Tao/HotspotMeter.git
```

使用 Visual Studio 或 .NET CLI 打开项目并构建。

```bash
dotnet restore
dotnet build -c Release
```

正式发布后，将在 GitHub Releases 页面提供可用的程序包。

## 🐛 问题反馈

如果你在使用过程中遇到问题，欢迎通过 GitHub Issues 反馈。

提交问题时，建议提供：

- Windows 版本
- HotspotMeter 版本
- 问题复现步骤
- 相关截图或日志

请在分享日志或截图前注意隐藏个人隐私信息。

## 📄 License

项目许可证待确定。
