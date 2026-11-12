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
PRAGMA synchronous = NORMAL;    -- WAL 下的常用折中；进程崩溃安全，突然断电可能丢最后若干事务
PRAGMA foreign_keys = ON;       -- Microsoft.Data.Sqlite 默认关闭，必须显式打开
PRAGMA busy_timeout = 5000;
```

为什么不用 `synchronous=FULL`：热点监控对性能敏感、写入频繁，FULL 会显著增加磁盘同步；NORMAL 配合 WAL 已能保证"进程崩溃不损坏、已提交事务不丢"，代价是**突然断电可能丢失最后几个事务**——这与 06 中"不承诺绝对零丢失"的边界一致。

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