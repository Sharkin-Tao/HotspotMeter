# 06 数据恢复与完整性

> 状态：设计。阶段 3 实现，阶段 7 用故障注入验证。

## 1. 基本立场

- **不承诺绝对零丢失**：突然断电时，最后一个检查点之后的数据可能丢失（最多约一个检查点周期 + 未落盘的 WAL 事务）。
- **承诺不重复累计**：任何恢复路径都不得把新的计数器原始值当作增量加上去。
- **承诺可修复**：`DailyTraffic` 永远可以从 `TrafficSnapshots` + `ConnectionSessions` 重算。

## 2. 检查点策略

| 触发时机 | 动作 | 说明 |
| --- | --- | --- |
| 每 5–10 秒（默认 10 秒） | 写快照 + 更新会话累计 | 一个事务内完成 |
| 热点断开 | 写 Final 快照、结束会话、更新 DailyTraffic | 立即，不等待下一个周期 |
| 正常退出 | 写 Final 快照、结束会话 | 在 `Exit` 流程中同步等待完成 |
| 进入睡眠 | 尽量写检查点 | 通过 `PowerModeChanged(Suspend)` 触发，最多等待 1 秒 |
| 跨午夜 | 正常周期写入即可 | 快照按时间比例拆分到两天 |

每次写入使用单个事务：

```
BEGIN;
INSERT INTO TrafficSnapshots(...);
UPDATE ConnectionSessions SET DownloadBytes=?, UploadBytes=?, LastCheckpointUtc=? WHERE SessionId=?;
INSERT INTO DailyTraffic(...) ON CONFLICT(LocalDate,HotspotId) DO UPDATE SET ...;
COMMIT;
```

三者必须原子，否则会出现"会话总量与快照之和不一致"。

## 3. 启动恢复流程（阶段 2 已把"能不能恢复"落成代码）

阶段 2 的 `BaselineResumeEvaluator` + `MonitorEngine.ResumeAsync` 定义了判定顺序；阶段 3 只负责把结果与证据写进数据库。

```
1. (阶段 3) PRAGMA quick_check；失败则备份后尝试修复，绝不直接覆盖
2. 读取未结束会话与最后有效检查点（BaselineState：GUID/LUID/in/out/UTC/TickCount64/开机近似时刻/SSID）
3. 调 ResumeAsync，按以下顺序判定：
   a. TickCount64 变小                  → Rebooted（跨重启禁止相减）
   b. 当前未连目标 SSID / GUID 不一致   → NotOnTarget
   c. 计数行不存在                      → InterfaceMissing
   d. LUID 变化                         → InterfaceChanged
   e. 计数小于基线                      → CounterReset
   f. 开机近似时刻偏移 > 10 分钟        → Rebooted（保守，无法区分重启与改时间）
   g. 以上都不成立                      → Continued，gap = 当前计数 − 基线计数
4. Continued 只说明"读数看起来连续"，**不等于**期间全程都在目标热点上：
   - 期间可能断开热点、连别的 Wi-Fi、再连回同名热点，计数器同样单调。
   - 因此还要问 IConnectionContinuityProvider：
       Continuous             → 并入原会话，沿用 SessionId，发 SessionResumed
       Unverified / Interrupted → 切开会话（F-1）：
                                 旧会话在 gap.FromUtc（最后可信检查点）结束，
                                 EndReason=RecoveryUnconfirmed，ResumeOutcome=Continued；
                                 RecoveryGap 只作为独立候选保留，不进入任何会话累计；
                                 同一 MonitorStep 内以新 SessionId 开新会话（累计 0），发 SessionStarted；
                                 之后的流量只进入新会话，HasUnmergedRecovery 仍保留。
   - 阶段 2 的默认证据源恒为 Unverified（WLAN API 不提供关联起始时间），见 D-031/D-032/D-035。
5. 其余非 Continued 结局（Rebooted / NotOnTarget / CounterReset / InterfaceChanged / InterfaceMissing）
   同样必须结束旧会话：终点取最后检查点 `Baseline.CapturedAtUtc`，EndReason=RecoveryRejected，
   ResumeOutcome 记录具体结局；不允许旧会话静默消失。
6. 单次结束与单调 Id：
   - `_lastClosedSessionId` 水位保证同一 SessionId 最多一次 SessionEnded（正常结束与恢复结束共用）；
   - SessionId 严格递增，达到 Int64 上限时抛异常，绝不复用历史 Id；
   - RecoveryId = `{sessionId}:{guid:N}:{utcTicks}:{inOctets}:{outOctets}`，进程内只处理一次；
     阶段 3 再加数据库唯一约束。

### 3.1 阶段 3 必须遵守的数据库约束（F-1 结论）

1. `RecoveryId` 必须有数据库唯一约束，同名候选不能出现两行。
2. `BaselineUtcTicks` 保留原始 100 ns 精度（或 ISO 8601 七位小数），不能用秒级时间替代。
3. 同一 RecoveryId 的重复观察必须**更新**候选终值与 gap（例如 later 终点/更大增量），
   不能简单 `DO NOTHING` 丢弃后续增量。
4. "恢复候选记录、旧会话结束、新会话开始"必须同一事务提交，不能出现只有候选没有会话边界的中间态。
5. 未确认恢复流量（Unverified/Interrupted）不得进入正式每日统计，只能留在候选表等待人工确认或丢弃。
6. 写库失败不得丢候选：先插入候选再采纳会话边界，并提供重试/待持久化机制。

### 3.2 为什么重启后不会重复累计

系统网卡计数器在重启/网卡重置后归零，而数据库里保存的是**已经算好的增量**。新进程启动时：

1. 先读完数据库，不会把"当前计数器值"当作历史增量；
2. 新会话的 `baseline` 取启动后第一次采样值；
3. 即使计数器值比数据库里的小，也无影响，因为两者从不做减法。

此外，`UX_Sessions_Open` 唯一索引 + 启动时先收尾旧会话，保证不会出现两个活动会话同时累加。

### 3.3 日归属与日汇总契约（3B-4 已实现）

1. 日归属一律按**本地时区自然日**；输入区间是 UTC 半开区间 `[FromUtc, ToUtc)`。
2. 跨午夜的拆分由 `LocalDayResolver` + `NightlySplitter` 完成：先切出各本地日的 UTC 边界，
   再按字节/上传/连接毫秒分别用 BigInteger 累计取整分配，保证 `Σ 分段 = 输入`，不产生零长度片段。
   DST 23/25 小时日、午夜落在跳变缺口、重复午夜都由边界求解处理；精度为 1 tick。
3. `DailyTrafficRepository.ApplyAsync` 只做**增量 UPSERT**（累加字节与毫秒、`First=MIN`、`Last=MAX`），
   它**本身不幂等**、不自开事务、不重试；重复应用同一增量会翻倍。
   幂等与"快照插入成功才累加日汇总"由 3B-5/3B-7 的 `OperationId` + 同一事务边界负责（尚未实现）。
4. 墙钟调整（回拨/前跳）用 `EffectiveTimePolicy` 做事件级计数，容差 2 s；
   持续同一方向的抖动只计一次事件，恢复后再次调整才计第二次。会话测速仍只用单调时钟。

### 3.4 操作级幂等的基础（3B-5a 已建表，尚未接入写入）

1. Schema v3 新增 `TrafficOperations`：一次逻辑写入一行，带 `OperationId`（唯一）、
   会话内 `OperationSequence`（唯一）、`OperationType`（1 检查点 / 2 结算 / 3 恢复合并）、
   应用后的累计字节与连接毫秒、`SegmentCount`、`RequestDigest`、`PayloadDigest`、`AppliedAtUtc`。
2. 时间列统一为 33 字符 UTC 文本（`yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'`），
   数据库 CHECK 与 C# `UtcTimestamp` 双重校验；`24:00:00`、`29:00:00` 等 SQLite 宽松解析漏洞
   已由测试钉住。
3. 请求摘要与载荷摘要分离：请求摘要排除 `OperationId`/序号/采集时刻/分段，重试不改变请求身份；
   载荷摘要覆盖全部落库字段，供将来从数据库行重算比对。
4. **边界**：3B-5a 只建表与编码器，没有任何写入路径使用它们；
   "操作行 + 全部快照 + 日汇总 + 水位"同一事务、提交结果不确定时的重读核验，
   都要等 3B-5b 及之后实现，当前仍可能重复写入。

## 4. 进程崩溃（未捕获异常/强制结束）

- 已提交事务的数据保留；
- 未结束会话在下次启动时按第 3 节标记为 Interrupted；
- `DailyTraffic` 只包含已提交的增量，不会多算；
- 记录崩溃时间（最后一次检查点）与恢复动作到日志。

## 5. 突然断电

| 数据 | 风险 | 结果 |
| --- | --- | --- |
| 会话累计 | 最后 ≤10 秒的增量未提交 | 丢失该段，重启后从新基线开始 |
| 快照 | 同上 | 与丢失的会话累计一致（同事务） |
| DailyTraffic | 同上 | 与快照一致，不会多算 |
| 数据库文件 | WAL 可能处于半提交状态 | SQLite 下次打开自动回滚未提交事务；`quick_check` 应通过 |
| 会话状态 | EndTimeUtc 为空 | 启动时标记 Interrupted |

## 6. 睡眠/唤醒

- `SystemEvents.PowerModeChanged`：
  - `Suspend`：停止采样循环，写检查点（尽力而为），记录时间。
  - `Resume`：重新枚举 WLAN 接口与 IP Helper 行，**重建基线**，启动新的采样计时（`Stopwatch` 重新取点），不把休眠时长计入速度。
- 休眠期间不产生快照；唤醒后如果热点仍在，会话继续（不新建会话），但会话的总量只增加唤醒后的增量。
- 若休眠期间热点断开，唤醒后检测到未连接 → 正常结束会话。

## 7. 完整性校验与修复

### 7.1 启动校验（快速）

```sql
PRAGMA quick_check;                       -- 页级一致性
SELECT COUNT(*) FROM ConnectionSessions WHERE DownloadBytes < 0 OR UploadBytes < 0;  -- 应为 0
-- 会话总量 vs 快照之和（最近 7 天）
```

### 7.2 修复命令（内部，设置 → 高级）

1. `重算每日统计`：删除指定日期范围 `DailyTraffic`，从快照重算；无快照时用会话重叠比例估算并标注。
2. `修复会话总额`：`UPDATE ConnectionSessions SET DownloadBytes = (SELECT SUM(...))`，仅当差值 < 1% 时自动执行，否则要求用户确认。
3. `清理孤立快照`：删除 `SessionId` 不存在的快照（外键级联正常情况下不会出现）。

所有修复操作写日志（操作、范围、影响行数、前后值）。

## 8. 备份

- 每次迁移前复制数据库文件（含 `-wal`/`-shm`），命名 `hotspotmeter.db.bak-v{版本}-{yyyyMMddHHmmss}`，保留最近 3 份。
- 不使用 `VACUUM` 作为常规操作（耗时且需要额外空间）；仅在修复流程中按需执行。

## 9. 恢复场景测试矩阵（阶段 7 执行）

| 场景 | 操作 | 期望 |
| --- | --- | --- |
| 强制结束进程 | 热点连接中 → 任务管理器结束 → 重启 | 会话标记 Interrupted；丢失 ≤ 检查点周期；无重复 |
| 断电模拟 | 直接断电（或断开虚拟机电源） | 数据库可打开；`quick_check` 通过；无负数 |
| 睡眠 1 分钟 | 连接中睡眠再唤醒 | 速度不出现巨大峰值；会话继续；无重复累计 |
| 网卡禁用/启用 | 设备管理器禁用再启用 | 检测回退 → 重建基线；数字不倒退 |
| 修改系统时间 | 前后拨 1 天 | 速度正常；日归属按本地日期，不重复结算 |
| 重复启动 | 双击两次 | 只有一个实例；不会双倍计数 |
| 跨午夜 | 23:55 连接、00:10 断开 | 两天各得正确比例 |
| 数据库损坏 | 人为破坏文件尾部 | 启动提示并可选择恢复/重建，不静默清空 |

## 10. 已知边界（必须写进"关于"）

- 程序未运行时产生的流量无法统计。
- 断电最多丢失一个检查点周期（约 10 秒）的数据。
- 30 天前的原始快照若被裁剪，只能按会话比例估算跨日归属。
- 用户手动修改数据库文件可能破坏一致性，程序会尝试提示而不是静默修复。
## 11. 阶段 6 恢复路径调整（2026-11-14）

- 启动清扫遗留 `SessionStatus=0` 会话时，`SessionRunMappings.MappingStatus` 写 **3（Abandoned）**，不再写 2（正常关闭）；`EndedAtUtc` 取 `COALESCE(LastPersistedToUtc, StartedAtUtc)`，`EndReason=5`、`ResumeOutcome=4`，不把空档字节补入会话。
- 检查点先提交事务、再推进内存基线；写库失败时保留旧基线，下一周期重算，靠 `OperationId` 唯一约束防重复。
- `synchronous` 由 `NORMAL` 改为 `FULL`，使“断电最多丢最后一个未提交检查点”与实际落盘行为一致；仍不承诺绝对零丢失。
- 连接时长：快照间隔 >30 s 或目标未连接时该段记 0，避免把睡眠时间算进每日连接时长；字节增量仍完整保留（除非间隔 >3 s 被判定睡眠，见 05 §15）。
