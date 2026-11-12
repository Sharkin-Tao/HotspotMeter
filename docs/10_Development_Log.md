# 10 开发日志

> 记录每次实际执行的操作、命令、结果与遗留问题。只写发生过的事；计划与设想去 08/09。

---

## 2026-11-12 阶段 0：环境检查、项目骨架、知识库

### 1. 环境检查（实际输出）

| 项目 | 结果 |
| --- | --- |
| 工作目录 | `E:\HotspotMeter`（初始为空，无 Git 仓库） |
| 操作系统 | Windows 11 专业版，版本 `10.0.26200`，x64（AMD64） |
| Git | `2.55.0.windows.2`，全局身份 `Sharkin-Tao <1365758196@qq.com>` |
| .NET SDK | `10.0.402`（唯一 SDK） |
| 运行时 | `Microsoft.NETCore.App` 7.0.20 / 9.0.22 / 10.0.13；`Microsoft.WindowsDesktop.App` 7.0.20 / 9.0.22 / 10.0.13 |
| WPF 模板 | `wpf`、`wpflib`、`wpfusercontrollib`、`wpfcustomcontrollib` 均可用 |
| NuGet 源 | `nuget.org`（唯一源） |
| 选定包版本 | `Microsoft.Data.Sqlite 10.0.15`、`Microsoft.Extensions.Logging.Abstractions 10.0.15`、`xunit 2.9.3`、`Microsoft.NET.Test.Sdk 17.14.1`、`xunit.runner.visualstudio 3.1.4`、`coverlet.collector 6.0.4` |

### 2. 环境问题与处理

**问题**：会话开始时所有 `pwsh` 调用失败，报错
`Error: SetNamedSecurityInfoW failed (Win32 5): grantWrite(E:\HotspotMeter)`，
即 DSH 无法为工作区写入授权。

**处理**：按 `diagnose-windows-sandbox-acl` 技能运行诊断脚本。第一次用 `powershell.exe -File` 因执行策略被拒（`PSSecurityException`），改用
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...` 后成功。

**结果**：`E:\HotspotMeter` 缺少 `WRITE_OWNER` 权限（所有者本身正确）；脚本为当前用户添加 `FullControl` ACE，复核 `writeDac/writeOwner` 均为 true，退出码 0，无需回滚。报告与备份在 `E:\HotspotMeter-acl-recovery\`。此后会话变为 `danger-full-access`、审批禁用。

**结论**：属于环境权限问题，与项目代码无关；不影响后续开发。

### 3. 执行的操作

```powershell
git init -b main
dotnet new sln -n HotspotMeter --format sln        # 显式选 .sln（默认已是 .slnx）
dotnet new wpf        -n HotspotMeter.App            -o HotspotMeter.App            -f net10.0
dotnet new classlib   -n HotspotMeter.Core           -o HotspotMeter.Core           -f net10.0
dotnet new classlib   -n HotspotMeter.Infrastructure -o HotspotMeter.Infrastructure -f net10.0
dotnet new xunit      -n HotspotMeter.Tests          -o HotspotMeter.Tests          -f net10.0
dotnet sln HotspotMeter.sln add <四个项目>
dotnet add HotspotMeter.Infrastructure reference HotspotMeter.Core
dotnet add HotspotMeter.App reference HotspotMeter.Core HotspotMeter.Infrastructure
dotnet add HotspotMeter.Tests reference HotspotMeter.Core HotspotMeter.Infrastructure
dotnet new gitignore
dotnet new editorconfig
```

随后手工完成：

- `Directory.Build.props`（LangVersion/Nullable/ImplicitUsings/Deterministic）、`Directory.Packages.props`（CPM）、`global.json`（10.0.402）、`app.manifest`（PerMonitorV2）。
- 删除模板 `MainWindow.xaml(.cs)`、`Class1.cs`、`UnitTest1.cs`。
- `App.xaml` 去掉 `StartupUri`；`App.OnStartup` 中 `Shutdown()`（阶段 0 无窗口）。
- 新增 `Core/Formatting/ByteSizeFormatter.cs` 与两个测试文件。
- 建立 `PROJECT_CONTEXT.md`、`README.md`、`AGENTS.md` 与 `docs/00`–`10`。

### 4. 构建与测试结果

| 命令 | 结果 |
| --- | --- |
| `dotnet build HotspotMeter.sln -c Debug`（首次） | 成功，0 警告 0 错误，约 7.35 秒 |
| `dotnet test HotspotMeter.sln -c Debug`（首次） | 12 通过 1 失败 |
| 失败原因 | `Format_Uses1024Base_NotDecimal` 期望值写错：1,000,000,000 字节按 1024 进制是 `953.67 MB`，不是 `0.93 GB` |
| 修正 | 改为验证 `1,000,000 → 976.56 KB` 与 `1,000 → 1000 B` |
| `dotnet test`（重跑） | **13/13 通过**，38 ms |
| `dotnet run --project HotspotMeter.App`（首次） | 进程不退出（无窗口 + `OnLastWindowClose`） |
| 修正 | `OnStartup` 调用 `Shutdown()`；重跑退出码 0 |
| 原生库检查 | `HotspotMeter.Tests\bin\Debug\net10.0\runtimes\win-x64\native\e_sqlite3.dll` 存在，`SELECT sqlite_version()` 成功 |

### 5. 已验证的事实

1. `.NET 10.0.402` 可创建并构建 `net10.0-windows` WPF 项目。
2. `Microsoft.Data.Sqlite 10.0.15` 在 .NET 10 上可加载原生 SQLite 并执行查询。
3. `Microsoft.Extensions.Logging.Abstractions 10.0.15` 可还原（尚未使用）。
4. 开 CPM 后，项目文件不带版本号也能正常还原与构建。
5. `AGENTS.md` 会被 DeepSeek Harness 自动加载（写入后本会话立即收到其内容）。

### 6. 未验证 / 遗留

- WLAN API、IP Helper 的全部假设（见 `docs/05` 第 10 节），留待阶段 1。
- `AllowsTransparency` 性能、任务栏定位可行性，留待阶段 4。
- `App` 目前引用 `Infrastructure` 但未使用其任何类型（组合根尚未实现），不影响构建。

### 7. 收尾复核

- `dotnet clean` 后重新 `dotnet build`：0 警告 0 错误（1.56 秒）。
- `dotnet test --no-build`：13/13 通过（42 ms）。
- `git status` 干净，无 `bin/obj` 入库。

### 8. 提交记录

| 提交 | 说明 |
| --- | --- |
| `bce7260` | 初始化解决方案与项目知识库（阶段 0），31 个文件 |
| `d702a40` | 统一换行规范（`.gitattributes`，工作区 CRLF / 索引 LF） |

### 9. 下一步

等待用户确认阶段 0。确认后按 `docs/08` 进入阶段 1：
`tools/ApiProbe` 控制台原型 + P1-1～P1-8 验证 + 结论回写 `docs/05`、`docs/09`。