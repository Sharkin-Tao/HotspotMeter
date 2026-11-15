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
| P1-1 | 通过 | SSID `TestHotspot`、BSSID `02:00:00:00:00:01`，与系统一致 |
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

---

# 阶段 2 收尾修正 F-1：恢复会话边界（2026-11-12）

### 1. 背景与授权

阶段 2 终验（m01092）发现：Unverified 恢复只把 gap 标记为"未并入"，会话仍是原 `SessionId`，恢复后的新流量继续累加到旧会话；非 Continued 的五个结局直接丢弃旧会话且不发 `SessionEnded`。用户 m01093 仅授权 F-1 修正，明确不批准进入阶段 3。

### 2. 改动文件

| 文件 | 改动 |
| --- | --- |
| `HotspotMeter.Core/Models/MonitorEvents.cs` | `SessionEndReason` 追加 `RecoveryUnconfirmed`、`RecoveryRejected`；`MonitorSessionEndedInfo` 增加可选 `ResumeOutcome? ResumeOutcome = null` |
| `HotspotMeter.Core/Monitoring/TrafficAccumulator.cs` | 新增 `_lastClosedSessionId` 水位；`AdoptResumedSession` 非合并分支结束旧会话并以新 SessionId 开新会话；`ResetForResume(ResumeContext, ResumeOutcome, DateTimeOffset)` 发带具体结局的 `SessionEnded`；`EndSession` 推进水位；新增 `CloseRecoveredSession`、`AllocateSessionId`、`NextSessionIdAfter` |
| `HotspotMeter.Infrastructure/Monitoring/MonitorEngine.cs` | 两处 `ResetForResume` 调用改传 `context` |
| `HotspotMeter.Tests/Unit/Monitoring/TrafficAccumulatorTests.cs` | 替换 1 例、新增 5 例（切会话、新会话 0、下一帧归属、Interrupted、单次结束与 Id 不复用） |
| `HotspotMeter.Tests/Unit/Monitoring/MonitorEngineTests.cs` | 强化 3 例（Unverified 切会话、Rebooted 带结局且重复调用不重复结束、NotOnTarget 发结束事件）；Continuous 断言不产生 `SessionEnded` |

### 3. 核心行为

- Unverified/Interrupted：旧会话在 `gap.FromUtc` 结束（`RecoveryUnconfirmed` + `ResumeOutcome=Continued`），同帧新会话从 0 开始，gap 只留候选；`HasUnmergedRecovery` 在切会话后仍保留。
- 其余非 Continued：旧会话在 `Baseline.CapturedAtUtc` 结束（`RecoveryRejected` + 具体结局），状态清理，无静默丢失。
- Continuous：不变（沿用 SessionId、`SessionResumed`、不产生 `SessionEnded`）。
- SessionId 严格递增，Int64 上限抛异常；同一 Id 最多一次 `SessionEnded`。

### 4. 验证

- `dotnet build HotspotMeter.sln`：0 警告 0 错误。
- `dotnet test HotspotMeter.sln`：**58/58 通过**（2 s）。
- `dotnet build tools/MonitorHost`：0 警告 0 错误。
- 独立提交：`阶段2: 修正恢复会话边界（F-1）`。

### 5. 未做与边界

- 未实现 SQLite；`RecoveryGap` 的持久化、唯一索引、更新策略留给阶段 3（约束见 `docs/06` §3.1）。
- 跨进程重复结束同一会话仍需数据库唯一约束兜底，本阶段只保证进程内单次结束。
- 未验证：真实崩溃后再次启动时，数据库中的旧会话 Id 与本进程分配 Id 的衔接（阶段 3 验证）。

---

# 阶段 3A：SQLite 基础设施与迁移骨架（2026-11-12）

### 1. 范围与授权

用户 m01299 授权阶段 3A：只建立数据库基础设施与最小元数据，**不**冻结业务表、不接入监控数据流、
不实现优先级写入、不做 UI。开始前复核：`main@2e0defb`、工作区干净、58/58 通过、解决方案与
MonitorHost 均 0 警告 0 错误。

### 2. 新增文件

| 文件 | 职责 |
| --- | --- |
| `HotspotMeter.Infrastructure/Persistence/DatabasePaths.cs` | 数据库/隔离目录唯一定义；默认 `%LOCALAPPDATA%`；拒绝相对路径与程序目录 |
| `HotspotMeter.Infrastructure/Persistence/SqliteConnectionFactory.cs` | 连接字符串 + PRAGMA（外键、忙等待、NORMAL、WAL 校验）；关闭连接池 |
| `HotspotMeter.Infrastructure/Persistence/SchemaMigrator.cs` | `user_version` 唯一权威；v1 元数据表；单事务迁移；高版本拒绝 |
| `HotspotMeter.Infrastructure/Persistence/DatabaseHealth.cs` | `PRAGMA quick_check`；缺失文件视为待创建；结构化失败结果 |
| `HotspotMeter.Infrastructure/Persistence/DatabaseQuarantine.cs` | db/-wal/-shm/-journal 整体隔离；失败还原并抛异常 |
| `HotspotMeter.Infrastructure/Persistence/DatabaseBootstrapper.cs` | 健康检查 → 隔离 → 迁移 的启动编排 |
| `HotspotMeter.Infrastructure/Persistence/IDatabaseWriter.cs` | 串行写入契约 |
| `HotspotMeter.Infrastructure/Persistence/SqliteWriter.cs` | 单消费者 FIFO、每项独立事务、排空式停止 |
| `HotspotMeter.Infrastructure/SingleInstance/SingleInstanceGuard.cs` | `Local\` 命名 Mutex + 进程内去重 |
| `HotspotMeter.Tests/TestSupport/TempDirectory.cs` | 每测试独立临时目录 |
| `HotspotMeter.Tests/Unit/Persistence/*` | 8 个测试类，见下 |

Schema v1 DDL 只有：

```sql
CREATE TABLE SchemaInfo (Key TEXT PRIMARY KEY NOT NULL, Value TEXT NOT NULL);
```

种子键：`created_at_utc`、`initialized_at_utc`、`app_version`（均 `INSERT OR IGNORE`，重复初始化不改写创建时间）。

### 3. 测试与构建

- `dotnet test HotspotMeter.sln`：**101/101 通过**（2 s）；阶段 2 的 58 例全部保留通过。
- 新增 43 例：路径 5、连接工厂 5、迁移 6、健康 4、隔离 5、启动编排 4、Writer 9、单实例 5。
- `dotnet build HotspotMeter.sln`：0 警告 0 错误。
- `dotnet build tools/MonitorHost/MonitorHost.csproj`：0 警告 0 错误。
- `tools/ApiProbe` 未改动。

### 4. 实现中预先处理的两个易错点

1. **WAL 是数据库级设置**：连接工厂仍每次执行 `PRAGMA journal_mode=WAL` 并校验返回值为 `wal`，
   目的是在只读目录/不支持 WAL 的存储上尽早失败，而不是把它当成"每连接开关"。
2. **Mutex 对同线程可重入**：只靠命名 Mutex 无法阻止同一进程重复获取，`SingleInstanceGuard`
   因此增加静态进程内集合；`AbandonedMutexException` 视为"上次异常退出、本次获得所有权"。

本轮没有出现编译失败或测试失败；上述两点是设计阶段识别并直接规避的风险。

### 5. 明确边界（未做）

- 业务表（HotspotProfiles/ConnectionSessions/TrafficSnapshots/DailyTraffic/RecoveryGaps/AppSettings）
  与索引、唯一约束：留待 3B 审核后以 v2 迁移加入。
- `SessionManager`、检查点、跨午夜拆分、启动恢复、`DailyTraffic` 重算、日志文件提供程序：3C–3F。
- Writer 未实现优先级、多队列、重试与业务命令；连接未接入 `MonitorEngine`。
- App 启动流程未改：单实例守卫目前是独立组件，3B/4 接入。

### 6. 下一步

等待用户审核 3A 的代码、测试、文档与提交；批准后再规划 3B（冻结业务表结构与约束）。

---

# 阶段 3B：业务表与数据层（2026-11-13）

3B 被拆成 8 个子步，逐段批准。3B-1～3B-3 在本节之前完成（当时只授权代码，未授权改文档），
补记如下；3B-4 是本节主体。

| 子步 | 内容 | 提交 |
| --- | --- | --- |
| 3B-1 | Schema v2：9 张表、复合外键、部分唯一索引、三态与日期 CHECK | `d08e036` |
| 3B-2 | `ProfileResolver` / `SessionRepository` / `BssidRepository` | `3b73089` |
| 3B-3 | `OperationIdFactory` / `SqliteTypeConversion` | `5c40984` |
| 3B-4 | 时间切分与日汇总（本节） | 本次提交 |

## 阶段 3B-4：时间切分与日流量汇总（2026-11-13）

### 1. 范围与基线

只实现日归属与日汇总的数据层：`LocalDayResolver`、`NightlySplitter`、`EffectiveTimePolicy`、
`DailyTrafficDelta`、`DailyTrafficRepository`。**不**接入快照写入、StepBuffer、`StepProduced`、
会话持久化或 UI；不改 Schema v2、`SqliteWriter`、Core、MonitorHost、ApiProbe。

开工复核：`main@5c40984`、工作区干净、173/173 通过；Infrastructure 与解决方案均可构建。

### 2. 新增文件

源码（`HotspotMeter.Infrastructure/Persistence`）：

- `Time/LocalDayResolver.cs`：UTC 偏移分段 + tick 二分求本地日边界；`GetLocalDate`、
  `GetDayStartUtc`、`EnumerateDayStartsUtc`。
- `Time/TrafficSegment.cs`：一个本地日片段的只读记录（日期、UTC 区间、三项增量）。
- `Time/NightlySplitter.cs`：BigInteger 累计取整分配；字节/上传/毫秒各自独立、总和严格守恒。
- `Time/EffectiveTimePolicy.cs`：墙钟调整的事件级计数（`None/Backward/Forward`、2 s 容差、
  单调回退单独计数、checked 溢出）。
- `Repositories/DailyTrafficDelta.cs`：私有构造 + `Create` 全量校验（含 SessionCount 0/1 与
  First/Last 联动、日期真实性）。
- `Repositories/DailyTrafficRepository.cs`：非幂等增量 UPSERT（累加、`MIN`/`MAX` 端点）、
  读取单行；不自开事务、不重试。

测试（84 例）：

- `Unit/Persistence/LocalDayResolverTests.cs` 12 例。
- `Unit/Persistence/NightlySplitterTests.cs` 24 例。
- `Unit/Persistence/EffectiveTimePolicyTests.cs` 18 例。
- `Integration/Persistence/DailyTrafficRepositoryTests.cs` 30 例（真实 SQLite：UPSERT、MIN/MAX、
  外键、CHECK、回滚、重复应用翻倍）。

### 3. 实现中发现的真实缺陷

`EffectiveTimePolicy.Advance` 首版把墙钟毫秒差**直接当作 tick** 推进有效时间
（1000 ms 只前进 1000 tick，即 0.1 ms），且墙钟前跳时把有效时间吸附到墙钟，
导致会话时长被放大。修正为：

- `checked(_lastEffectiveTicks + deltaMilliseconds * TimeSpan.TicksPerMillisecond)`；
- 有效时间永远取"单调推进值"，墙钟只用于调整事件计数；
- 单调时钟回退时有效时间保持不动、单独计数。

其余失败全部是我的测试期望算错（1/3 分配、段数、跨时区同日、SessionCount=1 需带端点时间、
`ClockAdjustmentCount` 含单调回退、`ThrowsAny<ArgumentException>`），均改测试而不改实现。

### 4. 验证

- `dotnet test`：**257/257 通过**（2 s）；原有 173 例全部保留。
- `dotnet build HotspotMeter.sln`：0 警告 0 错误。
- `dotnet build tools/MonitorHost/MonitorHost.csproj`：0 警告 0 错误。
- `git status`：仅本阶段 6 个源码与 4 个测试文件，无临时数据库或日志残留。

### 5. 已知边界

- `DailyTrafficRepository` 不幂等，重复应用会翻倍；幂等必须由 3B-5/3B-7 的
  "快照 + 日汇总同一事务"保证。
- 15 分钟粗采样只用于发现偏移变化；若某次偏移变化持续不足 15 分钟且完全落在采样间隙，
  理论上可能漏检（Windows 时区调整的实际粒度远大于此，暂接受）。
- Samoa 整日跳过在 Windows tzdata 下的建模与 IANA 不一致，测试只断言与数据无关的性质
  （边界单调、无重复、无零长度段），未伪造精确时间点。

### 6. 下一步

3B-5：`StepBuffer` / `StepPump` / JSONL 日志与关键步骤保留策略；等待批准。

## 阶段 3B-5a：Schema v3、UTC 时间戳与操作摘要编码（2026-11-13）

### 1. 范围

用户 m02109 仅批准四项：Schema v3 及迁移、`UtcTimestamp`、操作摘要与 PayloadDigest 编码、
对应测试与必要文档。**不**实现 Settlement 专用入口、`SqliteWriter` Faulted 状态机、午夜调度器；
不改 `MonitorEngine`、不引后台定时线程、不弱化既有约束。

### 2. 新增文件

- `Persistence/SchemaV3.cs`：`TrafficOperations` 表与 `IX_Operations_Session_FromUtc` 索引；
  `TimeCheck` 生成经 SQLite 3.53.3 实测修正的 UTC 文本约束。
- `Persistence/Time/UtcTimestamp.cs`：固定格式 `yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'`（33 字符）、
  `Format`/`FromUtc`/`TryParse`/`Parse`/`IsWellFormed`，只接受零偏移且要求格式化往返一致。
- `Persistence/Operations/SnapshotWriteCommand.cs`：`SnapshotQualityFlags`、`SegmentInput`、
  `SnapshotWriteCommand` 与结构校验（不含任何写库行为）。
- `Persistence/Operations/OperationContentDigest.cs`：二进制规范化编码（版本字节 + 长度前缀域串 +
  大端定长整数 + 长度前缀 UTF-8），`ComputeRequest` 与 `ComputePayload`，SHA-256 小写十六进制。
- `SchemaMigrator`：`SupportedVersion = 3`，追加 `business-schema-v3` 迁移。

### 3. 关键设计

- `TrafficOperations` 是"操作级幂等"的载体：一次逻辑写入对应一行，快照分段仍落在
  `TrafficSnapshots`；操作行带 `RequestDigest`、`PayloadDigest`、`SegmentCount`、水位终值与
  `AppliedAtUtc`，`UNIQUE(DbSessionId, OperationSequence)` 锁定会话内顺序。
- 时间列用定长 UTC 文本而非 SQLite 的宽松日期解析；`24:00:00` 与 `29:00:00` 的实测漏洞都已由
  自动化测试钉住。
- 摘要编码刻意不拼接分隔符字符串：时区 ID 等自由文本不可能通过注入分隔符制造碰撞，
  字段边界由长度前缀和定长整数保证。

### 4. 验证

- `dotnet test`：**348/348 通过**（2 s）；原 257 例全部保留，新增 91 例。
- `dotnet build HotspotMeter.sln`：0 警告 0 错误。
- `dotnet build tools/MonitorHost/MonitorHost.csproj`：0 警告 0 错误。
- 刻意调整 `SchemaV2Tests` 两处版本断言（2 → 3）并加入 `TrafficOperations` 期望表：
  schema 版本推进属于预期变更，不是弱化约束。

### 5. 已知边界

- 摘要只证明"同样的输入得到同样的字节串"，不证明输入本身合法；合法性仍由
  `SnapshotWriteCommand.Validate`、数据库 CHECK 与后续服务层共同保证。
- `TrafficOperations` 尚未被任何写入路径使用；真正的幂等事务在 3B-5b 及之后接入。
- 未用于 3D 的"从数据库行重算 PayloadDigest"：编码器已接受完整字段集，但行映射留给 3B-5b。

### 6. 下一步

等待用户代码审查与 3B-5a 最终验收；通过后才提交并推送。3B-5b 未获授权。

---

## 2026-11-13 阶段 4C-1：悬浮胶囊首版

### 1. 目标与范围

按用户 m02357 的授权，先实现悬浮胶囊（宽型绿色圆角矩形），完成后暂停等验收；
本轮不做统计窗口、设置窗口、历史写入管线、托盘或隐藏功能。

### 2. 新增 / 修改文件

- `HotspotMeter.App/Theme.xaml`：深色色板（`BgApp`、`BgCard`、`BgPill`、`AccentGreen`、
  `StateConnected`、`StateDisconnected` 等）与 `CapsuleMenuItemStyle`。
- `HotspotMeter.App/ViewModels/CapsuleViewModel.cs`：连接状态、会话流量、说明文字、
  上下行速度、绿/红背景、固定状态；`Apply(MonitorSnapshot?)`、`ApplySessionStarted`、
  `ApplySessionEnded`、`SetWarning`、`ShowUnavailable`。
- `HotspotMeter.App/Views/CapsuleWindow.xaml(.cs)`：320×104 DIP、圆角 20、无边框透明、
  Topmost、不占任务栏；左上 Wi-Fi 图标（未连接叠加斜杠）+ 状态；左中 34 号会话流量 +
  “已使用流量”；右下速度；右键菜单（流量统计/设置暂禁用、固定/解除、退出）。
- `HotspotMeter.App/Services/WindowStateStore.cs`：位置 + 固定状态 JSON 存储。
- `HotspotMeter.App/Services/MonitorSessionService.cs`：选目标热点、构造并启动
  `MonitorEngine`、把 7 类事件切回 UI 线程；退出时停止引擎。
- `HotspotMeter.App/Services/UiDump.cs`：开发期布局诊断（环境变量开关，默认不启用）。
- `App.xaml`（合并 Theme）、`App.xaml.cs`（组合根，`ShutdownMode=OnExplicitShutdown`）。

### 3. 关键实现与修正

- 首次位置按 Win32 工作区计算：DPI=1.5 下若把物理像素当 DIP 会跑到屏外，已按 D-047 修正。
- 行高不足导致数字与“已使用流量”重叠：改为两行 Grid（34 号字、`LineHeight=40`），
  速度与说明同处最后一行、左右分置。
- 无会话时数字 `--`、说明“已使用流量”；有会话后显示真实累计与速度。
- 目标热点暂取“已连接且信号最强”的 Wi-Fi（D-048），不按 SSID 猜测。

### 4. 验证

- `dotnet build HotspotMeter.App`：0 警告 0 错误。
- `dotnet test`：**348/348 通过**（UI 改动不影响既有测试）。
- 真机运行（DPI 1.5）：`window.left=1362.7 top=890.7 actual=320x104`；`已连接`、
  `5.38 KB`、`↓1.59 KB/s ↑1.6 KB/s`；`PrintWindow` 截图
  `docs/images/capsule-connected.png`（480×156 物理像素）。
- 文字区域实测（物理像素）：状态 y=25..45、数字 y=63..99、说明+速度 y=116..133，
  右侧速度距窗口右边缘 28 px（≈18.7 DIP），无越界或重叠。

### 5. 未验证 / 已知边界

- 断开热点后的红色 + “上次会话”状态：本轮未切断网络，未做真机验证。
- 拖动、固定后禁拖、位置记忆的交互路径未自动化测试；位置夹取只验证了首次定位。
- 透明窗口的鼠标命中（透明区域是否穿透）未单独验证。
- 本会话视觉引擎不可用，未做肉眼观感确认；截图与坐标只能证明结构正确。

### 6. 下一步

等待用户查看胶囊实际效果。确认后再实现统计窗口（4C-2）；统计/设置菜单项在数据能力
接通前保持禁用，不伪造数据。
## 2026-11-13 阶段 4C-2：系统托盘、设置窗口与胶囊尺寸自定义

### 1. 任务范围

在 4C-1 胶囊基础上增加托盘入口、独立设置窗口、胶囊尺寸预设/自定义、固定与解除固定交互；
胶囊菜单移除“设置”，隐藏胶囊不停止引擎，统计窗口本轮不实现。

### 2. 新增 / 修改文件

- `HotspotMeter.App/Assets/HotspotMeter.ico`、`HotspotMeter-256.png`、`tools/IconGen/generate-icon.ps1`、
  `docs/images/icon-512.png`。
- `HotspotMeter.App/Models/CapsuleSize.cs`：尺寸预设、范围、宽高比与缩放系数；`TryCreate` 校验。
- `HotspotMeter.App/Services/AppIcons.cs`、`TrayIconService.cs`、`DarkTitleBar.cs`、`ISettingsHost.cs`。
- `HotspotMeter.App/ViewModels/SettingsViewModel.cs`、`RelayCommand.cs`。
- `HotspotMeter.App/Views/SettingsWindow.xaml(.cs)`。
- `WindowStateStore.cs` 扩展为可空字段的兼容 JSON（Left/Top/IsPinned/Width/Height/ShowCapsuleOnStartup）。
- `CapsuleWindow.xaml(.cs)`：尺寸绑定改为代码赋值；菜单改为四项；固定文案动态。
- `App.xaml.cs`：实现 `ISettingsHost`、托盘、设置单实例；`App.UiSelfTest.cs` 扩展自检。

### 3. 关键实现与修正

- 设置入口迁移到托盘；胶囊菜单不再含“设置”，用自检断言 `menuHasSettings=False`。
- `Window.Width/Height` 绑定会被 WPF 窗口布局覆盖（实测 320×744.7，尺寸切换无效）；
  改为在 `DataContextChanged` 与 `ApplySize` 中直接赋值，尺寸切换后窗口真实变化（280×92 / 400×130）。
- 位置越界夹取验证：模拟 (99999,99999) → (1378.7,954.7)，(-99999,-99999) → (8,8)（150% DPI 单屏）。
- 隐藏后引擎继续采样：隐藏 1.6 s 期间快照计数 6→7；显示后位置尺寸不变。
- 启动隐藏：`ShowCapsuleOnStartup=false` 时托盘存在、窗口不显示、引擎运行。
- 重启恢复：写入 400×130 后第二个进程 dump 显示 `actual=400x130`。

### 4. 验证

- `dotnet build HotspotMeter.sln`：0 警告 0 错误。
- `dotnet test`：348/348 通过。
- 自检产物：`artifacts/ui-selftest9.txt`、`capsule-*.png`、`settings-window.png`。
- 视觉证据：`docs/images/capsule-standard-4c2.png`（PrintWindow，480×156；中心 #1E8E3E，圆角区透明被渲染成黑）；
  像素统计确认图标白色笔画与三行文字带（y≈28–44 / 64–96 / 116–132 物理像素）。

### 5. 未验证 / 已知边界

- 鼠标真实拖拽、固定后禁拖：仅代码守卫（`IsPinned` 时直接返回），未做自动化 UI 操作。
- 多显示器与 100/125/200% DPI：当前机器只有 150% 单屏，未实测。
- 托盘图标在通知区域的实际观感与菜单渲染：只验证 `NotifyIcon.Visible=True` 与菜单项文本。
- 视觉引擎仍不可用，PNG 仅作结构证据，不等同人眼审美确认。
- 统计窗口、“流量统计”菜单项、用户选择目标热点仍未实现。

### 6. 下一步

等待用户验收本轮交互与视觉；确认后进入统计窗口（4C-3），并开始接入快照/会话持久化管线。

## 2026-11-13 阶段 4C-3：四档等比例缩放与右键菜单闪退修复

> 编号说明：本轮按用户要求处理"胶囊等比例缩小 + 右键闪退"，占用 4C-3；
> 统计窗口顺延，不影响 `docs/08` 的阶段编号。

### 1. 任务范围

保留现有胶囊样式与布局，只做整体等比例缩小；新增迷你档并把四档统一到 320 : 104；
修复胶囊右键菜单闪退并给出根因；不修改 MonitorEngine、数据库 Schema、统计逻辑与托盘交互。

### 2. 根因与修复

- 复现：对胶囊发送真实右键（`SendInput`）后进程退出；`logs/crash-20261113-*.log` 记录
  `InvalidOperationException: 用于类型"MenuItem"的样式不能应用于类型"Separator"`，
  堆栈 `ItemsControl.ApplyItemContainerStyle` ← `Panel.GenerateChildren` ← `Popup.CreateWindow`
  ← `ContextMenu.OnIsOpenChanged` ← `PopupControlService.ProcessMouseUp`。
- 根因：`ContextMenu.ItemContainerStyle` 把 `TargetType=MenuItem` 的样式套到了 `Separator` 容器上，
  菜单首次布局必抛异常；与数据、DPI、点击位置均无关。
- 修复：删除 `ItemContainerStyle`，改在 `ContextMenu.Resources` 里放隐式 `MenuItem` 样式；
  `Separator` 用默认样式。异常仍按原路径抛出，没有 try/catch 吞掉。
- 取证设施：`Services/CrashLog.cs` 记录三处未处理异常（Dispatcher/AppDomain/UnobservedTask），
  只写堆栈，不改变程序行为。

### 3. 尺寸与缩放

- `Models/CapsuleSize.cs`：`Mini 220×71.5`、`Small 280×91`、`Default 320×104`、`Large 400×130`；
  自定义 220–480 × 71.5–156；比例 320/104，容差 1.2%；`Scale = clamp(min(W/320, H/104), 0.65, 1.6)`；
  `TryCreate` 对越界与比例错误分别给中文原因（含建议高度/宽度）；`FromPersisted` 把旧尺寸按宽度映射到统一比例。
- `CapsuleViewModel.ApplyMetrics` 用同一系数派生全部视觉量，XAML 结构未改；没有单行化、没有隐藏元素。
- 设置窗口增加“迷你”预设按钮与选中态；提示改为“宽度 220–480 DIP · 高度 71.5–156 DIP · 宽高比固定为 320 : 104”。

### 4. 验证

- `dotnet build HotspotMeter.sln`：0 警告 0 错误；`dotnet test`：348/348。
- 自检 `artifacts/ui-selftest12/selftest.txt`：
  - 尺寸实测 220×71.5 / 280×91.3 / 320×104 / 400×130；自定义 380×123.3；字号 23.4 / 29.8 / 34 / 42.5 / 40.4。
  - 每档 `UiDump.CheckLayout` 均 `layout=ok texts=7`（全部文本在窗口内、两两不重叠）。
  - 右键菜单 `IsOpen` 连续三开三关无异常；菜单仍为四项且 `menuHasSettings=False`。
  - 校验文案：`宽度需在 220–480 DIP 之间。`、`高度需在 71.5–156 DIP 之间（宽度 320 对应 104）。`、
    `宽高比需保持 320 : 104（约 3.08 : 1）。当前 380 × 130，请把高度改为 123.5，或把宽度改为 400。`
  - 固定文案、隐藏后引擎继续（快照 7→9）、设置单实例、托盘入口、重启恢复 400×130 均保持通过。
- 截图（150% DPI 原生像素）：`docs/images/capsule-size-{mini,small,standard,large}.png` 与
  `docs/images/capsule-size-comparison.png`。

### 5. 未验证 / 已知边界

- 本会话 `SendInput` 合成的鼠标事件无法送达该窗口（拖拽与右键均无响应；UIA 确认窗口 enabled、非 offscreen），
  因此真实鼠标右键与拖拽需用户手动验收；菜单修复的证据是同一开菜单代码路径的三开三关与修复前崩溃日志对比。
- 迷你档状态文字 8.9、说明 7.6（150% DPI 下约 13/11 物理像素），是按比例缩放的必然结果；
  若实际观感偏小，需要重新定义"最小可读字号"，那会破坏严格等比例，需用户决定。
- 仅 150% 单屏；100/125/200% DPI 与多显示器未实测。
- 视觉引擎仍不可用，截图仅作结构证据。

### 6. 下一步

等待用户验收四档尺寸与右键菜单；确认后进入统计窗口，并开始接入快照/会话持久化管线。

---

## 2026-11-13 阶段 4C-4：任务栏自动检测与胶囊自适应

### 1. 目标

识别任务栏位置/厚度/所属显示器/DPI，按任务栏附近可用空间等比缩放胶囊并自动停靠；提供"自动适配任务栏"开关；
不嵌入任务栏、不改 Explorer、不动监控与数据层。

### 2. 实现

- `HotspotMeter.Core/Layout/TaskbarLayout.cs`：纯几何计算（输入显示器矩形/工作区/任务栏矩形/边缘/自动隐藏/DPI），
  输出 `CapsulePlacement`（DIP 位置、宽高、缩放系数、放置类型、边缘、可读说明）。
- `HotspotMeter.App/Services/TaskbarDetector.cs`：`SHAppBarMessage(ABM_GETTASKBARPOS/ABM_GETSTATE)` +
  `MonitorFromWindow`/`MonitorFromRect` + `GetMonitorInfo` + `GetDpiForMonitor`（失败退 `GetDpiForWindow`）；
  失败回退主屏工作区推断并记录 `Source`/`Error`。
- `CapsuleWindow`：`WndProc` 监听 `WM_SETTINGCHANGE(0x1A, 0x2F)`、`WM_DISPLAYCHANGE`、`WM_DPICHANGED`，
  600 ms 防抖后重算；固定状态暂停自动停靠；手动改尺寸/拖动会关闭自动适配；关闭自动适配恢复手动尺寸。
- `WindowStateStore`：新增可空 `AutoFitTaskbar`、`ManualLeft/Top/Width/Height`，旧 JSON 无这些字段时按默认处理。
- 设置窗口："悬浮胶囊行为"新增自动适配开关、当前检测结果、固定时暂停说明。

### 3. 修复的真实缺陷

- `GetMonitorInfo` 原 P/Invoke 使用 `out MonitorInfo` 且未设置 `cbSize`，导致恒返回 false、检测始终走兜底；
  改为 `new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }` + `ref info`。修复后首次拿到真实任务栏矩形。
- 右键菜单 `Separator` 样式崩溃已在此前修复（D-055），本轮连续开关与自动适配重排后均未再产生崩溃日志。

### 4. 验证

- `dotnet build HotspotMeter.sln`：0 警告 0 错误；`dotnet test`：**364/364 通过**（新增 12 个布局测试）。
- UI 自检 `artifacts/ui-selftest18/selftest.txt`：
  - 合成四边停靠 expected 与 actual 完全一致：底部 (1378.7, 912)、顶部 (1378.7, 50.7)、
    左侧 (50.7, 8)、右侧 (1336, 8)，均 320×104 且 `layout=ok texts=7`。
  - 200% DPI 合成期望 (1592, 1040)、副屏期望 (4152, 1288)，实际被本机真实屏幕夹取到 (1378.7, 954.7)；
    这是合成坐标超出 1707×1067 虚拟屏所致，属于夹取逻辑的预期行为，不是布局计算错误。
  - 真实检测：`底部任务栏 · 150% · 工作区 1707×1019 DIP`；胶囊停在 `1378.7, 906.7`，320×104，`layout=ok`。
  - 固定状态再触发检测位置不变（`pinnedStable=True`）；设置 VM 开关 → 启用并持久化 `AutoFitTaskbar=true`；
    `WM_SETTINGCHANGE` 重排前后位置一致、无异常；关闭持久化为 false。
  - 手动尺寸保护：400×130 → 开自动适配变 320×104 → 关闭恢复 400×130，`layout=ok`。
- 截图：`docs/images/capsule-autofit-bottom.png`（真实底部任务栏停靠）、`docs/images/settings-window-autofit.png`。

### 5. 未验证 / 已知边界

- 真实的多显示器混合 DPI、任务栏自动隐藏、任务栏被拖到顶部/左侧/右侧：仅通过合成输入与单元测试覆盖，
  没有在物理多屏环境实测；自动隐藏的真实任务栏状态无法在本机稳定复现。
- `SendInput` 合成鼠标事件在本会话送不到窗口，真实鼠标拖动/右键仍需人工验收。
- 100%、125%、200% DPI 未在真实缩放下跑过（布局计算按输入 DPI 处理并有单测）。
- 视觉引擎仍不可用；截图仅作结构证据。

## 2026-11-13 阶段 4C-5：速度竖排、深色滚动条/右键菜单、任务栏适配与遮挡修复

### 1. 需求

- 下载/上传速度从右下角横排改到会话流量数字右侧，上下两行竖排（下载在上），四档尺寸与自定义范围不变。
- 设置窗口滚动条、胶囊右键菜单统一深色，不允许白色轨道、白边或黑白分割。
- 排查"任务栏自动适配没有效果"与实际遮挡问题的根因，而不是重复加功能。

### 2. 诊断结论

- 自动适配并非未执行：4C-4 的输出约为 (1378.7, 906.7)，与手动默认右下角 (约 1363, 891) 几乎重合，
  且 150% 单屏下 `MaxScale=1` 不改变尺寸，所以视觉上像"没生效"。
- 遮挡来自两处：`ClampToScreen` 用虚拟屏幕，允许窗口压在任务栏上；置顶只在显示时用 `Topmost` 切换，
  任务栏被点击后可能盖住胶囊。
- 菜单白色区域来自 `ContextMenu` 自身的系统模板，不是 `MenuItem` 背景。
- 滚动条白色来自系统默认 `ScrollBar` 模板。
- 多屏检测总取任务栏所在显示器，副屏窗口会被算到主屏。

### 3. 修改

- `CapsuleWindow.xaml`：速度区改为 `SpeedBlock` 纵向 `StackPanel` + 两个三列 `Grid`，列宽用
  `SharedSizeGroup` 对齐；绑定新增 `SpeedBlockMargin` / `SpeedLabelMargin` / `SpeedRowSpacing`。
- `CapsuleViewModel`：按缩放系数派生上述间距；删除旧 `SpeedGapMargin`。
- `Theme.xaml`：新增深色 `ScrollBar` 模板与隐式样式；新增 `CapsuleContextMenuStyle`（替换整个模板）、
  `CapsuleMenuSeparatorStyle`；保留 `CapsuleMenuItemStyle`，仍不使用 `ItemContainerStyle`。
- `TaskbarLayout.cs`：横向任务栏靠左、纵向靠上停靠，自动隐藏时额外让出"间距 + 任务栏厚度"。
- `TaskbarDetector.cs`：以窗口所在显示器为准，任务栏矩形不属于该显示器时走推断。
- `CapsuleWindow.xaml.cs`：`ClampCurrentPosition` 改用工作区；新增 `EnsureTopmost()`（`SetWindowPos`，
  `SWP_NOACTIVATE`）；`ShowCapsule` 不再切换 `Topmost` 抢焦点；`ApplyPlacement` 不再二次夹取；
  拖动结束夹取并置顶；`DescribeTaskbar` 每次现场检测。
- `UiDump`：新增 `DescribeSpeedBlock`（两行竖排/在窗口内断言）、`DescribeScrollBar`（滚动条模板与宽度）、
  `SaveElementPng`（渲染 `ContextMenu` 这类弹出层）。

### 4. 验证

- `dotnet build HotspotMeter.sln`：0 警告 0 错误；`dotnet test`：**364/364 通过**。
- UI 自检 `artifacts/ui-selftest21/selftest.txt`：
  - 四档 + 自定义 380×123.5 全部 `layout=ok texts=9`；每档
    `speedBlock ... stacked=True inside=True verdict=ok`（下载行在上、上传行在下）。
  - 菜单连续开关 10 次无异常；`contextMenu.background=#FF1F1F23`、`border=#FF3A3A3F`、`template=True`。
  - 设置滚动条 `scrollable=277.3 viewport=431.3 width=10 visible=True thumbFill=#FF3A3A3F`。
  - 合成四边 expected 与 actual 完全一致：底部 (8, 908)、顶部 (8, 54.7)、左侧 (54.7, 8)、右侧 (1332, 8)；
    200% (8, 1036) 与副屏 (1928, 1284) 也不再被本机虚拟屏夹取（`ApplyPlacement` 不再二次夹取）。
  - 真实检测 `底部任务栏 · 150% · 工作区 1707×1019 DIP`，自动停靠 (8, 902.7)；
    `reAdapt` 前后一致无异常；手动尺寸保护 400×130 恢复成功。
- 截图（150% DPI 原生像素）：`docs/images/capsule-size-{mini,small,standard,large}.png`、
  `capsule-speed-layout.png`、`capsule-autofit-bottom.png`、`settings-window-dark-scrollbar.png`、
  `context-menu-dark.png`。

### 5. 未验证 / 已知边界

- 真实鼠标右键、拖动、固定后禁拖仍未自动化（`SendInput` 在本会话送不到该窗口）；菜单与拖动逻辑只由
  同一代码路径 + 自检间接验证，需人工验收。
- "点击其他程序后不再被任务栏遮挡"依赖 `SetWindowPos` 置顶，属于 Windows Shell 层级行为，需真实点击验证。
- 多显示器混合 DPI、自动隐藏任务栏仍只有合成输入与单元测试，没有物理环境实测。
- 100%/125%/200% 真实缩放未跑过；截图圆角区为黑色是 `PrintWindow` 不保留透明所致，不代表实际渲染。

## 2026-11-13 阶段 4C-6：速度块与流量数字的垂直中心对齐

### 1. 需求

- 右侧"下载 / 上传"竖向速度块的整体垂直中心，必须与左侧**流量数字控件**的中心在同一水平线上；
  基准不是"数字 + 已使用流量"的整块区域，也不是窗口中心。
- 不允许用固定 Margin 模拟对齐，需用 Grid / VerticalAlignment 实现；四档与自定义尺寸保持一致。

### 2. 修改

- `CapsuleWindow.xaml`：Row1 重构为两行两列 `Grid`：`TrafficValue`（新增名称）与 `SpeedBlock` 同处第 0 行，
  两者均 `VerticalAlignment="Center"`；说明文字独占第 1 行，不再参与速度块的对齐。
  速度块加 `UseLayoutRounding="False"`，消除 150% 缩放下 5 DIP 偏移被舍入为 5.33 DIP 的 0.33 DIP 误差。
- `UiDump.cs`：新增 `CheckSpeedAlignment(window)`，比较两个控件矩形的垂直中心，输出
  `trafficCenterY / speedCenterY / delta / 高度`，`|delta| ≤ 0.5 DIP` 记为通过。
- `App.UiSelfTest.cs`：四档 + 自定义尺寸各输出一行 `speedAlign`。

### 3. 验证

- 自检 `artifacts/ui-selftest22b/selftest.txt`：
  - 迷你 220×71.5：`delta=0`（37 / 37，速度块高 22.2）
  - 小型 280×91：`delta=0`（48 / 48）
  - 标准 320×104：`delta=0`（53.33 / 53.33）
  - 大型 400×130：`delta=0`（67.67 / 67.67）
  - 自定义 380×123.5：`delta=0`（64.33 / 64.33）
  - 每档 `layout=ok texts=9`，没有因对齐产生新的重叠或越界。
- 构建：`dotnet build HotspotMeter.sln` 0 警告 0 错误；单元测试 364/364 不受影响。
- 截图更新：`docs/images/capsule-speed-aligned.png`、`capsule-speed-layout.png`、
  `capsule-size-{mini,small,large,custom}.png`。

### 4. 未验证

- 视觉引擎仍不可用，未做人眼级像素比对；对齐结论来自控件矩形中心（结构证据）。
- 仅 150% 单屏真实运行过；100%/125%/200% 由 `UseLayoutRounding="False"` 与同一布局规则推算，
  未在真实缩放环境实测。

## 2026-11-13 阶段 4C-7：任务栏内部显示的可行性验证

### 1. 目的与边界

- 用户希望胶囊显示在 Windows 11 任务栏矩形内部；本阶段只做技术可行性验证，不改产品窗口/服务。
- 约束：不注入 Explorer、不改系统文件、不使用未验证的私有接口；不改现有 CapsuleWindow、四档尺寸、
  设置窗口、统计窗口与托盘；不 commit/push。

### 2. 调研

- 微软 `Taskbar Extensions` 文档（2021-01-07 更新）只列出 Jump List、缩略图工具栏、图标叠加、进度条与
  通知区域；DeskBand 属 Windows 7 时代机制，文档未声明 Windows 11 支持，且实现需要把 COM DLL 加载进
  Explorer，违反本项目"不注入"约束。
- 未找到微软公开的"第三方任意 UI 嵌入任务栏"接口；AppBar 是独立应用栏，不是任务栏内部区域。
- 环境限制：`web_search` 不可用（HTTP 402），调研以微软文档直连 + 本机实测为主。

### 3. 实验

- 新增独立项目 `tools/TaskbarOverlayProbe`（WinExe、net10.0-windows、WPF+WinForms、PerMonitorV2
  manifest，不加入解决方案、不引用产品项目）。
- 探针按 `SHAppBarMessage(ABM_GETTASKBARPOS)` 取任务栏矩形，把置顶透明窗口放进任务栏内部；每 2 s 记录
  overlay/taskbar 矩形、`WindowFromPoint` 命中、z 序排名、topmost 位、前台窗口，并用
  `BitBlt(CAPTUREBLT)` 截任务栏区域。
- 实测（2560×1600、150% DPI）：任务栏 `0,1528,2560,1600`（72 px）、工作区 `2560×1528`；探针
  `197×64`（131.3×42.7 DIP）放在 `(249,1532)`，`hitIsOverlay=True`，z 序在任务栏之上。
- 交互：`mouse_event` 点击/拖拽被探针接收，右键弹出 WPF `ContextMenu`，`Escape` 关闭；
  `SetForegroundWindow(Progman)` 后探针仍在任务栏之上。
- UIA 避让：只取任务栏中可见 `Button`（22 个）后，空隙为 237..781，胶囊放在 249，未遮挡开始按钮
  （781,1528,68,72）。
- Explorer 重启：杀旧进程后收到 `TaskbarCreated(0xC0AF)`，新任务栏 `hwnd=0x506DE`；重检测前探针被盖住
  （`hit=Shell_TrayWnd`），约 0.8 s 后重检测 + `SetWindowPos(HWND_TOPMOST)` 恢复置顶。

### 4. 结论

- 不存在可公开、稳定、无需注入的任务栏内部嵌入接口。
- 置顶透明窗口"视觉上在任务栏内部"可做到，但依赖 UIA 空隙和反复重断言，Explorer/任务栏更新后存在遮挡
  与错位风险，且可能压住系统按钮；不满足"稳定、不破坏系统"的产品底线。
- 产品继续使用独立置顶窗口 + 任务栏附近自动停靠；不把实验模式并入产品。见 `docs/09` D-068。

### 5. 证据与状态

- 证据：`artifacts/taskbar-probe-6/probe.log`、`docs/images/taskbar-overlay-probe.png`
  （2564×76，胶囊绿色 `#1E8E3E`、白字，右侧图标未被遮挡）。
- 产品代码未改：`dotnet build HotspotMeter.sln` 0 警告 0 错误；`dotnet test` 364/364。
- `git status`：仅新增 `tools/TaskbarOverlayProbe/` 与既有未提交项；未 commit/push。
- 未验证：任务栏自动隐藏、多显示器、混合 DPI、真实鼠标手动操作；这些只经过合成输入或单屏实测。

---

## 阶段 4C-8：自动适配只改尺寸 + 胶囊 UI 精简（2026-11-13）

### 需求

1. 「自动适配任务栏」保留开关与位置，开启后按当前显示器分辨率/DPI/任务栏厚度自动计算尺寸，位置仍由
   用户拖动；关闭恢复原手动尺寸；开关持久化。
2. 胶囊删除 Wi-Fi 图标、连接状态文字、"已使用流量"说明；流量数字放大，速度字号增大，下载在上、
   上传在下，速度块中心仍与流量数字中心对齐；四档、自定义、自动模式都不裁切/不重叠。
3. 扩展 UI 自检；更新文档；只提交本次任务改动，不 push。

### 实现

- 新增 `HotspotMeter.Core/Layout/TaskbarSizing.cs`：高度优先算法、三档密度、`FitsTaskbar` 与中文说明；
  新增 12 个 xUnit 用例（`TaskbarSizingCalculatorTests`），总测试数 364 → 376。
- `CapsuleViewModel` 删除状态/说明/图标属性，新增 `Density`/`ShowSpeedLabels`；正常排版字号改为
  46s/54s（流量/行高）与 14s（速度），紧凑 30/34/12，最简 16/19/8.5。
- `CapsuleWindow.xaml` 改为单行两列：左侧数字（`Viewbox` 只缩小不裁切），右侧速度两行；速度块与数字
  仍在同一 Grid 行且 `VerticalAlignment=Center`。
- `CapsuleWindow` 的自动适配改为只调 `ApplyAutoFitSize`（不改 Left/Top）；固定状态只锁位置；拖动后重算
  尺寸；`TaskbarCreated` 动态消息触发重算+重断言置顶；`ApplyAutoFitForTest` 供合成几何自检。
- 设置窗口说明文案改为"只调尺寸、位置由你拖动"，删除旧的"自动停靠/对侧角落"描述。

### 验证与发现的缺陷

- 第一次 UI 自检发现"固定状态尺寸保护"用例没有真正改变尺寸（沿用了上一合成用例的同一几何），
  无法证明固定时尺寸仍可调；改成 120 DIP 任务栏几何后 `sizeChanged=True`，位置仍不变。
- 构建：`dotnet build HotspotMeter.sln` 0 警告 0 错误（期间因运行中的 PID 33844 锁定 DLL，先停止进程）。
- 测试：`dotnet test` 376/376 通过。
- UI 自检：`artifacts/ui-selftest24/selftest.txt`（第二次运行）。
  - 四档 + 自定义全部 `layout=ok texts=7`；`simplified ok=True`（无 Wi-Fi Path、无状态/说明文字）。
  - 标准档：流量字号 46、速度 14；速度块 `202.3,31.4,99.7x41.2`，`stacked=True inside=True`，
    `delta=0.33`；四档/自定义偏差均 ≤0.33 DIP（容差 0.5）。
  - 最简长文本压力：`999.9 GB` 与 `999.9 MB/s` 在 220×71.5 下 `layout=ok`、速度块仍在窗口内。
  - 真实自动适配：`taskbar.detect=底部任务栏 · 150% · 工作区 1707×1019 DIP ·
    任务栏厚 48 DIP → 胶囊 44×135.4 DIP（最简排版）`；实际 `size=135.4x44`，位置保持 (240,240)。
  - 8 组合成几何 expected 与 actual 完全一致：bottom 135.4×44、top 123.1×40（Fits=false）、
    left/right 320×104、DPI200 135.4×44、secondMonitor 123.1×40、thin 123.1×40、tall 192.8×62.7；
    全部 `moved=False`。
  - 固定时自动适配：`pinnedStable=True sizeChanged=True size=320x104`。
  - 关闭自动适配恢复 320×104；开启前手动 400×130 → 开自动 135.4×44 → 关自动 400×130。
  - 设置 VM 开关：开启后 `size=135.4x44`、`persisted=True`；`WM_SETTINGCHANGE` 重排后位置不变、
    `layout=ok`；关闭 `persisted=False`。
  - 设置窗口 780×620 单实例、深色滚动条 `width=10 thumbFill=#FF3A3A3F`、三条中文校验文案正确；
    隐藏 1.6 s 快照 18→19、位置/尺寸稳定；托盘点击打开设置 `count=1`；
    重启预期 400×130 与 `ui-state.json` 一致。
- 截图：`artifacts/ui-selftest24/capsule-{default,mini,small,large,standard,custom,long-text,autofit}.png`、
  `settings-window.png`、`context-menu.png`。

### 文档

- `docs/02` §7.1 改写为精简结构；§7.5 改写为"只调尺寸、不自动停靠"；新增 §7.7 实测尺寸表。
- `docs/09` 新增 D-069～D-073。
- `PROJECT_CONTEXT` 状态改 4C-8、新增 4C-8 结果节与变更记录。

### Git

- 本次改动与此前 4C-1～4C-7 的未提交改动在同一批文件里（`CapsuleWindow.xaml.cs`、`CapsuleViewModel.cs`、
  `Theme.xaml`、`SettingsWindow.xaml`、`UiDump.cs`、`App.UiSelfTest.cs` 等都被多轮任务修改），
  无法在不误提交其他任务的前提下只暂存本次改动；按用户"无法安全拆分不得强行提交"的要求，**未 commit**。
- 未 push。工作区保留全部改动，等待用户决定提交策略。

### 未验证 / 限制

- 自动模式在 48 DIP 任务栏上为 135.4×44、Minimal 密度、速度字号 8.5 DIP、隐藏"下载/上传"标签；
  这是能放进任务栏的取舍，不等同于"完整信息在任何高度都清晰"。
- 低于 44 DIP 任务栏厚度时只保证不裁切（`FitsTaskbar=false`），不宣称可读。
- 真实 100%/125%/200% DPI、多显示器混合 DPI、纵向任务栏、自动隐藏任务栏没有实机环境，只有合成几何与
  单屏 150% 实测。
- 真实鼠标拖入任务栏、固定后禁拖、点击其他程序后的置顶效果仍需人工验收。

## 阶段 4C-9：自动适配拖动边界、自动定位与层级恢复（2026-11-13）

### 需求

1. 自动适配开启时取消工作区底部的拖动纠正，允许胶囊进入任务栏矩形；保留显示器边界保护；关闭后恢复原行为。
2. 自动适配开启时尽量保持胶囊在任务栏之上；只做事件驱动的一次性置顶，不用高频定时器抢 Z 序；监听 `TaskbarCreated`。
3. 首次开启自动适配时定位到当前显示器底部任务栏偏左（约 120 DIP 偏移，垂直居中），之后以用户拖动为准；
   重启恢复上次位置；再次开关可重新定位。
4. 新增自动化测试；更新文档；按用户授权统一提交 4C-1～4C-9 全部未提交改动（不 push）。

### 实现

- 新增 `Core/Layout/TaskbarAnchor.cs`：仅底部任务栏；X=显示器左边界+120 DIP（左右 8 DIP 夹取），
  Y=任务栏顶+(任务栏高−胶囊高)/2（上限显示器底−胶囊高，垂直不扣边距）；不支持的方向返回中文原因。
- 新增 9 个 `TaskbarAnchorCalculatorTests`，测试总数 376 → **385**。
- 新增 `Services/AppLog.cs`：`%LOCALAPPDATA%\HotspotMeter\logs\app.log`，1 MB 轮转，记录自动定位几何与
  `TaskbarCreated`；IO 异常静默，不接管异常。
- `CapsuleWindow`：`SetAutoFitTaskbar(true)` → 改尺寸 + `PositionAtAnchor` + 置顶；`ApplyAutoFitSize(positionAtAnchor:false)`
  供启动恢复、`TaskbarCreated`、DPI/分辨率变化与拖动结束使用；`ClampCurrentPosition` 自动模式用显示器边界、
  margin=0，普通模式工作区 +8 DIP；`RestorePosition` 按模式选择 margin；拖动结束统一走 `OnDragFinished`；
  新增 `DragEndForTest`、`NotifyTaskbarCreatedForTest`、`ApplyAutoFitForTest(input, positionAtAnchor)`。
- `SettingsViewModel.AutoFitHint` 改为"首次开启自动放到屏幕底部偏左，之后以拖动为准；不会自动寻找空隙"。

### 验证与缺陷

- 首轮 9 个 anchor 单测中 4 个失败：`maxTop` 仍扣 8 DIP、DPI200 期望误写 240（正确 120）；修正后 385/385。
- 构建期间 PID 34808 锁定 DLL（MSB3027），停止进程后 0 警告 0 错误。
- 第一次自检读到的 top=1018.7 来自 App 输出目录仍是旧 Core 副本（只跑了 `dotnet test`，未重建 App）；
  重建 App 后 `artifacts/ui-selftest26/selftest.txt` 为 `top=1020.7`。
- 自检关键行：`autofit.anchor left=120 top=1020.7 insideTaskbar=True`；
  `dragIntoToolbar left=300 top=1021.3 inside=True`；`normalClamp top=906.7 insideWorkArea=True`；
  `reEnable 120,1020.7`；`taskbarCreated stable=True`；四档/合成几何 `layout=ok`、`moved=False`；
  `pinnedStable=True sizeChanged=True`；`viaSettings pos=120,1020.7 persisted=True`；关闭恢复 400×130；
  `restart 400×130`。
- 日志：`monitor 0,0,2560,1600 / taskbar 0,1528,2560,1600 / dpi 1.5 → 120,1020.7`。

### 文档

- `docs/02` §7.5 改为 4C-8/4C-9 行为：自动定位坐标、拖动边界、定位触发条件与层级说明。
- `docs/09` 新增 D-074～D-077（定位算法、显示器边界、定位触发、事件驱动层级）。
- `PROJECT_CONTEXT` 状态改 4C-9、新增结果节、事实 18、变更记录。

### 启动恢复顺序修复（提交后复验）

- 提交后把 `ui-state.json` 设为 `AutoFitTaskbar=true`、`Left/Top=120/1020.7`、`135.4×44` 重启，
  UiDump 实测 `top=995.3`，胶囊落在任务栏上方，而非任务栏内。
- 根因：`CapsuleSize.FromPersisted` 对 135.4 宽回退到 Mini 220×71.5；`RestorePosition` 在该尺寸下
  按显示器底边夹取得到 `1066.7 - 71.5 = 995.3`，之后 `ApplyAutoFitSize` 才把高度缩到 44，位置已被改坏。
- 修复：`OnSourceInitialized` 先调 `ApplyAutoFitSizeOnly()`（只算尺寸并设 `Width/Height`，不夹取、不持久化），
  再 `RestorePosition()`；随后保留原来的 `ApplyAutoFitSize()` 做夹取/持久化/置顶。
- 复验：同一 state 重启，UiDump `left=120 top=1020.7 actual=135.3x44`；`app.log` 记录
  `恢复位置：auto=True vm=135.4x44 work=(0,0,1706.7,1018.7)`；BitBlt 截图胶囊绿色区域 abs y=1531..1596，
  任务栏为 1528..1600，确认完整位于任务栏内部（`artifacts/ui-selftest26/taskbar-anchor-fixed.png`）。

### Git 与未验证

- 按用户授权统一提交 4C-1～4C-9 的产品代码与文档；排除 `bin/obj`、`artifacts`、日志、`tools/TaskbarOverlayProbe` 的构建产物。
- 启动恢复顺序修复追加到同一提交（amend，未 push），保持"一次提交"的交付要求。
- 未验证：真实鼠标拖拽（`SendInput` 无法驱动该窗口，自检用 `DragEndForTest` 走同一路径）、
  任务栏激活/全屏应用后的遮挡、Explorer 重启的真实恢复、自动隐藏任务栏、多显示器混合 DPI。
- 独立窗口仍不具原生任务栏组件的层级契约；不宣称任务栏内嵌。

## 阶段 4C-10：说明标签移至流量数字上方

- 需求：把"已使用流量"从数字下方移到上方；数字仍为主体；速度区位置/对齐不变；四档与自定义尺寸均不得重叠/裁切。
- 现状核对：4C-8 精简版已经删除该标签（D-069～D-073），因此本次不是"移动"而是"按新要求重新加入并固定在上方"。
  这是对 4C-8 决定的有意覆盖，保留精简版的其余结构（无 Wi-Fi 图标/连接状态）。
- 实现：`CapsuleWindow.xaml` 外层 Grid 增加两行，标签在第 0 行、数字与速度块同在第 1 行；数字与标签同列且都
  `HorizontalAlignment=Center`，速度块仍 `VerticalAlignment=Center`。`CapsuleViewModel` 新增
  `CaptionFontSize=11×s`、`CaptionMargin=(0,0,0,2×s)`、`ShowCaption`。
- 自动适配：Normal 密度显示标签；Compact/Minimal 高度不足（56/44 DIP）隐藏标签，避免第三行文字挤压数字。
- 自检 `artifacts/ui-selftest27/selftest.txt`：四档 + 自定义 + 长文本全部 `layout=ok texts=8`；
  `simplified ok=True captionAbove=True captionCentered=True`；`speedAlign` delta 0/0.12/0.12/0/0.33，均 ≤0.5；
  自动适配 8 组几何仍 `layout=ok`、`moved=False`；定位/固定/设置/重启路径全部通过。
- 实机标准档 UiDump：标签 `85.3,16.7,54.7×14.7`，数字 `55.3,33.3,114.7×54`，间距 1.9 DIP，水平中心均 112.65；
  速度块中心 60.3 与数字中心一致。
- 文档：`docs/02` §1.3、§7.1、§7.6、§7.7 更新并新增 §7.8；`docs/09` 新增 D-079；本日志；`PROJECT_CONTEXT`。
- 未验证：视觉引擎仍不可用，标签灰度/间距只能靠坐标与像素统计判断；真实鼠标交互仍需人工验收。

## 阶段 4C-11：标签与数字统一字体、左对齐

- 需求：标签与数字同字体家族、风格协调；标签在数字上方；左边缘严格对齐；四档/自定义尺寸不重叠、不裁切。
- 实现：`CapsuleWindow.xaml` 左侧改为 `StackPanel`（标签 → 内层 Grid=数字+速度），标签与数字都
  `HorizontalAlignment=Left`；新增 `TrafficFontFamily` 资源供二者共用；标签加 `FontWeight=SemiBold`，
  字号仍 11×s、颜色 `TextSecondary`。数字字号 46×s、字重、颜色未改。
- 速度区未动：与数字同处内层 Grid 同一行，`VerticalAlignment=Center`，`Margin` 仍为 `SpeedBlockMargin`。
- `UiDump.CheckSimplifiedContent` 把 `captionCentered` 改为 `captionLeftAligned`，并新增 `captionFontMatch`
  （比较 `FontFamily.Source`）；断言左边缘差 ≤0.5 DIP。
- 自检 `artifacts/ui-selftest28/selftest.txt`：四档+自定义+长文本全部 `layout=ok texts=8`，
  `captionAbove=True captionLeftAligned=True captionFontMatch=True`；速度对齐 delta 0（自动 Minimal 0.33）；
  自动适配/定位/固定/设置/重启路径全部通过。
- 实机标准档 UiDump：标签 `x=18,y=16.7,54.7×14.7`，数字 `x=18,y=33.3,146.7×54`，左边缘差 0；间距 1.9 DIP；
  速度块中心 60.3 = 数字中心 60.3。
- 文档：`docs/02` §1.3、§7.1、§7.8；`docs/09` D-080（覆盖 D-079 对齐部分）；本日志；`PROJECT_CONTEXT`。
- 未验证：视觉引擎不可用，字体渲染观感需人工确认；不同 DPI 下字形 bearing 的亚像素差异未单独测量。

## 阶段 4C-12：说明标签降低不透明度

- 需求：只弱化"已使用流量"标签，保持灰色次级色调，Opacity 初值 0.65；字体/字号/字重/位置/左对齐不变；
  不得通过父容器透明度实现，数字与速度区外观不受影响。
- 实现：`CapsuleWindow.xaml` 的 `TrafficCaption` 加 `Opacity="0.65"`；数字、速度块、外层 `StackPanel` 的
  `Opacity` 均保持默认 1.0。未改绑定、布局、数据与交互。
- `UiDump.CheckSimplifiedContent` 新增 `captionOpacity`、`trafficOpacity`、`opacityIsolated`（标签 0.65、
  数字 ≥0.999、父容器 ≥0.999），并把 `opacityIsolated` 纳入 `ok`。
- 自检 `artifacts/ui-selftest29/selftest.txt`：四档 + 自定义 + 长文本全部 `layout=ok texts=8`、
  `captionOpacity=0.65 trafficOpacity=1.00 opacityIsolated=True`；速度对齐 delta 0（自动 Minimal 0.33）；
  自动适配/定位/固定/设置/重启路径全部通过。
- 对比度实测：绿底合成 `#739A82`、1.34:1；红底合成 `#AE7577`、1.55:1；实心灰原本为 1.63:1 / 2.25:1。
  已在 `docs/02` §7.9 与 D-081 记录取舍，未宣称满足 WCAG AA。
- 文档：`docs/02` §1.3/§1.5/§7.1/§7.8/§7.9；`docs/09` D-081；本日志；`PROJECT_CONTEXT`。
- 未验证：视觉引擎仍不可用，实际柔和程度需人工目检；65% 在 11 px 下的可辨性未做用户测试。

## 阶段 4C-13：标签与数字统一字体家族、字重

- 需求：标签与数字看起来属于同一套字体；标签不得比数字更粗；数字保持字号与主体地位；保留 65% 透明度、
  上方位置、左对齐与速度区。
- 根因核查：用 `[Windows.Media.Fonts]::SystemFontFamilies` + `FamilyTypefaces` 枚举本机字体，确认
  `Microsoft YaHei UI` 只有 Light/Normal/Bold（无 SemiBold），`Segoe UI Variable Display` 有 SemiBold
  但不含 CJK。原资源把 Segoe 放首位，中文逐字符回退雅黑、数字留在 Segoe；标签 SemiBold 被雅黑映射为 Bold，
  数字为真 SemiBold，导致"标签更粗、字形两套"。
- 修复：`TrafficFontFamily` 改为 `Microsoft YaHei UI, Microsoft YaHei`；`TrafficCaption` 与 `TrafficValue`
  的 `FontWeight` 都改为 `Bold`；其余（字号、Margin、Opacity、布局、速度块）未动。
- `UiDump.CheckSimplifiedContent` 新增 `captionWeightMatch`、`captionStyleMatch`、`fontCoversCjk`、
  `fontCoversDigits`；后者用 `Typeface.TryGetGlyphTypeface(...).CharacterToGlyphMap` 直接检查 `已/流/0/B`
  是否在同一字体文件，避免只看 `FontFamily.Source` 的假一致。
- 自检 `artifacts/ui-selftest30/selftest.txt`：四档 + 自定义 + 长文本全部 `layout=ok texts=8`、
  `captionFontMatch=True captionWeightMatch=True captionStyleMatch=True fontCoversCjk=True fontCoversDigits=True`；
  速度 delta ≤0.33；自动适配/定位/固定/设置/重启全部通过。
- 实机标准档：标签 `x=18,y=19.3,54.7×14`；数字 `x=18,y=35.3,183.3×48.7`（`18.26 KB`）；间距 2.0 DIP；
  左边缘差 0；速度块中心 59.67 = 数字中心。
- 代价与观察：数字改用雅黑后拉丁字形变宽（183.3 vs 146.7）、行高变矮（48.7 vs 54）；`layout=ok` 且
  `Viewbox` 只缩不裁。像素截图标签主色仍为 `#739B82`（65% 合成），说明透明度未被破坏。
- 文档：`docs/02` §1.6/§7.1/§7.8/新增 §7.10；`docs/09` D-082；本日志；`PROJECT_CONTEXT`。
- 未验证：雅黑 Bold 在 11 px + 65% 下的实际观感仍需人工目检；视觉引擎仍不可用。

## 阶段 4C-14：自动适配任务栏的流量数字放大 18%

- 需求：仅自动适配模式增大流量数字 15%–20%；关闭立即恢复；四档/自定义、DPI、定位、拖动不变。
- 实现：`CapsuleViewModel.AutoFitTrafficFontScale=1.18`；`ApplyDensityMetrics` 对字号与行高统一乘倍率，
  `ApplyNormalMetrics` 固定 1.0；最简密度水平内边距 7→5、速度区间距 6→4，把 6 DIP 让给数字。
- 隐性缺陷：`SetSize` 的提前返回只看"密度 Normal + 尺寸相同"，自动 Normal 与手动 320×104 无法区分，
  关闭自动适配时倍率残留（实测 `trafficFont=54.3 fontScale=1.18`）。提前返回条件补 `TrafficFontScale == 1.0`，
  复测恢复 `trafficFont=46 fontScale=1.00`。
- 诊断增强：`UiDump.DescribeTrafficValue` 输出声明字号、行高、渲染矩形与 `renderScale`，
  用来发现 `Viewbox` 因列宽不足抵消放大。
- 自检 `artifacts/ui-selftest34/selftest.txt`：最简档 `trafficFont=18.9 fontScale=1.18 renderScale=1.000`、
  数字矩形 72×22.7、`layout=ok texts=8`；40 DIP 最矮档数字 72×22.7 仍在窗口内；速度中心偏差 ≤0.33；
  关闭后 `trafficFont=46 fontScale=1.00`；四档/自定义字号与布局不变。
- 构建 0/0；`dotnet test` 385/385。
- 限制（如实记录）：紧凑/正常密度下数字列被胶囊宽度与速度区挤压，7–8 字符长数值仍受 `Viewbox` 列宽缩放
  （renderScale 0.56–0.83），声明字号变大但渲染尺寸接近原值；最简档（48 DIP 任务栏）已完整实现 18%。
- 文档：`docs/02` §7.5/§7.7、`docs/09` D-083、本日志、`PROJECT_CONTEXT`。

## 2026-11-14 阶段 5：任务栏层级、启动管理、历史持久化与统计窗口

### 5.1 任务栏层级
- 新增 `HotspotMeter.App/Services/TaskbarForegroundWatcher.cs`：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`，
  仅前台为 `Shell_TrayWnd`/`Shell_SecondaryTrayWnd`/`TrayNotifyWnd` 时触发；无定时器。
- `CapsuleWindow` 在自动适配开启时启动监听、关闭时解钩；回调只做 `EnsureTopmost()`（`SWP_NOACTIVATE`）。
- 自检：`topmost.watcher=True count=0->1 afterDisable=False`。

### 5.2 开机自启与任务栏显示
- 新增 `StartupRegistrationService`（HKCU Run，值名 `HotspotMeter`、带引号路径、所有权校验）；
  `ISettingsHost`/`App`/`SettingsViewModel`/`SettingsWindow` 接入，开关状态每次回读注册表。
- `ShowStatsInTaskbar`（`bool?`，缺失=true）持久化到 `ui-state.json`，即时作用于统计窗口。
- 自检：启用/重复启用/禁用/回读全部符合预期；任务栏开关 `False/False`、`True/True`。

### 5.3 历史持久化与逐程序数据
- 新增 `TrafficHistoryStore`：稳定 ProfileKey、10 s 检查点、`NightlySplitter`、OperationId 去重、
  会话结束结算、启动收尾遗留 Active 会话；`MonitorSessionService` 用 `_historyTail` 串行化写入。
- 修正 `BeginSessionAsync` 未累加每日连接次数的问题；修正 `DailyTrafficDelta.Create` 的日期参数类型；检查点改为“事务提交成功后再推进内存基线”，写库失败时下轮重算该段，避免静默丢字节。
- 新增 `AttributedUsageReader`（WinRT），按 SSID+adapter 选 profile；名称解析抽到
  `HotspotMeter.Core/Usage/AttributionDisplayName.cs` 并补 5 个单测。
- 实测：6 个会话、当日 `SessionCount=6`；会话汇总 `10,431,015 / 14,377,165 B` 与 `DailyTraffic` 一致；
  遗留 Active 会话被收尾，`active mappings=0`。

### 5.4 统计窗口
- 新增 `TrafficStatsService`（只读 SQLite）、`StatsViewModel`、`StatsWindow`（980×700）；
  托盘增加“流量统计”，`App.OpenStats()` 单实例。
- 功能：今日/最近 7 天/单日查询、下载/上传/总流量卡片、7 天堆叠柱、程序排行（名称/上下行/总量/占比）、
  刷新与错误/空/不可用状态；关闭窗口后监控继续。
- `UiDump` 增加真实文字墨迹测量与 ScrollViewer 裁剪过滤，修复误报；布局 `layout=ok texts=43`。

### 测试
- `dotnet build HotspotMeter.sln`：0 警告 0 错误。
- `dotnet test HotspotMeter.sln --no-build`：**390/390 通过，0 失败**。
- 运行时自检：`HOTSPOTMETER_STAGE5_TEST=artifacts/stage5-selftest3.txt`（单实例、置顶回调、注册表、任务栏显示、关闭后采样）。
- 统计窗口：`HOTSPOTMETER_UI_STATS=artifacts/stage5-stats3.txt` + `stage5-stats3.png`。

### 未完成/限制
- 置顶不能覆盖 UAC/安全桌面；Explorer 重启恢复需人工复验。
- 逐程序数据依赖 Windows 归属服务，存在延迟、历史缺行；未入库、未提取图标。
- 统计窗口周/月聚合、程序图标、长跑与睡眠唤醒验收未做。

## 2026-11-14 阶段 5.1 修复：开始菜单/任务栏遮挡与统计窗标题栏

- 根因（实机探针，不是猜测）：
  - 点击任务栏：旧 `TaskbarForegroundWatcher` 只匹配三个 Shell 类名；前台变成被激活的普通应用后事件不触发，
    任务栏已把自己抬到胶囊之上。
  - 开始菜单：`GetWindowBand` 显示胶囊 band=1、SearchHost/任务栏 band=6；`SetWindowPos(HWND_TOPMOST)` 返回成功
    但无法跨 band 覆盖。开始菜单关闭后 band 回落，一次重断言即可恢复。
- 修复：
  - `TaskbarForegroundWatcher` → `ShellForegroundWatcher`，不按类名过滤；`CapsuleWindow` 每次前台变化只安排一次
    250 ms 后的 `EnsureTopmost()`（`SWP_NOACTIVATE|NOMOVE|NOSIZE`），无轮询；关闭自动适配即停钩并停止计时器。
  - `StatsWindow` 在 `SourceInitialized` 接入现有 `DarkTitleBar.Apply`，保留系统原生标题栏与三个按钮行为。
- 测试：构建 0/0；`dotnet test` **390/390**；运行时自检 `artifacts/stage5-selftest-watcher.txt`；
  Z 序/像素证据 `artifacts/shell-zorder-watcher2.log`（关闭开始菜单后 green 0→11,309，点任务栏空白 11,267）。
- 限制：开始菜单/搜索展开期间受 band 6 限制，不能保证覆盖；UAC/锁屏不承诺；任务栏仅显示/隐藏无前台变化时不触发。
- 文档：D-090～D-092、docs/02 §2.6、docs/03、docs/05 §14、docs/07 5.1 验收、PROJECT_CONTEXT。

## 2026-11-14 阶段 6：发布前全面审查与加固

- 基线 `main@2498c4a`，工作区干净；4 个只读子代理审查 App 生命周期、持久化、监控、统计 UI。
- 修复 P0：退出时 `DisposeAsync` 在 UI 线程同步等待自身续体导致固定 3 秒冻结；改为线程池执行 + 3 秒上限，服务内部 `ConfigureAwait(false)`、先排空历史队列再关写入器。
- 修复统计串热点：`TrafficStatsService` 的每日/区间查询补 `ProfileKey` 过滤；无目标时返回完整日期轴且标记无记录。
- 修复统计并发：`StatsViewModel` 版本守卫 + 关闭时 `Cancel()` + WinRT 令牌透传；旧查询不再清空新列表。
- 修复逐程序配置匹配：`AttributedUsageReader.FindProfile` 要求 adapter GUID 与 SSID 都精确匹配，缺失返回不可用。
- 修复持久化：遗留会话映射改 `MappingStatus=3`；连接时长在 >30 s 间隔/未连接时记 0；检查点事务成功后才推进基线；自检确认 14 秒内 5 个检查点、正常退出无 Active 会话。
- 修复数据库健康：BUSY/LOCKED/IOERR 不隔离，CANTOPEN 仍尝试隔离；`DatabaseBootstrapper` 在不可隔离时抛异常保留原文件；`synchronous=FULL`，连接工厂测试更新。
- 修复配置与设置：`ui-state.json` 原子写 + 损坏备份；设置窗口按工作区夹取、激活时不覆盖正在编辑的宽高；自启失败原因显示在设置页。
- 修复托盘：菜单字体不再泄漏 GDI；新增 `TaskbarCreated` 消息窗口重新挂载图标（未人工重启 Explorer 验证）。
- 修复热点晚到：无候选时 5 秒重试；启动失败清理半初始化引擎，避免重试被挡。
- 修复胶囊菜单：右键“流量统计”启用，调用 `OpenStats` 单实例。
- 运行时事故与修复：`CloseAbandonedSessionsAsync` SQL 内误写 `//` 注释，SQLite 报 `near "/": syntax error`，历史库每次启动初始化失败；改为 `--` 后恢复（D-099）。
- 验证：Debug/Release 构建 0 警告 0 错误；`dotnet test` Debug/Release 均 **390/390**；`HOTSPOTMETER_STAGE5_TEST` 自检 startup/taskbar/topmost/stats/monitor 全通过，退出 7.4 s 无超时；统计窗口 `layout=ok texts=44`，截图 `artifacts/stage6-stats.png`（1470×1050），标题栏 `rgb(11,11,12)`。
- 新增决策 D-093～D-099；更新 PROJECT_CONTEXT、docs/02/03/04/05/07/08/09。
- 已知未解决：WLAN 句柄永久错误不自动重开；>3 s 采样间隔仍按睡眠丢字节；无睡前检查点；`Local\` 单实例与 RunId 清扫；Alt+F4 关闭胶囊后重开风险；Explorer 重启托盘恢复未人工验证；未做 24 h 长跑与多 DPI 实机。

## 2026-11-15 中英双语：首次启动选择 + 全局即时切换

- 调查现状：WPF App 无统一资源体系，设置/统计/胶囊/托盘文字大量硬编码；`ui-state.json` 已有原子写；启动顺序为单实例→读配置→建胶囊/托盘/监控。
- 新增 `HotspotMeter.Core/Localization/LanguageCatalog.cs`（zh-CN/en-US 两套约 120 键、`TryNormalize/Normalize/Get/Keys`）；`HotspotMeter.App/Localization/LocalizationService.cs`（单例、索引器、`SetLanguage`、`T/F`、`TrExtension`）。
- 首次启动：`App.OnStartup` 改为读配置语言→单实例→无有效语言时 `LanguageWindow.ShowDialog()`；取消直接退出且不写配置；选择后写 `ui-state.Language`、切换文化并重新加载；主窗口创建在语言确定之后。
- 新增 `LanguageWindow` 460×340 深色双语选择窗，两个卡片按钮、`AutomationProperties.Name`、`DarkTitleBar`，与设置/统计主题一致。
- 设置窗增至 780×700，新增语言卡；全部静态文字改 `{loc:Tr}`，状态/错误改 `T/F`，订阅 `LanguageChanged` 后 `Refresh(keepSizeInputs)`。
- 统计窗 XAML/VM 全部资源化，`RefreshLanguage()` 重跑 `LoadAsync()`；胶囊标签/速度/菜单/固定/上次会话前缀本地化；托盘五项就地 `ApplyLanguage()` + `Refresh()`。
- 运行期提示：`MonitorSessionService` 等待热点与采集警告、`StartupRegistrationService` 错误、`CapsuleSize` 校验、`AttributedUsageReader` 状态、`CapsuleWindow.DescribeTaskbar` 全部走资源键。
- 测试：新增 `LanguageCatalogTests` 18 例；`dotnet build` 0/0；`dotnet test` **408/408**。
- 运行时验证：首次启动 UIAutomation 选择 English 并写 `en-US`；取消不写；英文设置 `layout=ok texts=26`、统计 `layout=ok texts=44`、胶囊四档/自定义 `layout=ok`、菜单英文；运行中中英互切即时生效；英文 Stage5 自检全通过。证据 `artifacts/i18n-*`。
- 限制：视觉引擎不可用，未人工目检；托盘菜单未实际展开；自动适配最简档按设计隐藏说明标签。
- 文档：新增 `docs/13_Localization.md`；更新 docs/02 §2.7/§7.4、docs/04 §12、docs/07、docs/09 D-100～D-103、PROJECT_CONTEXT。

## 2026-11-15 阶段 7：发布工程（0.1.0 便携版）

- 基线：`main`，工作区干净，HEAD `6ce4f73`（另有合并提交 `2b0ceae`）；未 push。
- 项目调查：App `net10.0-windows10.0.26100.0`、Windows 10 1809+、x64；SDK 10.0.402；依赖仅 Microsoft.Data.Sqlite、Logging.Abstractions、xUnit 等；数据目录 `%LOCALAPPDATA%\HotspotMeter`。
- 发布模式：self-contained、win-x64、文件夹、非单文件、无 PDB；版本 0.1.0。`dotnet publish` 成功，目录 210,418,431 B（200.67 MiB，483 文件）。
- 发布验证：目录内直接运行 `HotspotMeter.exe`，胶囊 `left=120 top=1020.7 135.3×44 dpi1.5`；`HOTSPOTMETER_UI_STATS` 统计窗 `980×700` 真实数据；退出码 0；历史库 37 会话/2 天/1179 快照保留。
- 产物：`artifacts/release/HotspotMeter-win-x64.zip`（81,890,833 B，78.10 MiB）、`SHA256SUMS.txt`；ZIP 解压到 `verify-unzip` 后再次运行退出码 0。SHA-256 见 `docs/12_Release_Notes.md`。
- 安装包：环境无 Inno Setup/NSIS，未擅自安装；便携版手动安装/卸载说明已写，安装/升级/卸载程序标记未验证（D-105）。
- 隐私：README 统计截图含真实 SSID，已生成 `Traffic-Statistics-redacted.png` 并替换引用；原图保留本地、从版本库移除并加入 `.gitignore`；15 个代码/文档/测试文件的 SSID/BSSID/GUID 替换为 `TestHotspot`/`02:00:00:00:00:01`/`11111111-...`（D-107/D-108）。
- 安全扫描：`git grep` 凭证无命中、无 `C:\Users\`、无外联代码、四项目依赖 0 已知漏洞；Git 历史无凭证，但仍有真实网络标识与未脱敏截图，属高风险历史残留，需授权后清理。
- 文档：README 中英、`docs/11_Release_Checklist.md`、`docs/12_Release_Notes.md`、`docs/13_Localization.md`、PROJECT_CONTEXT、docs/02/04/05/08/09。
- 验证：Release 构建 0 警告 0 错误；`dotnet test -c Release --no-build` **408/408**；未做安装程序、代码签名、干净虚拟机、混合 DPI/多显示器、24 h 长稳。
- 结论：便携版可交付；整体“有条件进入发布”。新增决策 D-104～D-108。
