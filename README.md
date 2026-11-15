# HotspotMeter

### Windows Hotspot Traffic Monitor

**Know your hotspot traffic at a glance.**

![Windows](https://img.shields.io/badge/Platform-Windows-0078D4?style=flat-square&logo=windows&logoColor=white)
![CSharp](https://img.shields.io/badge/Language-C%23-239120?style=flat-square&logo=csharp&logoColor=white)
![WPF](https://img.shields.io/badge/Framework-.NET%20%2F%20WPF-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![SQLite](https://img.shields.io/badge/Database-SQLite-003B57?style=flat-square&logo=sqlite&logoColor=white)

[简体中文](README.md) | **English**

---

HotspotMeter is a desktop traffic monitoring tool for Windows. It helps you keep track of your mobile hotspot connection status, real-time network traffic, and application-level data usage.

Monitor your hotspot traffic directly from your desktop without repeatedly opening Windows Settings.

## ✨ Features

- **Real-time Traffic Monitoring**: View hotspot connection status, session traffic, and upload/download speeds.
- **Desktop Floating Capsule**: A lightweight floating window for convenient traffic monitoring.
- **Flexible Window Sizing**: Multiple preset sizes and custom dimensions.
- **Position Locking**: Lock the floating window in place to prevent accidental movement.
- **Taskbar Adaptation**: A taskbar display mode for a more integrated desktop experience.
- **Historical Traffic Statistics**: View traffic usage for today, the last 7 days, or a selected date.
- **Per-Application Traffic Statistics**: Display application-level traffic attribution provided by Windows network APIs.
- **System Tray Integration**: Access statistics and manage the application from the system tray.
- **Startup on Boot**: Optionally launch HotspotMeter when Windows starts.
- **Background Monitoring**: Traffic monitoring continues even after the statistics window is closed.

> Note: Per-application traffic statistics rely on network attribution data provided by Windows. Some system services or applications may not be accurately attributed, so the sum of application-level traffic may differ from the total traffic. HotspotMeter does not fabricate or artificially distribute traffic data.

## 🖥️ Screenshots

| Desktop Floating Capsule | Traffic Statistics |
| --- | --- |
| Screenshot coming soon | Screenshot coming soon |

## 🛠️ Tech Stack

- **Language**: C#
- **Desktop Framework**: WPF
- **Platform**: Windows
- **Data Storage**: SQLite
- **Network Data**: Windows Network APIs, WinRT Network Usage API
- **System Integration**: Win32 API

## 📋 Requirements

- Windows
- .NET Desktop Runtime (the exact version requirement will be specified with the official release)

## 🚀 Getting Started

The source code can currently be cloned and built locally.

```bash
git clone https://github.com/Sharkin-Tao/HotspotMeter.git
```

Open the project with Visual Studio or use the .NET CLI.

```bash
dotnet restore
dotnet build -c Release
```

Pre-built packages will be available on the GitHub Releases page after the official release.

## 🐛 Feedback & Issues

If you encounter a bug or have a suggestion, feel free to open an issue on GitHub.

When reporting a bug, please include:

- Windows version
- HotspotMeter version
- Steps to reproduce
- Relevant screenshots or logs

Please remove any personal or sensitive information before sharing logs or screenshots.

## 📄 License

The project license has not yet been determined.
