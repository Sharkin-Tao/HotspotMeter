# 05 网络监控

> 状态：**设计与待验证方案**。本文件中的 API 名称、结构体字段来自 Windows SDK 文档，但**尚未在本项目代码中编译或运行验证**；阶段 1 的原型必须逐项确认，确认结果回写到本文件与 09。

## 1. 目标

1. 准确识别"用户选定的那个手机热点"对应的**那一块** Wi-Fi 网卡。
2. 只读取该网卡的累计收发字节，算出会话累计与实时速度。
3. 任何异常（切换网络、网卡重建、睡眠、计数器回退）都不能造成错误累计。

## 2. 为什么必须结合 WLAN API 与 IP Helper

- **WLAN API** 能告诉我们"当前这个 Wi-Fi 接口连的是哪个 SSID/BSSID"，但拿不到字节计数。
- **IP Helper** 能给出每个接口的累计收发字节，但只有 GUID/LUID/描述，不知道 SSID。
- 二者通过**接口 GUID** 关联，才是可靠的"身份 → 计数"链路。仅凭 SSID 或网卡描述字符串匹配会在多网卡、虚拟网卡、同名 SSID（随身 WiFi 复制 SSID）时出错。

## 3. WLAN API

### 3.1 调用顺序

```csharp
uint negotiatedVersion;
WlanOpenHandle(2 /* WLAN_API_VERSION_2_0 */, IntPtr.Zero, out negotiatedVersion, out IntPtr clientHandle);

// 枚举无线接口
WlanEnumInterfaces(clientHandle, IntPtr.Zero, out IntPtr interfaceListPtr);
// WLAN_INTERFACE_INFO_LIST { dwNumberOfItems, dwIndex, WLAN_INTERFACE_INFO[] }
// WLAN_INTERFACE_INFO { GUID InterfaceGuid; WCHAR strInterfaceDescription[256]; WLAN_INTERFACE_STATE isState; }

// 查询当前连接
WlanQueryInterface(clientHandle, ref interfaceGuid, wlan_intf_opcode_current_connection,
                   IntPtr.Zero, out uint dataSize, out IntPtr dataPtr, out WlanOpcodeValueType opcodeValueType);
// dataPtr → WLAN_CONNECTION_ATTRIBUTES
// {
//   WLAN_INTERFACE_STATE isState;          // 1 = wlan_interface_state_connected
//   WLAN_CONNECTION_MODE wlanConnectionMode;
//   WCHAR strProfileName[256];
//   WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
//   WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
// }
// WLAN_ASSOCIATION_ATTRIBUTES
// {
//   DOT11_SSID dot11Ssid;                  // { ULONG uSSIDLength; UCHAR ucSSID[32]; }
//   DOT11_BSS_TYPE dot11BssType;
//   DOT11_MAC_ADDRESS dot11Bssid;          // UCHAR[6]
//   DOT11_PHY_TYPE dot11PhyType;
//   ULONG uDot11PhyIndex;
//   WLAN_SIGNAL_QUALITY wlanSignalQuality;
//   ULONG ulRxRate;
//   ULONG ulTxRate;
// }
WlanFreeMemory(dataPtr);
WlanFreeMemory(interfaceListPtr);
WlanCloseHandle(clientHandle, IntPtr.Zero);
```

要点：

- SSID 是**字节数组 + 长度**，不是以 `\0` 结尾的字符串，必须按 `uSSIDLength` 截取后按 UTF-8 解码（可能是中文/emoji）。
- BSSID 是 6 字节 MAC，格式化成 `AA-BB-CC-DD-EE-FF` 用于比对与显示。
- `isState` 表示接口是否已连接；`WLAN_CONNECTION_ATTRIBUTES.isState` 可能与接口状态不同，两者都要看。
- 查询不到连接时返回 `ERROR_NOT_FOUND (1168)` 或 `ERROR_INVALID_PARAMETER (87)`，属于正常情况，不要当异常抛出。

### 3.2 错误处理

| 返回码 | 含义 | 处理 |
| --- | --- | --- |
| 0 | 成功 | 继续 |
| 5 | 拒绝访问 | 记录日志，提示"WLAN 服务不可用"，进入未连接状态 |
| 6 | 无效句柄 | 关闭并重建客户端句柄 |
| 87 | 参数错误 | 视为该接口未连接，跳过 |
| 1062 (ERROR_SERVICE_NOT_ACTIVE) | WLAN 服务未启动 | 提示用户检查 WLAN 服务，进入未连接状态 |
| 1168 | 找不到元素 | 视为未连接 |

## 4. IP Helper API

### 4.1 读取计数

```csharp
GetIfTable2(out IntPtr tablePtr);       // MIB_IF_TABLE2 { ULONG NumEntries; MIB_IF_ROW2 Table[1]; }
// MIB_IF_ROW2 关键字段（完整结构以 Windows SDK 为准）：
//   NET_LUID InterfaceLuid;           // 64 位
//   NET_IFINDEX InterfaceIndex;       // ULONG
//   GUID InterfaceGuid;
//   WCHAR Alias[IF_MAX_STRING_SIZE+1];
//   WCHAR Description[IF_MAX_STRING_SIZE+1];
//   IFTYPE Type;                      // 71 = IF_TYPE_IEEE80211
//   IF_OPER_STATUS OperStatus;        // 1 = IfOperStatusUp
//   ULONG64 InOctets;                 // 累计接收字节（下载方向）
//   ULONG64 OutOctets;                // 累计发送字节（上传方向）
//   ULONG64 InErrors, OutErrors;
FreeMibTable(tablePtr);
```

- 返回的是**快照数组**，读取后立刻复制出需要的字段再释放，不要保存指针。
- `InOctets` 是接收（下载），`OutOctets` 是发送（上传）。
- 计数是 64 位累计值，接口禁用/重建或重启后归零。

### 4.2 接口身份与匹配

匹配策略（按优先级）：

1. `MIB_IF_ROW2.InterfaceGuid == WLAN_INTERFACE_INFO.InterfaceGuid`（直接匹配，最可靠）。
2. 若 GUID 不匹配（驱动异常），退化为 `Type == IF_TYPE_IEEE80211` 且 `Alias`/`Description` 与 WLAN 描述一致的唯一行；仍不唯一则放弃采集并记录警告。
3. 用 `ConvertInterfaceGuidToLuid` / `ConvertInterfaceLuidToGuid` 交叉校验 GUID 与 LUID 的一一对应关系。

必须记录到会话/日志的标识：`InterfaceGuid`、`InterfaceLuid`、`InterfaceIndex`。LUID 在本次开机内稳定，可用来检测"接口被重建"（GUID 不变但 LUID 变化，或 GUID 消失后重新出现）。

### 4.3 不重复计算的规则

- **只读目标 Wi-Fi 行**，绝不把 VPN、虚拟网卡、Loopback、以太网的行相加。
- 即使启用了 VPN，物理 Wi-Fi 上仍承载加密后的字节，读取 Wi-Fi 行即可覆盖实际链路流量；不能再叠加 VPN 虚拟适配器，否则会翻倍。
- 多网卡同时在线（Wi-Fi + 以太网）时，只认用户选定的那块。

## 5. 采样算法

```
状态：baselineIn, baselineOut, lastIn, lastOut, lastSampleMono, sessionDownload, sessionUpload

每次 tick（默认 1s）：
  counters = TryRead(targetInterfaceGuid)
  if counters == null:            # 接口消失/未连接
      结束会话（若有），重置状态，UI 显示未连接
      return
  if lastIn == null:              # 会话开始或重建
      baselineIn = lastIn = counters.In
      baselineOut = lastOut = counters.Out
      lastSampleMono = Stopwatch.GetTimestamp()
      return
  if counters.In < lastIn or counters.Out < lastOut:   # 计数器回退
      记录警告；重建基线（lastIn/lastOut = 当前值）；本次增量为 0
      return
  dIn  = counters.In  - lastIn
  dOut = counters.Out - lastOut
  elapsed = 单调时间差（秒）
  sessionDownload += dIn; sessionUpload += dOut
  downSpeed = dIn / elapsed; upSpeed = dOut / elapsed
  lastIn = counters.In; lastOut = counters.Out; lastSampleMono = now
```

- 速度必须用**单调时钟**（`Stopwatch`）；系统时间被 NTP 校准或用户修改时，`DateTime.Now` 差值会出现负数或跳变。
- 单次间隔超过阈值（例如 5 秒，说明睡眠/卡顿）时：增量仍计入会话（它确实发生了），但速度显示为 0，避免出现巨大的瞬时速度。
- 采样间隔 1 秒是默认值；用户可改为 2/5 秒（阶段 6 设置项），间隔越长 CPU 越低、速度越平滑但响应越慢。

## 6. 热点状态机

```
[未连接]
   │ 目标热点已连接 且 接口可映射
   ▼
[建立基线] ──► [会话中] ──(断开/接口消失/用户切换目标)──► [结束会话] ──► [未连接]
                 │
                 └─(计数器回退/接口重建/唤醒)──► [重建基线] ──► [会话中]
```

- **连接**判定：目标 GUID 的接口存在、WLAN 状态为 connected、SSID 与用户选择一致（BSSID 变化不结束会话，只更新显示）。
- **断开**判定：接口消失、WLAN 状态非 connected、或 SSID 变化。
- 是否把"短暂掉线后重连"合并为一个会话：**待用户确认**，当前按"每次连接新会话"实现（与总指令一致）。
- 用户在设置里更换目标热点时：先结束当前会话，再按新目标重建。

## 7. 边界与限制

| 情况 | 行为 | 用户可见结果 |
| --- | --- | --- |
| 连其他 Wi-Fi | 不建会话、不计数 | 红色"未连接" |
| 手机关热点/离开范围 | 结束会话、保存 | 红色，保留上次会话数字 |
| 网卡禁用再启用 | 检测到计数器归零 → 重建基线 | 数字不倒退、不重复累计 |
| 睡眠唤醒 | 唤醒后重建基线；休眠期间无采样 | 速度先显示 0，随后恢复 |
| 系统时间被改 | 速度用单调时钟不受影响；日归属可能受影响 | 已在 06 中规定用 LocalDate 快照拆分 |
| 程序启动时热点已连接 | 从启动时刻开始计，之前的流量不计 | 会话从启动起算（在文档与"关于"中说明） |
| 同时开 VPN | 只读 Wi-Fi 行 | 数值为链路真实字节，不翻倍 |
| 适配器驱动重置 | GUID 可能不变但计数归零 → 基线重建 | 短暂速度波动 |

## 8. 统计口径声明（必须写进界面）

统计的是**本机目标 Wi-Fi 网卡的收发字节**，包含链路层/协议开销，不包含：

- 手机自身产生的流量；
- 通过热点连接的其他设备；
- 运营商侧的重传、计费舍入、定向免流等规则。

因此**不能**宣称与运营商账单一致。

## 9. 阶段 1 验证结果（2026-11-12 实机执行，全部结论来自真实运行）

原型：`tools/ApiProbe/`（net10.0-windows 控制台，**不加入主解决方案**，不影响产品构建）。
环境：Windows 11 26200 x64；无线网卡 Intel Wi-Fi 6 AX203；目标热点 `OPPO Find X9`（5 GHz，866.7 Mbps，BSSID `1A:44:DD:0E:E5:B0`）；GUID `2f96e651-0898-4f18-ab35-41096d91e1b5`，ifIndex 5，LUID `0x0047008000000000`。

| 编号 | 验证项 | 结论 | 关键证据（`tools/ApiProbe/out/`） |
| --- | --- | --- | --- |
| P1-1 | WLAN 枚举与当前连接 | 通过 | `list.json`：SSID/BSSID/GUID/profile/信号/速率与系统一致，中文正常 |
| P1-2 | GUID ↔ IP Helper 行映射 | 通过 | 同一 GUID **恰好 1 行**，Alias `WLAN`，Type=71，尺寸自检 1352/604/532 全对 |
| P1-3 | 计数真实性 | 通过 | 下载 150 MB（157,286,400 B）：InOctets 增量 **161,933,750 B**（154.43 MiB），比值 **1.0295**；OutOctets +5,195,455 |
| P1-4 | 空闲计数稳定 | 通过 | 60 s 空载 in 187.16 KB / out 120.68 KB（约 3 KB/s 后台流量），0 次回退、0 缺帧 |
| P1-5 | 计数器回退 | 通过 | 禁用网卡瞬间计数归零、约 26 s 冻结、随后从小值重新增长；无负数、无虚假速率 |
| P1-6 | 睡眠唤醒 | 通过（第一次失败后修复复测） | 睡眠 **94,534 ms** 被识别为长暂停，唤醒首帧增量强制 0，之后恢复正常 |
| P1-7 | 系统时间跳变 | 通过 | 墙钟被调快 **+7,196,980 ms（约 2 小时）**，单调时钟仅 1,015 ms；速度完全不受影响，0 次回退 |
| P1-8 | 其他网卡隔离 | 通过（结论很重要） | 表 45 行，其中 **16 行 Type=71**；5 个 WLAN 过滤层行计数与目标行**完全相同**，按 Type 求和 = 目标值的 **6.00 倍** |
| P1-9 | 启动时热点已连接 | 通过 | 新进程首帧 `inDelta=0`，历史累计值只作基线，不计入会话 |
| P1-10 | 进程退出期间连续性 | 通过 | 无进程时外部下载 5 MiB，恢复 gap = **5,492,930 / 87,098 B**（比值 1.0476） |
| P1-11 | 跨重启计数器归零 | 通过 | 真机重启：TickCount64 5,744,156 → 256,093，开机时刻偏移 99.1 min，判定 `REBOOT_DETECTED`，**拒绝恢复** |

### 9.1 已验证事实（可直接作为阶段 2 的实现前提）

1. **只能按 WLAN 的 InterfaceGuid 精确取一行**。按 `Type=71`、别名或 MAC 求和都会重复计算：本机 5 个 WLAN 过滤层行（`WLAN-WFP Native MAC Layer…` 等）与物理行计数完全相同，会把流量放大 6 倍；Radmin VPN 也有 4 行同值。
2. **会话流量必须累加每帧 delta，不能显示网卡累计计数器**。实测计数器重置后，控制台"累计"归零，而真实会话增量仍保留重置前的 267,124 B。
3. **计数器可能在接口拆除瞬间被读到 0，随后又恢复原值**。P1-6 中睡眠开始时读到 `0`，唤醒后读回 24,191,795；若直接相减会产生约 23 MB 的幻影增量。因此"回退/缺失/唤醒后的首帧"必须强制增量 0，且正式实现应在恢复后**连续两帧确认**再继续累计。
4. **测速只能用 `Stopwatch`**；墙钟跳变（含时间同步 -5.2 s 的小幅回调）不得参与速度计算。墙钟只用于给样本打时间戳、决定自然日归属。
5. **睡眠期间 Stopwatch 继续走**：用墙钟时长做循环结束条件会在唤醒瞬间直接退出（第一次 P1-6 就是这样失败的）。必须用"有效采样时长"（排除长暂停区间）。
6. **接口切换时采样循环会丢 tick**（P1-5 丢 3 帧、P1-6b 丢 1 帧），但速度按真实 gap 计算，不会产生虚假速率；正式引擎应记录 `gapMs` 并在 UI 上以"实际间隔"平滑。
7. **重启可被可靠识别**：`TickCount64` 变小，或"当前 UTC − TickCount64"得到的开机时刻偏移超过阈值（原型用 ±10 分钟）。误判方向是"放弃恢复"，只会少算不会多算。
8. **崩溃 gap 的字节可精确计算，但发生日期不可还原**（P1-10）。阶段 3 采用"恢复会话 + `Recovered` 标记"方案，计入恢复当天。
9. `dot11PhyType=8` 与 `netsh` 报告的 "802.11ac" 不一致，**UI 不应把 PHY 类型当作权威信息展示**。
10. 结构体布局：`MIB_IF_ROW2`=1352、`WLAN_CONNECTION_ATTRIBUTES`=604、`WLAN_INTERFACE_INFO`=532（x64），运行时自检不符即退出码 2。

### 9.2 由验证得出的实现约束（阶段 2 已落实）

- 采样器内部状态机至少需要：`Running` / `InterfaceMissing` / `CounterReset` / `Suspended`（长暂停）/ `Rebooted`；每次进入非 `Running` 都重建基线。
- 会话累计 = Σ 有效 delta；任何"首帧、恢复帧、唤醒帧"贡献 0。
- 采样时间差采用单调时钟；样本同时记录 `timestampUtc`（墙钟）与 `gapMs`（单调）。
- 合理性上限默认只告警（`PlausibilityMode.Warn`），保留字节；`Drop` 必须显式开启；无链路速率时不设限（见 D-029）。
- 实现位置：Core 的 `TrafficAccumulator`、`BaselineResumeEvaluator`，Infrastructure 的 `MonitorEngine`；决策见 D-026～D-034。

## 10. 仍未验证 / 留待后续

1. 连接事件用 1 秒轮询还是 `WlanRegisterNotification` 回调：阶段 2 已用轮询（简单可靠，延迟 ≤1 秒）；回调留待阶段 6 评估。
2. 2.4G/5G 频段切换与 BSSID 变化是否需要提示。
3. 计数器是否包含 802.11 帧头：P1-3 比值 1.0295 说明除了 TCP/IP 头还有额外开销，但具体构成未拆分；口径说明里只承诺"接近系统数据使用量"。
4. 企业级/隐藏 SSID 热点、多个无线网卡同时存在时的表现（本机只有 1 个 WLAN 接口，未覆盖）。
5. 目标热点与同名已知 Wi-Fi 自动切换时的行为。
6. 超长睡眠（数小时）与休眠（S4）下的 `TickCount64` 语义差异，未单独测试。
## 13. 逐程序归属流量（阶段 5 验证结论）

- 数据源：`Windows.Networking.Connectivity.ConnectionProfile.GetAttributedNetworkUsageAsync`，
  配合 `GetNetworkUsageAsync(..., DataUsageGranularity.PerDay, states)` 取总量；App 的 TFM 因此提升到
  `net10.0-windows10.0.26100.0`（`SupportedOSPlatformVersion=10.0.17763.0`）。
- 权限：非打包 WPF 进程、无管理员、无 manifest/受限能力即可调用（探针实测 `packaged=False`）。
  枚举中没有 `Daily`，按日查询使用 `PerDay`。
- profile 选择：同一适配器下可能同时存在多个 SSID 配置，必须用“当前 SSID + 接口 GUID”精确选；
  只按 SSID 或只按 adapter 都会拿到 0 流量或错误配置。
- 返回内容：`AttributionName` 对桌面程序基本为空；`AttributionId` 可能是设备路径、包族名或空串。
  显示名从路径 basename/包族名派生，空串单列“未归属流量（系统/服务）”，不编造进程名。
- 准确性：逐应用合计通常**小于**系统总量，差额是系统/服务/未归属流量；禁止为对齐而分摊或把实时接口流量当逐程序数据。
  归属统计存在延迟与历史缺行，查询失败要显示“不可用”而不是 0。
- 图标：当前未提取（需要额外的 Shell/包信息查询），第一版只显示名称。
