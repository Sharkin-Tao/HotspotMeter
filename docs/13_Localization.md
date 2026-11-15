# 11. 国际化（中英双语）

> 状态：已实现并通过构建、单元测试与运行时 UI 自检（2026-11-15）。
> 本文记录**实际实现**，不是设想；未验证项在文末单列。

## 1. 目标与范围

- 支持简体中文（`zh-CN`）与英文（`en-US`），全局同一语言状态。
- 首次启动且没有有效语言配置时，显示独立的模态语言选择窗口；选择后才进入主程序。
- 设置窗口、统计窗口、悬浮胶囊、托盘菜单、运行期提示/警告同步切换，不重启进程。
- 语言选择写入现有 `%LOCALAPPDATA%\HotspotMeter\ui-state.json`，不新增第二套配置系统。
- 不改变流量采集、数据库 Schema、统计口径、窗口尺寸/位置计算和既有交互。

## 2. 资源组织

语言资源不使用 XAML `ResourceDictionary`，而是集中在：

- `HotspotMeter.Core/Localization/LanguageCatalog.cs`

原因：

1. 资源需要被 App 的 ViewModel、Infrastructure/服务层（启动注册、监控警告、归属名称）共同使用；放在 Core 可避免 Core 依赖 WPF。
2. 两套字典在同一个文件内上下并排，新增键时必须同时补齐中英文，差异一眼可见。
3. 单元测试可直接对 `Keys` 做集合比较、对占位符做一致性校验，不依赖 WPF 资源加载。

结构：

```csharp
LanguageCatalog.ChineseSimplified // "zh-CN"
LanguageCatalog.English           // "en-US"
LanguageCatalog.TryNormalize(raw, out normalized)
LanguageCatalog.Normalize(raw)    // 非法回退 zh-CN
LanguageCatalog.Get(language, key)// 缺键返回 key 本身
LanguageCatalog.For(language)     // 只读字典
LanguageCatalog.Keys              // 排序后的键集合
```

约束：

- 键名使用 `区域_用途`，如 `Settings_LanguageSection`、`Stats_ColDownload`、`Capsule_MenuPin`。
- 两语言**键集合必须完全一致**；`LanguageCatalogTests` 会失败。
- `{0}` 等占位符必须在两语言中一致；测试会检查。
- 缺键不抛异常，直接返回键名，保证界面不会因漏翻译崩溃；开发期通过测试兜底。
- 真实程序名（Chrome、Microsoft Edge 等）不翻译。

## 3. 运行时服务

`HotspotMeter.App/Localization/LocalizationService.cs`：

- `LocalizationService.Current`：进程内唯一实例。
- `this[string key]`：WPF 绑定入口。
- `Language`、`Culture`：当前语言与文化（日期/数字格式随之变化）。
- `SetLanguage(string?)`：
  - 规范化；与当前相同返回 `false`（不重复刷新）。
  - 否则更新字段，依次触发 `PropertyChanged("Item[]")`、`PropertyChanged(string.Empty)` 与 `LanguageChanged`。
- 静态 `T(key)` / `F(key, args)`：代码内取词，支持 `string.Format` 与文化格式化。
- `TrExtension`：XAML 中的 `{loc:Tr Key}`，内部生成
  `Binding($"[{Key}]") { Source = LocalizationService.Current }`，因此静态文字也能随语言即时刷新。

各窗口的刷新方式：

| 界面 | 刷新机制 |
| --- | --- |
| 设置窗口 | `{loc:Tr}` 绑定即时刷新；`LanguageChanged` 触发 `SettingsViewModel.Refresh()` 重算状态/错误文案 |
| 统计窗口 | `{loc:Tr}` 绑定即时刷新；`RefreshLanguage()` 重跑 `LoadAsync()`，重建日期/柱状图/排行文案 |
| 悬浮胶囊 | `{loc:Tr}` 刷新标签/菜单/速度标签；`CapsuleViewModel.RefreshLanguage()` 重算 `PinMenuText` 与“上次会话”前缀 |
| 托盘菜单 | `ApplyLanguage()` 逐项更新 `Text`，再 `_menu.Refresh()`；不重建菜单、不丢事件 |
| 运行期提示 | 产生提示时调用 `LocalizationService.T/F`，不缓存已翻译字符串 |

## 4. 配置与启动顺序

`CapsuleWindowState.Language`（`string?`）写入现有 `ui-state.json`：

- 旧版本文件没有该字段 → `null`，视为“尚未选择语言”。
- 显式 `"zh-CN"` / `"en-US"` → 直接使用，不再弹选择窗口。
- 非法值 → `TryNormalize` 返回 false，按未选择处理（首次启动选择），运行期回退 `zh-CN`。
- 不修改、不重置旧配置的其他字段；读取损坏时沿用既有 `.bad-*` 备份 + 默认值逻辑。

`App.OnStartup` 的实际顺序（关键）：

1. `ShutdownMode = OnExplicitShutdown`。
2. `WindowStateStore.Load()`，读取 `Language`。
3. `TryNormalize`：有效则 `SetLanguage`；否则先临时设为 `zh-CN`，仅为本地化“单实例失败”等启动期消息。
4. `SingleInstanceGuard.TryAcquire()`；失败用当前语言弹框后 `Shutdown()`。
5. 若无有效语言：`new LanguageWindow().ShowDialog()`。
   - 取消/系统关闭：`Shutdown()`，**不写入** `Language`。
   - 选择后：`_stateStore.Update(s => s.Language = ...)`，`SetLanguage`，重新 `Load()`，写 `AppLog`。
6. 创建胶囊、托盘、监控服务。
7. 语言选择发生在任何主窗口显示之前，因此不会出现“先中文再闪成英文”。

## 5. 首次启动语言选择窗口

`HotspotMeter.App/Views/LanguageWindow.xaml(.cs)`：

- 460×340 DIP，`WindowStyle=None` + 透明背景 + 圆角 12，深色 `BgApp`，与设置/统计窗口同一套 Theme 资源。
- 双语标题 `Welcome to HotspotMeter` / `欢迎使用 HotspotMeter`；双语副标题与底部提示。
- 两个 188×112 卡片按钮：`简体中文 / Simplified Chinese`、`English / English (US)`；悬停 `BgHover` + 绿色边框。
- 两个按钮均设置 `AutomationProperties.Name`，便于 UI Automation 与读屏。
- 点击即 `DialogResult = true`，`SelectedLanguage` 为 `zh-CN` / `en-US`。
- `SourceInitialized` 应用 `DarkTitleBar`；窗口居中、不可调整大小。
- 不使用默认 MessageBox，不增加注册/登录等额外引导。

## 6. 已覆盖的界面

- **设置窗口**：标题、语言卡、四档尺寸、宽/高、应用、固定位置、自动适配、恢复默认位置、启动显示胶囊、开机自启、出现在任务栏、状态/错误提示、完成按钮、Tooltip。
- **统计窗口**：标题、今日/最近 7 天/指定日期、查询、刷新、总/下载/上传/连接次数、7 天图标题/图例/悬停、表头、程序排行、占比、未归属/系统说明、加载/空/失败/重试状态、页脚。
- **悬浮胶囊**：`Data used` 标签、`Down/Up` 速度标签、右键菜单四项、固定/解除固定文字、“上次会话”前缀、采集警告。
- **托盘**：流量统计、设置、显示/隐藏胶囊、退出。
- **启动/错误**：单实例提示、开机自启注册表状态与错误、任务栏检测说明。

## 7. 布局适配

- 设置窗口由 780×620 调整为 780×700，给语言卡留出空间；仍按工作区夹取。
- 语言选项使用两列等宽卡片；英文说明较长时换行，不截断。
- 统计窗口 980×700 / 最小 860×620 未改；英文标签在现有列宽内通过布局断言检查，无重叠/越界。
- 胶囊在自动适配最简密度下按原设计隐藏说明标签；英文标签在普通四档与自定义尺寸下验证。
- 英文日期格式使用 `en-US` 文化；数值仍由既有 `ByteFormatter` 输出，单位不翻译（KB/MB/GB）。

## 8. 测试与验证

自动化：

- `HotspotMeter.Tests/Localization/LanguageCatalogTests.cs`：18 个用例（规范化、非法值、键集合一致、占位符一致、缺键回退、核心翻译）。
- 全量：`dotnet build HotspotMeter.sln` 0 警告 0 错误；`dotnet test` **408/408**。

运行时（UIAutomation / UI dump / 像素；视觉引擎在本会话不可用）：

- 首次启动、英文选择、写入配置、进入主流程：`artifacts/i18n-picker.png`。
- 取消语言窗：进程退出，配置未新增 `Language`。
- 英文设置窗：`artifacts/i18n-settings-en2.txt`，`layout=ok texts=26`。
- 英文统计窗：`artifacts/i18n-stats-en.txt`，`layout=ok texts=44`。
- 英文胶囊 + 菜单：`artifacts/i18n-ui-en.txt`，四档/自定义/自动适配 `layout=ok`，菜单为
  `Traffic Statistics | Lock Position | Resize… | Exit`。
- 运行中中英切换：设置标题、统计标题/标签、胶囊标签即时变化；配置写回 `zh-CN` / `en-US`。
- 英文 Stage 5 自检：`artifacts/i18n-stage5-en.txt`，startup/taskbar/topmost/stats/monitor 全通过。

## 9. 维护方式（新增文字时）

1. 在 `LanguageCatalog` 的中英两个字典中同时新增同名键。
2. XAML 静态文字用 `{loc:Tr Key}`；代码/服务用 `LocalizationService.T/F`。
3. 含动态值用 `{0}` 占位符，中英顺序可不同但数量必须一致。
4. 运行 `dotnet test`；键集合/占位符不一致会直接失败。
5. 若新增窗口，订阅 `LanguageChanged` 并在关闭时退订，避免静态窗口泄漏。

## 10. 已知限制与未验证项

- 托盘菜单的英文**未通过 UI Automation 实际展开验证**（图标位于任务栏隐藏区，自动化未暴露）；代码路径与 `MenuItemTexts` 已本地化。
- 视觉引擎（`read_image` / modlens）在本会话持续失败，英文界面靠 UI Automation、布局断言与像素采样验证，**未做人工肉眼终检**。
- 胶囊在自动适配最简密度（44 DIP）按设计不显示 `Data used` 标签，因此该档只验证数值与速度；普通四档/自定义已验证标签。
- 英文长文案在极端 DPI（如 200%）与最小窗口尺寸下的组合未逐一实机枚举；当前断言覆盖 150% 与本机窗口尺寸。
- 未实现第三语言；`LanguageCatalog` 的键结构可扩展，但首次选择窗口目前固定两个选项。