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

---

# 阶段 1：Windows API 与技术可行性验证（2026-11-12）

### 1. 范围

只做一次性验证原型 `tools/ApiProbe/`：不改 App/Core/Infrastructure 业务代码，不实现正式监控服务，不开发 UI，不引入第三方依赖（HTTP 下载、参数解析、JSON 全部用 BCL）。

### 2. 新增文件

| 路径 | 作用 |
| --- | --- |
| `tools/ApiProbe/ApiProbe.csproj` | net10.0-windows 控制台，`IsPackable=false`，**不加入解决方案** |
| `Native/WlanApi.cs` | `WlanOpenHandle/EnumInterfaces/QueryInterface/FreeMemory/CloseHandle` |
| `Native/IpHlpApi.cs` | `GetIfTable2/FreeMibTable` + `MIB_IF_ROW2` |
| `Commands/ListCommand.cs` | P1-1/P1-2：接口与 GUID 映射 |
| `Commands/WatchCommand.cs` | P1-4/P1-6/P1-8：连续采样、速度、回退/长暂停处理 |
| `Commands/DownloadCommand.cs` | P1-3：Range 分段下载 + 计数器对比 |
| `Commands/ContinuityCommand.cs` | P1-9/P1-10/P1-11：`--save` / `--verify` 基线 |
| `Support/*` | 参数解析、格式化、布局自检、JSON、CSV |

### 3. 验证结果（全部真实运行）

| 项 | 结论 | 数值摘要 |
| --- | --- | --- |
| P1-1 | 通过 | SSID `OPPO Find X9`、BSSID `1A:44:DD:0E:E5:B0`，与系统一致 |
| P1-2 | 通过 | 结构体 1352/604/532；GUID 唯一映射 1 行，Type=71 |
| P1-3 | 通过 | 150 MB 下载 → InOctets +161,933,750 B，比值 1.0295；BCL 交叉对比 16 MiB 时差 0 字节 |
| P1-4 | 通过 | 空载 60 s 约 3 KB/s 背景流量，0 回退、0 缺帧 |
| P1-5 | 通过 | 禁用网卡 → 计数 0/0、26 s 冻结、随后小值恢复；增量无负数 |
| P1-6 | 通过（修复后） | 睡眠 94,534 ms 被识别，唤醒首帧增量 0 |
| P1-7 | 通过 | 墙钟 +2 h 跳变，单调时钟不受影响，0 回退 |
| P1-8 | 通过 | 16 行 Type=71；按类型求和 = 真实值 6.00 倍 |
| P1-9 | 通过 | 新进程首帧 inDelta=0 |
| P1-10 | 通过 | 外部 5 MiB 下载 → gap 5,492,930/87,098 B 可恢复 |
| P1-11 | 通过 | 真机重启 → `REBOOT_DETECTED`，拒绝恢复 |

证据文件在 `tools/ApiProbe/out/`（不入库）：`list.json`、`p1-5.csv`、`p1-6b.csv`、`p1-7.csv`、`continuity-after-5mb.json`、`continuity-after-reboot.json`。

### 4. 本轮发现的真实缺陷

1. **墙钟计时导致睡眠后立即退出**：第一版 `watch` 用 `Stopwatch.Elapsed.TotalSeconds` 判停，睡眠跨过 316 s 后循环直接结束，一帧唤醒数据都没采到。改为累计"有效采样时长"（`gapMs > 3×interval` 不计入并强制增量 0）。
2. **接口拆除瞬间的计数器 0 读数**：P1-6b 睡眠开始时读到 `0`、唤醒后读回 24,191,795，若直接相减会凭空多出约 23 MB。已加入 `sleepGap` 规则并写入 `D-020`（恢复后两帧确认）。
3. **首次构建 6 个编译错误**：`CsvSampleWriter.Path` 属性遮蔽 `System.IO.Path`；`WlanApi.cs` 缺 `using ...Support`。均已修复，未改动验证逻辑。
4. **Cloudflare 下载端点返回 403**：`speed.cloudflare.com/__down` 在本网络不可用（加 UA 也 403），改用 `ash-speed.hetzner.com/100MB.bin`（支持 Range，206）。

### 5. 构建与提交

- `dotnet build tools/ApiProbe/ApiProbe.csproj -c Release`：0 警告 0 错误。
- `dotnet build HotspotMeter.sln -c Debug`：0 警告 0 错误（产品代码未被改动）。
- 提交 `7650bcf`：阶段 1 原型源码 + `tools/ApiProbe/out/` 忽略规则。

### 6. 项目目录迁移（用户操作）

用户把项目从 `E:\HotspotMeter` 迁移到 `E:\hotspotmeter\HotspotMeter`，原目录下恰好存在一个无关克隆仓库（`bd9001a Initial commit`），导致嵌套 `.git`。处理方式：把本项目 `.git` 迁入新目录作为唯一仓库，克隆的 `.git` 移至 `E:\hotspotmeter-clone-git-backup` 保留（未删除），见 `D-025`。

### 7. 下一步

阶段 1 全部 11 项验证完成，等待用户确认。确认后进入阶段 2：Core 接口 + Infrastructure 采样引擎 + 状态机 + 单元测试（无 UI）。

---

# 阶段 2：热点识别与实时流量引擎（2026-11-12）

### 1. 范围与门禁

用户 m00757 批准计划（含恢复幂等与职责分离两项追加要求）后，按步骤 1→5 小步实施：Core 模型/接口 → 累积器 + 恢复判定 + 单测 → Infrastructure interop → MonitorEngine + 引擎测试 → MonitorHost 真机比对 + 文档。未改 `tools/ApiProbe`，未做 UI/SQLite，无新增第三方依赖。

### 2. 新增文件

| 路径 | 作用 |
| --- | --- |
| `HotspotMeter.Core/Models/Network.cs` | 接口/读数/候选/目标/匹配结果 |
| `HotspotMeter.Core/Models/MonitorState.cs` | 六状态：Stopped/Running/InterfaceMissing/CounterReset/Suspended/Rebooted |
| `HotspotMeter.Core/Models/MonitorSnapshot.cs` | 17 字段快照（会话累计、速度、gap、确认帧、未并入恢复量） |
| `HotspotMeter.Core/Models/MonitorEvents.cs` | 会话开始/结束、BSSID、异常、恢复候选 |
| `HotspotMeter.Core/Models/Recovery.cs` | 基线、恢复上下文、判定结果、gap、连续性、RecoveryId |
| `HotspotMeter.Core/Models/MonitorFrame.cs` | 单帧输入 + 单步输出 |
| `HotspotMeter.Core/Abstractions/*` | 检测器/计数器/三种时钟/连续性证据/引擎接口 |
| `HotspotMeter.Core/Monitoring/MonitorOptions.cs` | 间隔、长暂停倍数、确认帧、合理性模式、开机容差 |
| `HotspotMeter.Core/Monitoring/TrafficAccumulator.cs` | 会话累计、基线、两帧确认、BSSID、合理性检查、恢复合入 |
| `HotspotMeter.Core/Monitoring/BaselineResumeEvaluator.cs` | 纯恢复判定与增量计算 |
| `HotspotMeter.Core/Monitoring/HotspotMatcher.cs` | GUID 主判、SSID 复核 |
| `HotspotMeter.Core/Monitoring/NoEvidenceContinuityProvider.cs` | 默认证据源：恒 Unverified |
| `HotspotMeter.Infrastructure/Interop/WlanApi.cs` | WLAN P/Invoke 与映射 |
| `HotspotMeter.Infrastructure/Interop/IpHlpApi.cs` | GetIfTable2/FreeMibTable 与行读取 |
| `HotspotMeter.Infrastructure/Monitoring/WlanHotspotDetector.cs` | 热点识别 |
| `HotspotMeter.Infrastructure/Monitoring/InterfaceTrafficCounter.cs` | 按 GUID 取唯一计数行 |
| `HotspotMeter.Infrastructure/Monitoring/MonitorEngine.cs` | 采样循环、事件、恢复编排与幂等 |
| `HotspotMeter.Infrastructure/SystemClocks.cs` | Stopwatch / UTC / TickCount64 |
| `HotspotMeter.Tests/TestDoubles/*` | 假时钟、假检测器、假计数器、假证据源 |
| `HotspotMeter.Tests/Unit/Monitoring/*` | 累积器 21 例、恢复判定 11 例、引擎 9 例（合计 54，含原有 13） |
| `tools/MonitorHost/*` | 控制台宿主（不入解决方案） |

### 3. 测试与真机结果

- `dotnet build HotspotMeter.sln -c Debug`：0 警告 0 错误。
- `dotnet test HotspotMeter.sln`：**54/54 通过**（2 s）。
- 真机 45 s（含 60 MB 限额下载）：引擎 61.6 MB / 715.06 KB；`Get-NetAdapterStatistics` 计数差 64,637,304 / 746,352 B；差异 <0.1%，来自采样边界与后台流量。
- 启动即连接：首帧增量 0，会话从 0 起算，未把历史累计值计入。

### 4. 本轮发现的真实缺陷

1. **恢复判定顺序错误**：先判行缺失导致"未连任何 Wi-Fi"报成 `InterfaceMissing`。修正为"先判目标匹配，再判行缺失"，并让检测异常统一走 `InterfaceMissing`。
2. **测试绕过配置校验**：引擎测试用 10 ms 间隔，被 `MonitorOptions.Validate()`（最小 500 ms）拒绝。改测试为 500 ms，而不是放宽校验。
3. **测试期望值笔误**：恢复合并后 upload 期望写成 1050，正确是 550（会话 50 + gap 500）。改测试。
4. **`HotspotConnection` 是引用类型**：MonitorHost 误写 `connection.Value`（CS1061），去掉 `.Value`。

### 5. 构建与提交

- 提交见本条日志追加的 Git 记录；`tools/MonitorHost` 源码入库，无运行产物。
- `tools/ApiProbe` 及其历史未改动。

### 6. 下一步（阶段 3，待用户确认）

SQLite 表结构与迁移、检查点写入、会话收尾、`RecoveryCandidate` 的持久化与展示、`DailyTraffic` 聚合与跨午夜拆分。