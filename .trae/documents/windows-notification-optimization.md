# Windows 端通知显示优化

## Context

用户要求优化 Windows 端通知显示，共 7 项需求：缩小间距、添加删除按钮、音乐通知媒体控件、深色模式、勿扰模式、UI 现代化、点击通知打开手机 APP。当前通知卡片间距 16px 偏大；无关闭按钮（DismissRequested 事件已声明但从未使用）；无深色模式；无勿扰；设置窗口风格陈旧；无打开 APP 功能。需同时改动 Windows 端（WPF）和 Android 端（Kotlin）。

## 实现步骤

### P1 — 基础

**1. 缩小通知间距**
- [MainWindow.xaml.cs](file:///f:/MiToast/windows/src/mitoast/MainWindow.xaml.cs) L20：`NotificationGap = 16` → `6`

**AppSettings 扩展**（F4/F5/F6 前置）
- [AppSettings.cs](file:///f:/MiToast/windows/src/mitoast/Services/AppSettings.cs)：添加 `bool DarkMode` 和 `string DndMode = "off"` 属性；`Normalize()` 追加 DndMode 校验

### P2 — 深色模式 + 关闭/打开按钮

**2. 每张通知添加关闭按钮**
- [MiFocusNotification.xaml](file:///f:/MiToast/windows/src/mitoast/UI/MiFocusNotification.xaml)：Column 2 的 CopyCodeButton 包入 `<StackPanel Orientation="Horizontal">`，在其后添加 `CloseButton`（✕ 图标，始终可见，Click 触发 DismissRequested）。同时在 StackPanel 开头添加 `OpenAppButton`（"打开" 文字，可见性由 packageName 控制）
- [MiFocusNotification.xaml.cs](file:///f:/MiToast/windows/src/mitoast/UI/MiFocusNotification.xaml.cs)：添加 `CloseButton_Click`（触发 DismissRequested）和 `OpenAppButton_Click`（调用 NetworkManager.SendOpenApp）；LoadData() 中设 OpenAppButton.Visibility
- [MainWindow.xaml.cs](file:///f:/MiToast/windows/src/mitoast/MainWindow.xaml.cs) L122：创建卡片后追加 `notification.DismissRequested += (_, _) => DismissByKey(key);`

**4. 深色模式**
- MiFocusNotification.xaml：RootBorder.Background 改 `{x:Null}`；图标 Border 加 `x:Name="IconBackgroundBorder"`
- MiFocusNotification.xaml.cs：添加 `ApplyTheme()` 方法，根据 `AppSettings.Instance.DarkMode` 设置所有颜色：
  - 浅色：bg=White, primary=#1D1D1F, secondary=#86868B, iconBg=#F5F5F7
  - 深色：bg=#1C1C1E, primary=#FFFFFF, secondary=#98989F, iconBg=#2C2C2E
  - 覆盖：RootBorder、IconBackgroundBorder、AppName/Time/Title/HintTitle/Content/HintText/ProgressPercent 的 Foreground，以及 Close/Open/Media 按钮文字色
- LoadData() 末尾调用 ApplyTheme()；MainWindow.ApplySettings() 末尾遍历卡片调 ApplyTheme()

### P3 — 打开 APP + 设置窗口

**7. 点击通知打开手机 APP**
- [NetworkManager.cs](file:///f:/MiToast/windows/src/mitoast/Network/NetworkManager.cs)：添加 `SendOpenApp(packageName)` 和 `SendMediaAction(key, actionIndex)` 方法，用 `JsonSerializer.Serialize` + `_webSocket.SendAsync` 发送
- Android [MiToastWebSocketServer.kt](file:///f:/MiToast/android/app/src/main/java/com/mitoast/network/MiToastWebSocketServer.kt)：`onMessage` 替换为 JSON 解析逻辑，按 type 分发 `open_app`（→ NetworkManager.executeOpenApp）和 `media_action`（→ NotificationMonitor.executeMediaAction）。import `kotlinx.serialization.json.*`
- Android [NetworkManager.kt](file:///f:/MiToast/android/app/src/main/java/com/mitoast/network/NetworkManager.kt)：`init()` 保存 applicationContext；添加 `executeOpenApp(pkg)` 方法用 `packageManager.getLaunchIntentForPackage` 启动

**6. 设置窗口 UI 现代化**
- [SettingsWindow.xaml](file:///f:/MiToast/windows/src/mitoast/Windows/SettingsWindow.xaml)：整体改为 ScrollViewer + StackPanel，内含分区卡片（位置/外观/勿扰/历史）；自定义 ToggleSwitch 样式（CheckBox + ControlTemplate 模拟开关，44x26 轨道 + 22 圆 thumb）；添加 DndMode ComboBox 和 DarkMode ToggleSwitch
- [SettingsWindow.xaml.cs](file:///f:/MiToast/windows/src/mitoast/Windows/SettingsWindow.xaml.cs)：构造函数加载 DarkMode/DndMode；SaveButton_Click 追加保存这两个设置

### P4 — 勿扰模式

**5. 勿扰模式**
- MainWindow.xaml.cs `OnNotificationReceived`：HistoryService.Add 后检查 DndMode——"pc"直接 suppress；"sync"检查 `NetworkManager.Instance.PhoneDndEnabled`；suppress 则 return 不弹窗
- NetworkManager.cs：添加 `bool PhoneDndEnabled` 属性；ProcessMessage 的 switch 追加 `case "dnd_status"` 处理
- Android [NetworkService.kt](file:///f:/MiToast/android/app/src/main/java/com/mitoast/network/NetworkService.kt)：启动协程每 30s 检测 `NotificationManager.currentInterruptionFilter`，变化时调用 `NetworkManager.broadcastDndStatus(enabled)`。DND 开 = filter 不是 ALL(1) 且不是 UNKNOWN(0)
- Android NetworkManager.kt + MiToastWebSocketServer.kt：添加 `broadcastDndStatus(enabled)` 广播 `{"type":"dnd_status","enabled":bool}`
- MainWindow.xaml.cs InitializeTrayIcon()：在设置/历史后、Separator 前插入"勿扰模式（PC 端静默）"托盘菜单项（CheckOnClick，切 off↔pc，调 Settings.Save）

### P5 — 媒体控件（最复杂）

**3. 音乐通知添加媒体控件**
- Android [NotificationMessage.kt](file:///f:/MiToast/android/app/src/main/java/com/mitoast/model/NotificationMessage.kt)：添加 `data class MediaAction(name: String, index: Int)` 和 NotificationMessage 末尾添加 `val mediaActions: List<MediaAction>? = null`
- Android [NotificationConverter.kt](file:///f:/MiToast/android/app/src/main/java/com/mitoast/notification/NotificationConverter.kt)：添加 `extractMediaActions(n: Notification)` 方法——遍历 `n.actions`，按 title 关键词（上一曲/播放/暂停/下一曲 + 英文）匹配，返回 `List<MediaAction>` 或 null；convert() 返回时传入
- Android [NotificationMonitor.kt](file:///f:/MiToast/android/app/src/main/java/com/mitoast/notification/NotificationMonitor.kt)：
  - 添加 `ConcurrentHashMap<String, List<Notification.Action>>` 存储媒体 actions
  - onNotificationPosted 末尾用 buildKey(sbn) 存 actions（非空时）
  - onNotificationRemoved 追加移除
  - 添加 `executeMediaAction(key, actionIndex)` 方法——查 map 取 action，调 `action.actionIntent.send()`
- Windows [NotificationMessage.cs](file:///f:/MiToast/windows/src/mitoast/Models/NotificationMessage.cs)：添加 `class MediaAction { Name, Index }` 和 NotificationMessage 添加 `List<MediaAction>? MediaActions`
- Windows MiFocusNotification.xaml：ProgressPanel 后添加 `MediaPanel`（Border, Collapsed, 圆角, 含横向 StackPanel `MediaButtonsPanel`）
- Windows MiFocusNotification.xaml.cs：
  - LoadData() 调 `BuildMediaButtons()`——遍历 Message.MediaActions 创建按钮（⏮ ▶ ⏭ 等 Unicode 字符），Click 调 `NetworkManager.Instance.SendMediaAction(Message.Key, actionIndex)`
  - CalculateHeight() 追加媒体面板高度（16+40+6）
  - ApplyTheme() 覆盖媒体面板背景和按钮文字色

## 关键注意事项

- JSON 协议字段名须严格匹配：`mediaActions`(Android→Win)、`type`/`key`/`actionIndex`(Win→Android media_action)、`type`/`packageName`(Win→Android open_app)、`type`/`enabled`(Android→Win dnd_status)
- Android 用 `kotlinx.serialization.json.Json { ignoreUnknownKeys = true, encodeDefaults = true }` 解析 incoming；Windows 用 `System.Text.Json` + `[JsonPropertyName]`
- `NotificationMonitor.getInstance()` 已有单例（L40），`buildKey(sbn)` 已有（L146），两者可复用
- `DismissRequested` 事件已声明（MiFocusNotification L21）但从未触发/订阅，本次补全链路
- 深色模式 ApplyTheme 必须覆盖所有文字元素，否则深色背景上不可见
- 勿扰 "sync" 模式：PhoneDndEnabled 默认 false（未收到手机状态时不过滤，安全）
- 媒体 actions 为 null 时（多数非媒体通知）面板折叠，无副作用

## 验证

1. `dotnet build f:\MiToast\windows\src\mitoast` 确保 0 error
2. Android 端 `./gradlew :app:assembleDebug` 确保编译通过
3. 手机发通知验证：间距缩小、关闭按钮可点、深色模式切换实时生效
4. 手机播放音乐（网易云/QQ音乐）验证媒体控件出现且可控制播放
5. 设置勿扰 PC 模式后发通知不弹窗但仍入历史
6. 点击"打开"按钮验证手机上对应 App 启动
