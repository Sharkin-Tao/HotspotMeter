# 07 测试计划

## 1. 测试分层

| 层级 | 范围 | 工具 | 运行频率 |
| --- | --- | --- | --- |
| 单元测试 | 纯逻辑：格式化、日边界、拆分、状态机、恢复算法 | xUnit | 每次构建 |
| 集成测试 | SQLite 仓储、迁移、事务、重算 | xUnit + 临时文件库 | 每次构建 |
| 手工/半自动 | Win32/WLAN 真实行为、窗口交互、DPI、电源事件 | 人工 + 记录表 | 每阶段收尾 |
| 长稳测试 | 72 小时连续运行 | 人工 + 性能计数器 | 阶段 7 |

原则：**系统 API 一律通过接口隔离**。真实 API 的行为放到阶段 1 原型与手工测试，单元测试不依赖真机网络状态。

## 2. 单元测试清单（随阶段补充）

### 已完成（阶段 0）

- `ByteSizeFormatterTests`（12 个用例）：0/1/512/1023/1024/1536/KB/MB/GB/TB、1024 进制、负数抛异常。

### 已完成（阶段 2，含 F-1 收尾修正，共 58 个用例）

- `TrafficAccumulatorTests`（26）：正常采样、首帧 0、计数器回退 + 两帧确认、长暂停（睡眠）、检测器失败只暂停、SSID 变化结束会话、BSSID 变化只告警、接口缺失与恢复、合理性 Warn/Drop/无速率不设限、恢复合入（不改会话边界）、墙钟 +2 h 不影响速度；F-1：Unverified 结束旧会话并开新会话、Interrupted 同 Unverified、新会话累计 0、下一帧只进新会话、非 Continued 结束带具体 ResumeOutcome、同一 SessionId 只结束一次、拒绝后新 Id 不复用旧 Id。
- `BaselineResumeEvaluatorTests`（11）：Continued/CounterReset/InterfaceChanged/Rebooted/InterfaceMissing/NotOnTarget 六种结局；开机偏移 10:00 仍 Continued、10:01 判 Rebooted；Tick 回退优先于一切。
- `MonitorEngineTests`（9）：循环累计并在 Stop 报 EngineStopped、检测器异常发告警、恢复合并、同上下文幂等、并发只处理一次、Unverified 切会话并出候选、Rebooted 结束旧会话且重复调用不重复结束、未连目标结束旧会话、连着其他热点。

### 待实现（阶段 3+）

| 模块 | 用例 |
| --- | --- |
| 日边界 | 本地 00:00 前后归属；跨午夜区间按比例拆分且总和不丢字节；夏令时切换日；时区非 UTC |
| 恢复持久化 | 未结束会话收尾；多条未结束会话时的取舍；Abandoned 不进统计；RecoveryCandidate 落库与唯一索引 |
| 聚合 | 会话次数按开始日；跨日时长拆分；月/周边界 |
| 设置 | 类型转换、缺省值、损坏值回退 |

## 3. 集成测试清单

- 建库/迁移：空库 → 最新版本；旧版本 → 新版本；迁移失败回滚。
- 事务：快照 + 会话 + 日汇总原子提交；中途异常不留下半条记录。
- 唯一索引：同一热点不能有两个活动会话。
- 重算：删除 `DailyTraffic` 后重算结果与删除前一致。
- 裁剪：保留期外快照被删除，日统计不受影响。
- 并发：读写同时进行（WAL）不出现 `database is locked`（busy_timeout 生效）。

## 4. 手工测试矩阵

### 4.1 连接与识别

| 编号 | 步骤 | 期望 |
| --- | --- | --- |
| M-01 | 连接目标热点 | 10 秒内变绿并显示"已连接" |
| M-02 | 连接学校 Wi-Fi | 保持红色，不产生会话 |
| M-03 | 两个同名热点 | 用 GUID 区分，不会串数据 |
| M-04 | 关闭热点 | 5 秒内变红，保留上次会话数字并标注 |
| M-05 | 更换目标热点 | 旧会话结束，新会话开始 |

### 4.2 窗口与交互

| 编号 | 步骤 | 期望 |
| --- | --- | --- |
| M-10 | 拖动悬浮窗 | 跟随鼠标，无延迟卡顿 |
| M-11 | 固定后拖动 | 无法拖动 |
| M-12 | 重启程序 | 位置与固定状态保持 |
| M-13 | 150% DPI、双屏 | 不模糊；跨屏拖动后清晰；不越界 |
| M-14 | 任务栏上/下/左/右 | 自动定位合理，不遮挡开始按钮 |
| M-15 | 关闭统计窗口 | 悬浮窗继续刷新，后台继续采集 |
| M-16 | 右键菜单四项 | 行为符合 02 规范 |
| M-17 | 统计窗口重复打开 | 激活已有窗口，不出现多份 |

### 4.3 数据与异常

见 [06_Data_Recovery.md](06_Data_Recovery.md) 第 9 节矩阵，编号 R-01～R-08。

### 4.4 日期与时间

| 编号 | 步骤 | 期望 |
| --- | --- | --- |
| T-01 | 跨午夜连接 | 两天各得正确比例 |
| T-02 | 修改系统时间 | 速度不乱跳；日归属按本地日期 |
| T-03 | 历史查询区间 | 起止日期含端点；空区间显示"暂无记录" |

## 5. 验收标准（阶段级）

每个阶段结束时必须满足：

1. `dotnet build` 0 警告 0 错误；
2. 新增逻辑有对应测试，全部通过；
3. 手工场景表中本阶段相关项通过并记录结果；
4. 知识库已更新（日志、决策、必要时需求文档）；
5. 未引入与需求无关的功能。

## 6. 阶段 0 实测记录（2026-11-12）

| 项目 | 结果 |
| --- | --- |
| `dotnet build HotspotMeter.sln -c Debug` | 成功，0 警告 0 错误（约 7 秒） |
| `dotnet test HotspotMeter.sln -c Debug` | 13/13 通过（12 个格式化 + 1 个 SQLite 冒烟） |
| SQLite 原生库 | `runtimes\win-x64\native\e_sqlite3.dll` 存在，`SELECT sqlite_version()` 成功 |
| `dotnet run --project HotspotMeter.App` | 按设计启动即退出，退出码 0 |
| 首次测试失败 | `Format_Uses1024Base_NotDecimal` 期望值写错（1e9 字节应为 976.56 MB 而非 0.93 GB），已修正测试，非产品缺陷 |

## 7. 测试环境

- 开发机：Windows 11 专业版 10.0.26200，x64，.NET SDK 10.0.402。
- 需要额外准备：一台可开热点的 Android 手机；一个可用的第二显示器（或分辨率切换）用于 DPI/多屏测试；标准用户账户（验证不需要管理员权限）。
- 测试数据：临时 SQLite 文件库，测试结束删除；不在开发者真实数据目录上做破坏性测试。
## 阶段 5 运行时验收（2026-11-14）

```powershell
# 构建与单测
dotnet build HotspotMeter.sln
dotnet test HotspotMeter.sln --no-build

# 阶段 5 运行时自检（会临时写/删 HKCU Run，结束后恢复）
$env:HOTSPOTMETER_STAGE5_TEST="$PWD\artifacts\stage5-selftest2.txt"
.\HotspotMeter.App\bin\Debug\net10.0-windows10.0.26100.0\HotspotMeter.exe

# 统计窗口布局 + 截图（真实数据库只读查询）
$env:HOTSPOTMETER_UI_STATS="$PWD\artifacts\stage5-stats3.txt"
$env:HOTSPOTMETER_UI_STATS_PNG="$PWD\artifacts\stage5-stats3.png"
$env:HOTSPOTMETER_UI_DUMP_EXIT="1"
.\HotspotMeter.App\bin\Debug\net10.0-windows10.0.26100.0\HotspotMeter.exe
```

结果：
- `dotnet test`：390 通过 / 0 失败 / 0 跳过（新增 5 个 `AttributionDisplayName` 单测）。
- 自检输出：开机自启注册/重复注册/删除、任务栏显示即时生效、置顶 watcher `0->1`、
  统计窗口单实例、关闭后采样 `5->6`。
- 统计窗口：`layout=ok texts=43`。

人工验收（未自动化）：
1. 打开自动适配，点击任务栏/其他窗口，确认胶囊可见性；被遮挡时点任务栏应恢复。
2. 设置中开启开机自启，注销/登录验证；关闭后确认 `HKCU\...\Run\HotspotMeter` 被删除。
3. Explorer 重启（任务管理器重启资源管理器）后胶囊位置/层级恢复。
4. 断开/重连热点、跨午夜、睡眠唤醒后统计窗口日期与总量是否正确。
5. 长时间运行观察内存、句柄。

## 阶段 5.1 补充修复验收（2026-11-14，开始菜单/任务栏遮挡 + 统计窗标题栏）

自动化：
- `dotnet build HotspotMeter.sln`：0 警告 0 错误。
- `dotnet test HotspotMeter.sln --no-build`：**390 通过 / 0 失败 / 0 跳过**。
- `HOTSPOTMETER_STAGE5_TEST=artifacts/stage5-selftest-watcher.txt`：
  `topmost.watcher=True`、前台类名用 `Chrome_WidgetWin_1` 模拟仍触发一次（count `0->1`）、
  关闭自动适配后 `afterDisable=False`；开机自启与任务栏显示开关状态保持预期。
- Z 序探针 `tools/ShellZOrderProbe` → `artifacts/shell-zorder-watcher2.log`：
  | 场景 | 绿色像素 | 结论 |
  | --- | --- | --- |
  | 基线（任务栏可见） | 0 | 被任务栏盖住 |
  | 隐藏任务栏 | 11,311 | 证明遮挡层是任务栏 |
  | 按 Win 打开开始菜单 | 0 | band 1 vs 6，无法覆盖 |
  | Esc 关闭 1.5 s，未手工重断言 | 11,309 | 新监听自动恢复 |
  | 点击任务栏空白 | 11,267 | 新监听自动恢复 |
- 统计窗标题栏：`HOTSPOTMETER_UI_STATS` 实机截屏，标题栏主色 `rgb(11,11,12)`、边框 `rgb(38,38,42)`、
  亮色文字/按钮像素 877；`docs/images/stage5-stats-dark-titlebar.png`。

实机与模拟的边界：
- “按 Win/点击任务栏/关闭开始菜单”由探针用 `SendInput`/`keybd_event` 真实发送到系统，属于实机交互；
  但没有人工用鼠标逐项点击验收。
- 开始菜单展开期间的遮挡是 Windows 11 Shell band 行为，不是代码可修复项；只能保证关闭后恢复。
- 任务栏自动隐藏/恢复若没有前台变化，不会触发重断言；本轮未单独验收。
- Explorer 重启、注销/登录、UAC/锁屏覆盖未做本轮人工验收。

## 阶段 6 验收记录（2026-11-14）

### 自动化与构建

| 命令 | 结果 |
| --- | --- |
| `dotnet build HotspotMeter.sln -c Debug` | 0 警告 / 0 错误 |
| `dotnet test HotspotMeter.sln -c Debug` | 390 通过 / 0 失败 / 0 跳过 |
| `dotnet clean HotspotMeter.sln -c Release` | 成功（先关闭运行中的 App，否则 MSB3061 锁文件） |
| `dotnet build HotspotMeter.sln -c Release` | 0 警告 / 0 错误 |
| `dotnet test HotspotMeter.sln -c Release --no-build` | 390 通过 / 0 失败 / 0 跳过 |

### 运行时自检

- `HOTSPOTMETER_STAGE5_TEST=<file>`：开机自启读写/恢复、ShowInTaskbar 即时生效、前台 watcher 开关、统计窗口单实例、关闭统计窗后采样继续，全部通过；进程正常退出（约 7.4 s，含自检等待），`app.log` 无“退出时监控释放超时”。
- 运行 14 s 后查库：`TrafficSnapshots` 314→315、`ConnectionSessions` 与会话字节同步增长、`DailyTraffic` 同步；正常退出后 `SessionStatus=0` 的行为 0 条，说明结算落库。
- `HOTSPOTMETER_UI_STATS` + `HOTSPOTMETER_UI_STATS_PNG`：统计窗口 `layout=ok texts=44`，截图 1470×1050；标题栏像素 `rgb(11,11,12)`，深色生效。
- 运行时发现并修复：`CloseAbandonedSessionsAsync` 的 SQL 内误用 `//` 注释，导致历史库每次启动初始化失败；改为 `--` 后恢复。该缺陷无法由构建/单测发现，已记入 D-099。

### 仍需人工验收

1. 真实鼠标拖拽胶囊、固定/解除固定、四档尺寸与自定义。
2. 按 Win 打开/关闭开始菜单、点击任务栏图标/空白时胶囊可见性（展开期间 band 6 属已知限制）。
3. Explorer 重启后托盘图标是否恢复（代码已监听 `TaskbarCreated`，未人工重启验证）。
4. 睡眠/唤醒、合盖 30 分钟、跨午夜；长间隔 >3 s 的字节丢失仍属已知限制。
5. 多显示器混合 DPI、任务栏换边/自动隐藏。
6. Alt+F4 关闭胶囊后再从托盘“显示”是否崩溃（已知风险，未修）。
7. 24 小时以上常驻的内存/句柄/日志增长。

## 中英双语验收记录（2026-11-15）

### 自动化与构建

| 命令/用例 | 结果 |
| --- | --- |
| `dotnet build HotspotMeter.sln` | 0 警告 / 0 错误 |
| `dotnet test HotspotMeter.sln` | **408 通过** / 0 失败 / 0 跳过（含 18 个本地化用例） |
| `LanguageCatalogTests` | 规范化 zh/en、非法值、中英键集合一致、占位符一致、缺键回退、核心翻译 |

### 运行时验证（UIAutomation / UiDump / 像素）

- 首次启动（无 `Language`）：`LanguageWindow` 690×510 物理（460×340 DIP @150%），双语标题/副标题/两个卡片；`artifacts/i18n-picker.png`。
- 选择 English：4 s 内 `ui-state.json` 写入 `"Language":"en-US"`，进入主流程。
- 取消语言窗：进程退出，配置未新增 `Language`。
- 英文设置窗 `artifacts/i18n-settings-en2.txt`：`layout=ok texts=26`，无中文残留（语言选项“简体中文”按设计保留）。
- 英文统计窗 `artifacts/i18n-stats-en.txt`：`layout=ok texts=44`，图表/表头/排行/状态全英文。
- 英文胶囊 `artifacts/i18n-ui-en.txt`：四档 + 自定义 + 自动适配 `layout=ok`；`captionAbove/LeftAligned/FontMatch/WeightMatch/StyleMatch=True`、`captionOpacity=0.65`；菜单英文四项。
- 运行中切换：设置/统计/胶囊即时刷新，配置写回 `zh-CN`，再切 English 正常；未重启。
- 英文 Stage5 自检 `artifacts/i18n-stage5-en.txt`：startup/taskbar/topmost/stats/monitor 全通过。

### 仍需人工验收 / 未验证

1. 托盘菜单英文实际展开（图标在任务栏隐藏区，自动化未暴露；代码与 `MenuItemTexts` 已本地化）。
2. 英文界面人工肉眼终检（本会话视觉引擎失败，仅有布局/像素证据）。
3. 200% DPI、最小窗口尺寸、超长英文程序名组合。
4. 第三语言扩展不在范围。

## 阶段 7 发布验收记录（2026-11-15）

### 构建与测试

| 命令 | 结果 |
| --- | --- |
| `dotnet clean/build HotspotMeter.sln -c Release` | 0 警告 / 0 错误 |
| `dotnet test HotspotMeter.sln -c Release --no-build` | **408 通过 / 0 失败 / 0 跳过** |
| `dotnet publish HotspotMeter.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false` | 成功 |

### 发布产物验证

- 发布目录 `artifacts/release/HotspotMeter-win-x64/`：483 文件、210,418,431 B；无 `.pdb/.db/.log`；exe `FileVersion=0.1.0.0`。
- 直接运行发布目录 exe：退出码 0；胶囊 `left=120 top=1020.7 actual=135.3×44 dpi=1.5`；Minimal 档隐藏说明标签，数值/速度正常。
- `HOTSPOTMETER_UI_STATS`：统计窗口 980×700，真实历史与目标热点正常，深色标题栏。
- SQLite：37 会话、2 个自然日、1179 快照；启动后检查点增长，退出后无 Active 会话；升级/覆盖程序目录不丢 `%LOCALAPPDATA%` 数据。
- ZIP `artifacts/release/HotspotMeter-win-x64.zip`（81,890,833 B）解压到 `artifacts/release/verify-unzip` 后再次运行，退出码 0；SHA-256 与 `SHA256SUMS.txt` 一致。

### 安全/隐私

- 工作区凭证、`C:\Users\`、外联代码、依赖漏洞扫描均无命中；真实 SSID/BSSID/GUID 已替换为示例值；README 统计截图已脱敏。
- Git 历史仍含早期真实网络标识与未脱敏截图，未重写，标记为高风险历史残留。

### 未验证

1. 安装程序安装/升级/卸载（环境无 Inno Setup/NSIS，未制作安装包）。
2. 代码签名、SmartScreen 行为。
3. 干净虚拟机/全新 Windows 用户环境。
4. 多显示器混合 DPI、不同屏幕分辨率。
5. 24 小时以上长稳与内存/句柄增长。
6. 真实鼠标拖拽、托盘菜单展开、Explorer 重启、睡眠唤醒。

## Traffic Guard 第一阶段验收记录（2026-11-17）

### 自动化测试

| 命令 | 结果 |
| --- | --- |
| `dotnet build HotspotMeter.sln -c Debug` | 0 警告 / 0 错误 |
| `dotnet build HotspotMeter.sln -c Release` | 0 警告 / 0 错误 |
| `dotnet test HotspotMeter.sln -c Debug` | **454 通过 / 0 失败 / 0 跳过** |
| `dotnet test HotspotMeter.sln -c Release --no-build` | **454 通过 / 0 失败 / 0 跳过** |

新增/扩展用例覆盖：

| 类别 | 用例要点 |
| --- | --- |
| 身份 | 同名不同目录不合并；路径规范化（大小写/分隔符/相对路径）；PFN 大小写；Uncertain 不合并；空归属返回 null；设备路径映射；包族名；非 `.exe` 伪路径转 opaque |
| 多 PID | 同 AppKey 多进程聚合为一个样本，`ProcessCount=2`；UWP 多进程按 PFN 合并 |
| 状态 | 前台 PID→Foreground；无窗口有流量→Background；无前台/无可见窗口/无增量→Idle |
| 增量 | 首帧只建基线；第二帧只算正增量；同时间戳去重；任一方向回退重建；ProfileKey 变化清空；缺失程序删除基线；长间隔 `IntervalGap`；`RestoreState` 回滚；负累计拒绝；零增量不产样本 |
| 生命周期 | 暂停不采样、恢复重建基线；停止后可重启；重复 Start 不重复循环；读取异常不逃逸；写库失败回滚后下一轮重试不丢字节 |
| 网络/目标 | API 不可用不写样本、恢复后重建基线；目标丢失重置；网络切换后基线清空；空归属直接跳过 |
| 迁移 | v3→v4 保留 HotspotProfiles 与历史；重复迁移幂等；四表/索引/种子；CHECK 约束；样本拒绝 0 字节/重复/缺规则/非法 ProfileKey；Kind=3 必须 certain=0；故障注入迁移失败回滚且旧库完整 |
| 存储 | 规则+样本事务写入；重复样本幂等；规则名称/LastSeen 更新；设置往返；非法 ProfileKey 整批不写 |

### 运行时验收

- 真实热点连接时启动，Guard 日志 `Traffic Guard 已启动，采样周期 30 秒。`
- 连续采样 78 秒：8 条样本、0 条错误路径；`exe:c:\...`/`exe:e:\...`/`pkg:...` 正确，多进程样本 `ProcessCount=2/3`。
- 再次运行 76 秒：45 条样本；`opaque:… / IPv6 Control Message` 为 `IdentityKind=3/IdentityCertain=0`（2 条不确定样本）；出现 `Foreground` 样本（`ProcessCount=10`）。
- 实际库 `user_version=4`，原有 52 会话/5439 快照/4 天日汇总保留；`GuardTrafficSamples=52`、`GuardAppRules=13`；样本间隔 30 秒。
- 现有 `HOTSPOTMETER_STAGE5_TEST` 自检通过：startup/taskbar/topmost/stats/monitor、`ui-layout=ok texts=5`，退出码 0；胶囊/统计/托盘/设置无回归。

### 未验证/限制

1. 24 小时长稳、内存/句柄增长；测试用生命周期用例替代，未做 soak。
2. 真实睡眠/唤醒、网络切换、程序退出/重启的长时间人工验收。
3. 系统伪条目的业务解释（如 `IPv6 Control Message`）只按“不确定身份”处理，未做人工映射。
4. 逐应用样本尚未接入统计窗口展示；本阶段只验证写入正确。
5. 提醒/评分/阈值/信任 UI/结束进程/防火墙未实现，属后续阶段。

## Traffic Guard 第二阶段验收记录（2026-11-17）

### 自动化测试

- `dotnet build HotspotMeter.sln -c Debug`：0 警告 0 错误。
- `dotnet test`：**485/485**；Release 构建与 `dotnet test -c Release` 同样 485/485。
- 新增覆盖：全局/单程序阈值继承、三周期起点、三口径、仅后台、Trusted/Disabled/Ignored、同周期去重、冷却、二次增长、ProfileKey 切换、缺失/重复/时间异常、活动会话起点、通知成功/失败、失败仍保留记录、提醒异常不影响样本、v4→v5 迁移与幂等、GuardStore 聚合/规则/提醒。

### 运行时验收

- 用户数据库先备份；启动后 `user_version=5`，默认配置 100 MB/自然日/合计/仅后台/3600 秒/二次 50 MB，提醒表 0 条。
- 临时改为 1 MB、关闭仅后台、冷却 60 秒，连接真实热点运行 75 秒：产生 2 条 `GuardAlertHistory`（Claude、node），`NotificationStatus=1 (Sent)`、`NotificationError=NULL`；`PeriodStartUtc` 为本地零点对应的 UTC。
- 继续等待 45 秒无重复提醒，冷却与同周期去重生效。
- 停止应用后恢复备份并重新启动：数据库迁移为 v5，提醒记录归零，阈值恢复 100 MB；未删除用户历史会话/快照/日汇总。
- 现有 Stage5 自检仍通过（startup/taskbar/topmost/stats/monitor），Guard 与既有功能并行。

### 未验证/限制

- 勿扰模式、通知权限关闭、系统静默抑制无法可靠检测；只能记录 `Show` 异常或“已发送”。
- 没有守护设置页/规则 UI，规则目前通过数据库/服务 API 配置；用户无法在界面修改。
- 未做 24 小时长稳、真实睡眠/唤醒、真实网络切换专项人工验收。
- 二次提醒只在冷却结束且增长达到阈值时触发；没有“同一周期无限次提醒”的选项。

## Traffic Guard 第三阶段验收记录（2026-11-17）

### 自动化测试

- `dotnet build HotspotMeter.sln -c Debug` / `-c Release`：0 警告 0 错误。
- `dotnet test`：**515/515**，Debug 与 Release 一致；原有 485 项无删除、无跳过、无弱化。
- 新增覆盖：
  - v5→v6 迁移保留规则/提醒、默认值、`ExecutablePath`、两个索引、重复迁移幂等、故障注入回滚后仍为 v5。
  - 详情排序/按程序/按通知状态筛选、无规则回退、metric/source/activity/notification 映射、处理记录（OpenedFolder 不 dismiss，Trusted dismiss）、规则路径 `COALESCE` 不清空。
  - 进程安全策略：受保护名单、`%WINDIR%`、会话 0、uncertain/opaque、空路径、普通程序允许。
  - 进程管理：路径/PFN 匹配、`AlreadyExited`、启动时间不符→`IdentityChanged`、系统路径→`Blocked` 且当前进程不退出、复制 `ping.exe` 到临时目录并成功结束。

### UI 验收

- `HOTSPOTMETER_UI_GUARD` 打开单实例守护窗：960×640（本机 150% DPI），`layout=ok`。
- 空态 `texts=37`；注入合成提醒后 `texts=42`，历史列表、详情九宫格、规则行、进程区、操作按钮全部在窗口内；详情区超出部分由 `ScrollViewer` 裁剪，不与底部操作区重叠。
- 筛选项显示“全部程序/全部状态”，不再显示 ViewModel 类型名。
- 合成提醒用临时脚本写入，验证后已恢复数据库；真实库没有 `Demo App` 等测试记录。

### 未验证/限制

1. 未逐按钮做真实点击回归（信任/取消/忽略/打开文件夹/结束进程的 UI 事件），后端与仓储路径已由 515 项测试覆盖；二次确认取消仅由代码路径保证。
2. 未对真实第三方程序做“信任→再次超阈值→不再提醒”的端到端人工验收。
3. 路径失效、程序卸载时 UI 只显示明确状态，不做路径猜测。
4. 自动结束、防火墙、评分、守护设置页仍属后续阶段。

### 真实数据库事故（2026-11-17）

第三阶段收尾时发现真实用户库已于 19:59 被隔离（malformed），当前库为重建后的 v6 新库，仅 6 会话/5 快照/1 日汇总；原 52 会话/5439 快照历史可能不可恢复。隔离文件：`%LOCALAPPDATA%\HotspotMeter\Database\corrupt\corrupt-20261117-115909-084\hotspotmeter.db`。

因此：v6“旧数据保留”的结论只适用于测试库；不得把自动化迁移通过当成真实历史完整。恢复前应在隔离文件副本上做只读检查/恢复，不覆盖原文件。
