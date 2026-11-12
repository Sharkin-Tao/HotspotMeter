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

## 9. 阶段 1 原型验证计划（尚未执行）

用一个临时控制台程序（`dotnet new console`，不进入解决方案，验证后删除或移入 `tools/`）完成：

| 编号 | 验证项 | 方法 | 通过标准 |
| --- | --- | --- | --- |
| P1-1 | WLAN 枚举与当前连接 | 打印所有无线接口、SSID、BSSID、GUID | 与实际连接一致，中文 SSID 正确 |
| P1-2 | GUID ↔ IP Helper 行映射 | 打印 `MIB_IF_ROW2` 的 GUID/Alias/Type/LUID | 恰好一行匹配，Type=71 |
| P1-3 | 计数真实性 | 下载已知大小文件（如 100 MB），比较计数器增量 | 增量 ≥ 文件大小，偏差在合理协议开销内 |
| P1-4 | 空闲时计数稳定 | 空载 60 秒打印增量 | 增量接近 0（允许后台心跳） |
| P1-5 | 计数器回退 | 禁用/启用 Wi-Fi 适配器 | 检测到归零且不产生负数 |
| P1-6 | 睡眠唤醒 | 睡眠 1 分钟后唤醒 | 唤醒后增量合理，无巨大突增 |
| P1-7 | 时间测量 | 修改系统时间前后测速度 | 速度不受影响 |
| P1-8 | 其他网卡隔离 | 同时插网线/开 VPN | 目标行计数不受其他行影响 |

原型结果（通过/不通过、偏差数值、异常现象）写入本文件"验证结果"一节和 `docs/10_Development_Log.md`，并据此调整阶段 2 设计。

## 10. 待验证/待确认

1. 连接事件用 1 秒轮询还是 `WlanRegisterNotification` 回调（轮询简单可靠，延迟 ≤1 秒）。
2. 是否需要区分 2.4G/5G 频段并在 BSSID 变化时提示。
3. 计数器是否包含 802.11 帧头（影响与系统"数据使用量"的对比口径）。
4. 企业级/隐藏 SSID 热点的识别表现。
5. 目标热点被系统自动切换（手机热点与已知 Wi-Fi 同名）时的行为。