# 04 数据模型

> 状态：**设计草案**。阶段 3 实现前需再评审并据此生成真正的建表脚本与迁移。
> 所有字节字段使用 SQLite `INTEGER`（64 位有符号）。
> 时间统一存 UTC 的 ISO-8601 字符串（`DateTimeOffset.UtcDateTime.ToString("o")`，即 `2026-11-12T04:31:07.1234567Z`），
> 本地日期另存 `yyyy-MM-dd` 文本，便于按自然日分组且不受时区/夏令时换算影响。

## 1. 设计目标

1. 任意时刻最多一个"进行中会话"（每个热点一条）。
2. 会话总量必须与快照增量之和一致（可校验不变量）。
3. 每日统计是**可重建的物化视图**，不是唯一真相。
4. 崩溃/断电后只丢失最后一个检查点之后的数据，且**绝不重复累计**。
5. 表结构可通过版本号迁移，升级不丢数据。

## 2. 实体关系

```
HotspotProfiles 1 ──< ConnectionSessions 1 ──< TrafficSnapshots
        │                                          │
        └──────────────< DailyTraffic >────────────┘
                         （按 LocalDate + HotspotId 聚合，可重算）

AppSettings（键值配置，独立）
SchemaInfo（schema_version，独立）
```

## 3. 表结构

### 3.1 SchemaInfo

```sql
CREATE TABLE SchemaInfo (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
-- 初始化：('schema_version', '1'), ('created_at_utc', '...')
```

### 3.2 HotspotProfiles

```sql
CREATE TABLE HotspotProfiles (
    HotspotId      INTEGER PRIMARY KEY AUTOINCREMENT,
    InterfaceGuid  TEXT    NOT NULL,              -- WLAN 接口 GUID，小写、带花括号统一格式
    Ssid           TEXT    NOT NULL,
    Bssid          TEXT    NOT NULL DEFAULT '',   -- 宁可空串也不用 NULL，避免 UNIQUE 失效
    Label          TEXT    NULL,                  -- 用户自定义名称（可选）
    IsActive       INTEGER NOT NULL DEFAULT 0,    -- 当前选中的监控目标（全局应只有 0/1 条为 1）
    CreatedAtUtc   TEXT    NOT NULL,
    LastSeenAtUtc  TEXT    NULL,
    UNIQUE (InterfaceGuid, Ssid, Bssid)
);

CREATE UNIQUE INDEX UX_HotspotProfiles_Active
    ON HotspotProfiles(IsActive) WHERE IsActive = 1;
```

设计说明：

- 接口 GUID 是主身份。同一手机热点在网卡更换/驱动重装后 GUID 会变，此时新建一条 Profile，历史数据通过"归档旧 Profile"保留，不强行合并。
- BSSID 可能随手机重启/频段切换变化，因此它参与去重但不作为唯一判断；真正判断"是不是目标热点"由用户选择 + SSID 匹配 + 接口 GUID 共同决定（见 05）。
- `UX_HotspotProfiles_Active` 用部分索引保证只有一个激活目标，避免两个热点同时计数。

### 3.3 ConnectionSessions

```sql
CREATE TABLE ConnectionSessions (
    SessionId         INTEGER PRIMARY KEY AUTOINCREMENT,
    HotspotId         INTEGER NOT NULL REFERENCES HotspotProfiles(HotspotId),
    StartTimeUtc      TEXT    NOT NULL,
    EndTimeUtc        TEXT    NULL,
    StartLocalDate    TEXT    NOT NULL,            -- yyyy-MM-dd，冗余字段，便于"按开始日统计连接次数"
    DownloadBytes     INTEGER NOT NULL DEFAULT 0 CHECK (DownloadBytes >= 0),
    UploadBytes       INTEGER NOT NULL DEFAULT 0 CHECK (UploadBytes >= 0),
    SessionStatus     INTEGER NOT NULL,            -- 0=Active 1=Completed 2=Interrupted 3=Abandoned
    LastCheckpointUtc TEXT    NULL,
    IsRecovered       INTEGER NOT NULL DEFAULT 0,  -- 1 表示由启动恢复/异常终止收尾
    AppVersion        TEXT    NULL,
    Note              TEXT    NULL
);

CREATE INDEX IX_Sessions_StartTime     ON ConnectionSessions(StartTimeUtc);
CREATE INDEX IX_Sessions_Hotspot_Start ON ConnectionSessions(HotspotId, StartTimeUtc);

-- 同一热点最多一个未结束会话
CREATE UNIQUE INDEX UX_Sessions_Open
    ON ConnectionSessions(HotspotId) WHERE EndTimeUtc IS NULL;
```

状态语义：

| 值 | 名称 | 含义 |
| --- | --- | --- |
| 0 | Active | 正在采样，EndTimeUtc 为 NULL |
| 1 | Completed | 正常断开或正常退出时结束 |
| 2 | Interrupted | 进程崩溃/断电后由启动流程收尾，EndTimeUtc = 最后检查点时间 |
| 3 | Abandoned | 无法可信恢复（例如数据损坏），不参与日统计 |

#### 3.3.1 恢复导致的会话边界（F-1）

恢复流程不允许让旧会话"静默消失"。`MonitorSessionEndedInfo.ResumeOutcome` 记录具体结局：

| 恢复结果 | 会话动作 | EndReason | ResumeOutcome | 结束时间 |
| --- | --- | --- | --- | --- |
| 证据 Continuous | 沿用原 SessionId，发 `SessionResumed` | —（不结束） | Continued | — |
| Unverified / Interrupted | 结束旧会话，同帧开新会话（累计 0） | RecoveryUnconfirmed | Continued | `gap.FromUtc` |
| Rebooted | 结束旧会话 | RecoveryRejected | Rebooted | 最后检查点 `Baseline.CapturedAtUtc` |
| NotOnTarget | 结束旧会话 | RecoveryRejected | NotOnTarget | 同上 |
| CounterReset | 结束旧会话 | RecoveryRejected | CounterReset | 同上 |
| InterfaceChanged | 结束旧会话 | RecoveryRejected | InterfaceChanged | 同上 |
| InterfaceMissing | 结束旧会话 | RecoveryRejected | InterfaceMissing | 同上 |

落库约定（阶段 3）：

- 被恢复流程结束的旧会话：`SessionStatus=2 (Interrupted)`、`IsRecovered=1`、`EndTimeUtc` 按上表。
- 未确认的恢复空档单独存 `RecoveryGaps`（阶段 3 新建），不得写入 `TrafficSnapshots`，也不得计入 `DailyTraffic`。
- `RecoveryGaps.RecoveryId` 必须有 `UNIQUE` 约束；`BaselineUtcTicks` 保留 100 ns 精度；
  同一 Id 的后续观察更新终值与增量，而不是忽略。
- 候选记录、旧会话收尾、新会话开始必须同一事务；未确认流量不得进入日统计。

### 3.4 TrafficSnapshots

```sql
CREATE TABLE TrafficSnapshots (
    SnapshotId        INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId         INTEGER NOT NULL REFERENCES ConnectionSessions(SessionId) ON DELETE CASCADE,
    PeriodStartUtc    TEXT    NOT NULL,
    PeriodEndUtc      TEXT    NOT NULL,
    DownloadDeltaBytes INTEGER NOT NULL CHECK (DownloadDeltaBytes >= 0),
    UploadDeltaBytes   INTEGER NOT NULL CHECK (UploadDeltaBytes >= 0),
    LocalDate          TEXT   NOT NULL,           -- 该增量归属的本地自然日
    IntervalMs         INTEGER NOT NULL CHECK (IntervalMs >= 0),
    Kind               INTEGER NOT NULL DEFAULT 0,-- 0=Periodic 1=Final 2=Recovered
    CreatedAtUtc       TEXT    NOT NULL
);

CREATE INDEX IX_Snapshots_Session_PeriodEnd ON TrafficSnapshots(SessionId, PeriodEndUtc);
CREATE INDEX IX_Snapshots_LocalDate         ON TrafficSnapshots(LocalDate);
```

设计说明：

- 快照记录的是**区间增量**，不是累计值。累计值在 `ConnectionSessions` 上，两者通过"会话总量 = 该会话快照增量之和"互相校验。
- 检查点默认 10 秒一条快照；断开、退出、睡眠前额外写一条 `Kind=Final`。
- 跨午夜的区间在**写快照时按时间比例拆成两条**：例如 23:59:58–00:00:08 的 10 秒区间，2 秒归前一天、8 秒归后一天，字节数按比例取整，余数给后一段以保证总和不变。采样间隔 1 秒时误差可忽略。这是**有意采用的近似**，写入文档，不宣称完全精确。
- 单条记录同时承担"恢复精度"和"日聚合来源"两个职责，避免再维护一条采样流水。

### 3.5 DailyTraffic

```sql
CREATE TABLE DailyTraffic (
    LocalDate        TEXT    NOT NULL,             -- yyyy-MM-dd
    HotspotId        INTEGER NOT NULL REFERENCES HotspotProfiles(HotspotId),
    DownloadBytes    INTEGER NOT NULL DEFAULT 0,
    UploadBytes      INTEGER NOT NULL DEFAULT 0,
    SessionCount     INTEGER NOT NULL DEFAULT 0,
    ConnectedSeconds INTEGER NOT NULL DEFAULT 0,
    UpdatedAtUtc     TEXT    NOT NULL,
    PRIMARY KEY (LocalDate, HotspotId)
);

CREATE INDEX IX_DailyTraffic_Date ON DailyTraffic(LocalDate);
```

- 由 `TrafficSnapshots`（字节）与 `ConnectionSessions`（次数、时长）聚合生成。
- `SessionCount` 按**会话开始日**归属：23:50 开始、次日 00:20 结束的会话，连接次数记在开始日。
- `ConnectedSeconds` 按区间实际归属拆分（与字节拆分规则一致）。
- 可随时 `DELETE` 后重算，程序启动时若检测到不一致会记录警告并提供重算（见 06）。
- "今日总流量"在查询时对同一天的所有 HotspotId 求和；由于产品只监控一个目标热点，通常只有一行。

### 3.6 AppSettings

```sql
CREATE TABLE AppSettings (
    Key          TEXT PRIMARY KEY,
    Value        TEXT NOT NULL,
    ValueType    TEXT NOT NULL,      -- string | int | long | bool | double | json
    UpdatedAtUtc TEXT NOT NULL
);
```

建议的键（阶段 6 定稿）：`target.hotspotId`、`sample.intervalMs`、`floating.isPinned`、`floating.x`、`floating.y`、`floating.screenId`、`stats.window.width/height`、`app.autoStart`、`log.level`、`data.retentionDays`。

### 3.7 TrafficOperations（v3，阶段 3B-5a）

> 本节与 `SchemaV3.cs` 同步；§3.1–§3.6 中仍残留早期草案的列名（如 `HotspotId`、`ConnectedSeconds`、
> `EndTimeUtc`），以代码中的 `SchemaV2.cs` / `SchemaV3.cs` 为准，后续随迁移逐节校正。

```sql
CREATE TABLE TrafficOperations (
    OperationRowId        INTEGER PRIMARY KEY AUTOINCREMENT,
    OperationId           TEXT    NOT NULL UNIQUE,     -- op:{session}:{kind}:{fromTicks}:{toTicks}
    OperationSequence     INTEGER NOT NULL,            -- 会话内严格递增，从 1 开始
    OperationType         INTEGER NOT NULL,            -- 1 检查点 / 2 结算 / 3 恢复合并
    DbSessionId           INTEGER NOT NULL,
    ProfileId             INTEGER NOT NULL,
    FromUtc               TEXT    NOT NULL,            -- 33 字符 UTC 文本
    ToUtc                 TEXT    NOT NULL,
    CapturedAtUtc         TEXT    NOT NULL,
    AppliedAtUtc          TEXT    NOT NULL,
    DownloadBytesAfter    INTEGER NOT NULL,            -- 操作完成后的会话累计
    UploadBytesAfter      INTEGER NOT NULL,
    ConnectedMilliseconds INTEGER NOT NULL,
    SegmentCount          INTEGER NOT NULL,            -- 1..400
    TimeZoneId            TEXT    NOT NULL,
    QualityFlags          INTEGER NOT NULL,
    RequestDigest         TEXT    NOT NULL,            -- 64 位小写十六进制
    PayloadDigest         TEXT    NOT NULL,
    UNIQUE (DbSessionId, OperationSequence),
    FOREIGN KEY (DbSessionId, ProfileId) REFERENCES ConnectionSessions (SessionId, ProfileId)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    CHECK (OperationSequence >= 1),
    CHECK (OperationType IN (1, 2, 3)),
    CHECK (SegmentCount BETWEEN 1 AND 400),
    CHECK (FromUtc <= ToUtc),
    CHECK (CapturedAtUtc >= ToUtc),
    CHECK (length(RequestDigest) = 64 AND RequestDigest NOT GLOB '*[^0-9a-f]*'),
    CHECK (length(PayloadDigest) = 64 AND PayloadDigest NOT GLOB '*[^0-9a-f]*')
);
```

时间列在真实 DDL 中还带长度、GLOB、年月日时范围、`strftime` 非空与往返一致的组合校验
（见 `SchemaV3.TimeCheck`）。`TrafficOperations` 只承载操作级幂等信息，分段仍写
`TrafficSnapshots`；该表在 3B-5a 只建表，尚未接入写入路径。

## 4. 关键查询（草案）

```sql
-- 今日汇总
SELECT COALESCE(SUM(DownloadBytes),0), COALESCE(SUM(UploadBytes),0),
       COALESCE(SUM(SessionCount),0), COALESCE(SUM(ConnectedSeconds),0)
FROM DailyTraffic WHERE LocalDate = $today;

-- 区间每日明细
SELECT LocalDate, SUM(DownloadBytes), SUM(UploadBytes)
FROM DailyTraffic WHERE LocalDate BETWEEN $from AND $to
GROUP BY LocalDate ORDER BY LocalDate DESC;

-- 启动时查找未完成会话
SELECT * FROM ConnectionSessions WHERE EndTimeUtc IS NULL;

-- 一致性校验：会话总量 vs 快照之和
SELECT s.SessionId, s.DownloadBytes,
       COALESCE(SUM(t.DownloadDeltaBytes),0) AS SnapDownload
FROM ConnectionSessions s
LEFT JOIN TrafficSnapshots t ON t.SessionId = s.SessionId
GROUP BY s.SessionId
HAVING s.DownloadBytes <> SnapDownload;
```

## 5. 连接与 PRAGMA 约定

每个连接打开后执行：

```sql
PRAGMA journal_mode = WAL;      -- 读写不互相阻塞；断电后由 WAL 恢复
PRAGMA synchronous = FULL;      -- 每个已提交事务落盘；阶段 6 由 NORMAL 调整为 FULL
PRAGMA foreign_keys = ON;       -- Microsoft.Data.Sqlite 默认关闭，必须显式打开
PRAGMA busy_timeout = 5000;
```

阶段 6 起使用 `synchronous=FULL`：热点监控每 10 秒一个检查点，写入频率不高，FULL 的额外 fsync 可以接受；它使"进程崩溃/突然断电最多丢最后一个未提交检查点"的口径更接近真实。代价是每次事务多一次磁盘同步。原先 `NORMAL` 在 WAL 下可能丢掉最后若干已提交事务，与 06 的完整性声明不完全一致，故调整（D-095）。

## 6. 聚合与重建规则

1. 正常路径：每次写快照时，同事务内 `INSERT OR REPLACE` 更新 `DailyTraffic` 对应行（增量累加），保证读写一致。
2. 修复路径：按日期范围删除 `DailyTraffic` 行，从 `TrafficSnapshots` 重算字节与时长，从 `ConnectionSessions` 重算连接次数。
3. 超出快照保留期时：只能用会话起止时间按重叠比例估算跨日归属（精度下降），并在修复报告中注明使用的来源。
4. 重建必须在事务内完成，并写入日志（开始/结束/影响行数）。

## 7. 保留策略（待用户确认）

| 数据 | 默认保留 | 理由 |
| --- | --- | --- |
| ConnectionSessions | 永久 | 体积极小，是长期统计的基础 |
| DailyTraffic | 永久 | 一年 365 行，可忽略 |
| TrafficSnapshots | 30 天（可配置 7–365） | 10 秒一条约 8640 行/天，长期保存会膨胀；保留期外的日统计由 DailyTraffic 承担 |

每日维护任务在启动时或跨日时执行一次裁剪。裁剪不删除未结束会话的快照。

## 8. 迁移

- `SchemaInfo.schema_version` 为整数；每次结构变更 +1。
- 迁移脚本按版本顺序执行，全部在单个事务内；失败则回滚并阻止程序启动（宁可不动数据）。
- 迁移前把数据库文件复制为 `hotspotmeter.db.bak-v{旧版本}-{时间戳}`，保留最近 3 份。
- 只做增量兼容迁移（加列、加索引、建新表）；删除/改名列必须先与用户确认并写决策记录。

## 9. 待确认

1. 快照保留期默认 30 天是否可接受。
2. 是否需要"按热点分别统计"的界面（当前数据模型支持，UI 只展示目标热点）。
3. `DailyTraffic` 的 `ConnectedSeconds` 是否要扣除"睡眠/唤醒"期间（当前：不归属任何会话，故自然不计入）。
4. 是否需要导出 CSV。
## 10. 阶段 5 落地说明（2026-11-14）

- Schema 仍为 **v3**，未新增表；总流量走 `TrafficSnapshots + DailyTraffic`，会话走 `ConnectionSessions + SessionRunMappings`。
- `HotspotProfiles.ProfileKey` 由“接口 GUID + SSID”派生（`TrafficHistoryStore.StableProfileKey`），不再每次启动生成随机值；
  同一热点跨重启落到同一 ProfileId。
- `DailyTraffic.SessionCount` 在 `BeginSessionAsync` 增加 1（跨午夜只记开始日），
  `ConnectedMilliseconds` 由检查点分段累加；两者都由增量 UPSERT 维护。
- 异常退出遗留的 `SessionStatus=0` 会在下次启动时被收尾为
  `SessionStatus=2 / EndReason=5(RecoveryRejected) / ResumeOutcome=4(InterfaceMissing)`，
  `EndedAtUtc=COALESCE(LastPersistedToUtc, StartedAtUtc)`，不补空档字节；对应映射改为 `MappingStatus=2`。
- 逐程序流量**不入库**：当前每次查询按选定日期实时调用 Windows 归属 API。原因见 D-088；
  因此没有“按应用历史表”，也不存在与总流量重复累计的问题。

## 11. 阶段 6 数据层加固（2026-11-14）

- **未改 Schema**：仍为 `user_version=3`，9 张表与索引均未变更；旧库直接兼容。
- `synchronous=FULL`（见第 5 节），`journal_mode=WAL`、`foreign_keys=ON`、`busy_timeout=5000` 不变。
- 启动清扫：`ConnectionSessions.SessionStatus=0` 的遗留会话按最后检查点收尾（`SessionStatus=2`、`EndReason=5`、`ResumeOutcome=4`、`EndedAtUtc=COALESCE(LastPersistedToUtc, StartedAtUtc)`），`SessionRunMappings.MappingStatus=3`（Abandoned），不补空档字节。
- `DailyTraffic.ConnectedMilliseconds`：快照间隔 >30 s 或目标未连接时该段记 0；睡眠时间不再被算成连接时长。字节增量不受影响。
- `DailyTraffic.SessionCount` 在会话开始时 +1（跨午夜只记开始日），避免结算时按分段重复。
- 统计读取按当前目标热点的 `ProfileKey` 过滤 `DailyTraffic`，不会把其他热点历史混入；逐程序数据仍不落库，按查询日实时读 Windows 归属 API。
- 数据库健康：BUSY/LOCKED(5/6)、IOERR(10) 属于暂时占用，不隔离；CANTOPEN(14) 仍尝试隔离，Move 失败会抛异常而不是清空；只有确定损坏（如 NOTADB/快速检查失败）才整体移入 `corrupt-{timestamp}`。

## 12. 用户配置与语言字段（2026-11-15）

`ui-state.json` 不是 SQLite 表，仍由 `WindowStateStore` 原子读写（临时文件 + `File.Replace`，损坏文件改名 `.bad-*`）。国际化只新增一个可空字段，不改变任何已有字段含义：

| 字段 | 类型 | 含义 | 缺省/兼容 |
| --- | --- | --- | --- |
| `Language` | `string?` | `"zh-CN"` / `"en-US"` | 旧文件无此字段 = 未选择，首次启动弹语言窗；非法值按未选择处理并回退 `zh-CN` |

- 读取顺序：`Load()` → `LanguageCatalog.TryNormalize` → 有效则 `LocalizationService.SetLanguage`；否则主流程在单实例检查后显示 `LanguageWindow`。
- 用户确认后才 `Update(s => s.Language = ...)`；取消/关闭语言窗不写入，也不重置其他字段。
- 语言只影响展示（文案、日期文化、数字分组），**不进入数据库**，不改历史流量、会话、日汇总或统计口径；切换语言不会触发任何写库操作。
- 国际化本身未改数据库；当时仍为 `user_version=3`。2026-11-17 的 Traffic Guard 第一阶段将 schema 升到 v4，见 §13。

## 13. Traffic Guard 数据层（v4，2026-11-17）

阶段 1 只做增量迁移，**不修改/重建** v1–v3 任何表；`SchemaMigrator.SupportedVersion=4`，迁移在单事务内执行，失败回滚并保留旧库。实际用户库已从 v3 升到 v4，原有会话、快照、日汇总仍在（实测 52 会话/5439 快照/4 天）。

### 13.1 GuardSettings（单行）

```sql
CREATE TABLE GuardSettings (
    Id                      INTEGER PRIMARY KEY NOT NULL CHECK (Id = 1),
    Enabled                 INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
    SamplingIntervalSeconds INTEGER NOT NULL CHECK (SamplingIntervalSeconds BETWEEN 10 AND 300),
    DataRetentionDays       INTEGER NOT NULL CHECK (DataRetentionDays BETWEEN 1 AND 3650),
    UpdatedAtUtc            TEXT    NOT NULL CHECK (33 字符 UTC 文本校验)
);
INSERT OR IGNORE ... (1, 1, 30, 30, now);
```

默认启用、30 秒采样、保留 30 天；本阶段没有设置 UI，读取值即可。

### 13.2 GuardAppRules

| 列 | 含义 |
| --- | --- |
| `AppKey` | 主键；`exe:{规范化完整路径}` / `pkg:{小写 PFN}` / `uncertain:{pid}:{startTicks}:{name}` / `opaque:{hash}` |
| `DisplayName` | 可读名称，仅展示用途，不参与身份判断 |
| `IdentityKind` | 1 桌面 EXE、2 UWP/MSIX、3 不确定 |
| `IdentityCertain` | 0/1；Kind=3 必须为 0（CHECK 强制） |
| `TrustState` | 预留给后续信任列表；本阶段默认 0，不做 UI |
| `FirstSeenAtUtc/LastSeenAtUtc` | 首次/最近写入样本时间；`LastSeen >= FirstSeen` |

同名程序靠完整路径区分；UWP 多进程靠 PFN 合并；不确定身份不会与确定身份合并。

### 13.3 GuardTrafficSamples

```sql
CREATE TABLE GuardTrafficSamples (
    SampleId      INTEGER PRIMARY KEY AUTOINCREMENT,
    AppKey        TEXT    NOT NULL REFERENCES GuardAppRules(AppKey),
    ProfileKey    TEXT    NOT NULL CHECK (32 位小写十六进制),
    FromUtc       TEXT    NOT NULL,
    ToUtc         TEXT    NOT NULL,
    CapturedAtUtc TEXT    NOT NULL,
    DownloadBytes INTEGER NOT NULL CHECK (>= 0),
    UploadBytes   INTEGER NOT NULL CHECK (>= 0),
    ActivityState INTEGER NOT NULL CHECK (IN (0,1,2)),  -- 0 前台 / 1 后台 / 2 空闲
    ProcessCount  INTEGER NOT NULL CHECK (>= 0),
    QualityFlags  INTEGER NOT NULL CHECK (BETWEEN 0 AND 63),
    CHECK (FromUtc <= ToUtc),
    CHECK (CapturedAtUtc >= ToUtc),
    CHECK (DownloadBytes > 0 OR UploadBytes > 0),
    UNIQUE (AppKey, ProfileKey, FromUtc, ToUtc)
);
```

- 只写**已确认的正增量**；0/缺失/失败不落行，避免“缺失=0”。
- `UNIQUE` + `INSERT ... ON CONFLICT DO NOTHING` 保证重复提交幂等。
- 未归属流量没有 `AppKey`，不允许写入本表，也不分摊。
- 索引：`(ProfileKey,ToUtc)` 与 `(AppKey,ToUtc)`，支持按热点/程序时间查询。
- `QualityFlags` 目前用 `IntervalGap=1` 表示采样间隔超过阈值；位定义在 Core 中，数据库只存整数。

### 13.4 GuardAlertHistory（预留）

只建表：`AlertId/AppKey/ProfileKey/AlertType/ThresholdBytes/ActualBytes/CreatedAtUtc/IsDismissed/Details`，`AppKey` 删除时 `SET NULL`。本阶段**不写任何提醒业务**，后续提醒/评分/阈值功能再接入。

### 13.5 事务与失败处理

`GuardStore.WriteAsync` 在单个事务内 upsert `GuardAppRules` + 插入样本；任一行失败整批回滚，调用方（`TrafficGuardService`）恢复内存基线，下一轮从旧基线重算，避免“库写失败但基线前移”造成静默丢字节。数据库连接仍复用 `SqliteConnectionFactory`（WAL、`foreign_keys=ON`、`synchronous=FULL`）与 `SqliteWriter` 单消费者。

## 14. Traffic Guard 提醒数据（v5，2026-11-17）

v5 不重建任何表，只在 v4 上 `ALTER TABLE ADD COLUMN` 并新增索引；迁移名 `traffic-guard-alerts-v5`，`SchemaMigrator.SupportedVersion=5`。

### 14.1 GuardSettings 新增列

| 列 | 类型/默认 | 约束/含义 |
| --- | --- | --- |
| `AlertsEnabled` | INTEGER NOT NULL DEFAULT 1 | 0/1，全局提醒开关 |
| `DefaultThresholdBytes` | INTEGER NOT NULL DEFAULT 104857600 | >0，默认 100 MB |
| `PeriodKind` | INTEGER NOT NULL DEFAULT 1 | 0=当前会话，1=自然日，2=滚动 24h |
| `MetricKind` | INTEGER NOT NULL DEFAULT 2 | 0=下载，1=上传，2=合计 |
| `BackgroundOnly` | INTEGER NOT NULL DEFAULT 1 | 0/1，只提醒后台程序 |
| `CooldownSeconds` | INTEGER NOT NULL DEFAULT 3600 | 0–86400 |
| `SecondaryEnabled` | INTEGER NOT NULL DEFAULT 1 | 0/1，二次提醒开关 |
| `SecondaryGrowthBytes` | INTEGER NOT NULL DEFAULT 52428800 | >0，二次提醒增长阈值 |

### 14.2 GuardAppRules 新增列

| 列 | 类型 | 含义 |
| --- | --- | --- |
| `RuleMode` | INTEGER NOT NULL DEFAULT 0 | 0=Inherit，1=Trusted，2=Disabled，3=Ignored；0–3 CHECK |
| `ThresholdOverrideBytes` | INTEGER NULL | NULL 或 >0；单程序阈值覆盖 |
| `IgnoreUntilUtc` | TEXT NULL | NULL 或 33 字符 UTC；Ignored 到期后恢复继承 |

### 14.3 GuardAlertHistory 新增列

| 列 | 类型 | 含义 |
| --- | --- | --- |
| `PeriodKind` | INTEGER | 0/1/2，冗余保存触发时周期 |
| `PeriodStartUtc` | TEXT | 33 字符 UTC，周期起点 |
| `DownloadBytes` | INTEGER | 触发时下行为累计 |
| `UploadBytes` | INTEGER | 触发时上行为累计 |
| `NotificationStatus` | INTEGER | 0=Pending，1=Sent，2=Suppressed，3=Failed |
| `NotificationError` | TEXT NULL | ≤512 字符，发送异常摘要 |
| `HandledAtUtc` | TEXT NULL | 用户处理时间，预留给后续 UI |

新增索引 `IX_GuardAlerts_App_Period(AppKey, PeriodKind, CreatedAtUtc)`，用于按程序/周期查最近提醒。

### 14.4 写入与查询

- 提醒插入与流量样本写入共用 `GuardStore` 的单消费者 `SqliteWriter`；`InsertAlertAsync` 返回自增 `AlertId`。
- 聚合查询：`ProfileKey` 小写匹配 + `ToUtc >= periodStart` 对 `DownloadBytes/UploadBytes` 求和，`ActivityState` 取该程序最新样本。
- 设置与规则保存为整行更新；规则不存在时插入占位 `IdentityKind=3/IdentityCertain=0`，避免外键失败。
- 所有时间列使用现有 33 字符 UTC 格式；`Today` 的边界在服务层换算，数据库不存本地日期。

### 14.5 迁移与兼容

- v4 的既有列、行、索引全部保留；新列有默认值，旧行升级后即为默认配置。
- 迁移失败整体回滚，`user_version` 不前进；重复执行幂等。
- 不改变 `TrafficSnapshots/DailyTraffic/ConnectionSessions` 等既有表。
