# MiToast

把 Android 手机通知实时同步到 Windows 桌面。

手机端监听系统通知，通过局域网 WebSocket 推送到电脑；电脑端以无边框卡片的形式把通知浮在屏幕角落，鼠标可直接点按卡片上的按钮控制手机（打开 App、切歌、流转投屏）。

```
┌─────────────────── 手机（Android）───────────────────┐        ┌──────────── PC（Windows）────────────┐
│                                                      │        │                                      │
│  NotificationListenerService                         │        │   MiToast.exe（托盘常驻）             │
│        │  监听通知、过滤、解析进度/媒体控件            │        │        │                             │
│        ▼                                             │        │        ▼                             │
│  NotificationConverter ──► NotificationMessage       │        │   透明通知宿主窗口                    │
│                                 │                    │        │   （全屏、鼠标穿透、卡片堆叠）        │
│                                 ▼                    │        │        ▲                             │
│  MiToastWebSocketServer  (TCP 8080)  ◄───────────────┼────────┼────────┤  通知卡片                    │
│        │                                             │  ws:// │        │                             │
│  DiscoveryService        (UDP 9000)  ◄───────────────┼────────┼────────┘                             │
│        │                                             │ 广播   │   NetworkManager                     │
│  无障碍服务 / Shizuku（保活）                         │        │   自动发现 → 连接 → 重连（指数退避）  │
└──────────────────────────────────────────────────────┘        └──────────────────────────────────────┘
```

## 功能

### 手机端（Android）

- **通知监听与转发**：基于 `NotificationListenerService`，前台服务保活，通知产生即时推送
- **通知过滤**：默认转发所有非系统应用，短信/电话/日历/闹钟等常用系统应用默认放行；可切换为白名单模式，只转发勾选的应用
- **可见性判断**：低重要度通知、分组摘要、后台保活前台的噪音通知、HyperOS 自动收纳区的通知都不会转发，避免电脑端出现通知栏里根本看不到的内容
- **HyperOS 焦点通知**：解析 `miui.focus.param`（超级岛 / 实况通知）的文案与进度
- **进度解析**：识别外卖配送与到店取餐通知，提取取餐码、预计时间、门店位置，并映射为 6/7 阶段进度
- **媒体控件**：从通知 Action 中识别上一曲 / 播放暂停 / 下一曲 / 收藏，读取播放位置与总时长
- **勿扰状态同步**：定时检测手机勿扰模式并广播给电脑
- **投屏设备**：列出可用投屏设备与小米妙播状态，支持流转（设备列表走 MediaRouter2，需 Android 11 / API 30 及以上；更低版本仅显示本机）
- **保活手段**：无障碍服务自动拉起、Shizuku（可选，用 shell 权限重启服务并在后台启动 App）、忽略电池优化引导、WebSocket 端口看门狗
- **Compose 权限引导页**：一屏查看各项权限状态并跳转设置

### 电脑端（Windows）

- **桌面通知卡片**：无边框透明窗口，卡片堆叠在屏幕任意一角（左上 / 右上 / 左下 / 右下），圆角阴影，自动消失（默认 9 秒，常驻通知不消失）
- **完全鼠标穿透**：卡片之外的区域（含阴影与缝隙）点击直接穿透到下层窗口，不挡操作
- **全屏自动隐藏**：检测到前台全屏窗口时自动沉底，退出全屏后恢复置顶
- **深色模式**
- **勿扰模式**：关闭 / PC 端静默（不弹卡片但仍记入历史）/ 跟随手机勿扰状态
- **媒体控件**：卡片上直接切歌、播放暂停
- **打开手机 App**：点卡片上的「打开」，电脑端发送指令，手机端拉起对应 App（优先走 Shizuku，规避后台启动限制）
- **历史记录**：最近 500 条通知持久化到本地，可随时回看
- **系统托盘**：现代风格托盘菜单与设置窗口，可调位置、卡片宽度、屏幕边距、卡片间距、紧凑度

## 工作原理

### 设备发现

电脑端向 `UDP 9000` 广播 `MTOAST_DISCOVER_REQUEST`，手机端收到后单播回复：

```
MTOAST_DISCOVER_RESPONSE|<手机IP>|<WebSocket端口>
```

拿到地址后，电脑端作为 **客户端** 连接手机的 WebSocket 服务（`ws://<手机IP>:8080`）。断开后按指数退避重连（1 秒起，最长 30 秒），每 30 秒发送一次 `ping` 保活。

### 消息协议

基于 WebSocket 文本帧传输 JSON，通过 `type` 字段区分。手机端使用 `kotlinx.serialization`，电脑端使用 `System.Text.Json`。

手机 → 电脑：

| type | 载荷 | 说明 |
| --- | --- | --- |
| `ping` | `deviceName`、`deviceModel` | 连接建立时握手 |
| `notification` | 完整的 `NotificationMessage` | 新通知，同 key 重复到达时更新已有卡片 |
| `clear` | `key` | 通知在手机上被移除 |
| `dnd_status` | `enabled` | 手机勿扰模式状态变化 |
| `cast_devices` | `devices`、`miplaySupported`、`miplayCasting`、`carCasting` | 投屏设备列表与妙播状态 |

电脑 → 手机：

| type | 载荷 | 说明 |
| --- | --- | --- |
| `ping` | — | 保活 |
| `open_app` | `packageName` | 打开手机上的应用 |
| `media_action` | `key`、`actionIndex` | 触发该通知第 index 个媒体按钮 |
| `cast_query` | — | 请求刷新设备列表 |
| `cast_transfer` | `routeId` | 把媒体流转到指定设备 |
| `miplay_switch` | `target`（`local` / `device`） | 切换小米妙播输出 |

## 环境要求

| | 要求 |
| --- | --- |
| 手机 | Android 7.0（API 24）及以上；小米 / HyperOS 机型体验最完整 |
| 电脑 | Windows 10 / 11 x64，需要 .NET 8 桌面运行时（或使用自包含发布） |
| 网络 | 手机与电脑处于**同一局域网** |

## 构建

### Android 端

需要 **JDK 17 ~ 21**（Gradle 8.7 不支持更新的 JDK）。工程使用 Gradle Wrapper，无需单独安装 Gradle。

```bash
cd android
./gradlew :app:assembleDebug
```

产物：`android/app/build/outputs/apk/debug/app-debug.apk`

技术栈：Kotlin 1.9.22、AGP 8.5.2、Jetpack Compose（BOM 2024.02.00）、Java-WebSocket 1.5.4、Shizuku 13.1.5，`compileSdk 34` / `minSdk 24`。

> **关于 JDK 配置**：`gradlew` 启动脚本本身也需要一个 JDK —— 在启动 Gradle 之前就要能读到 `JAVA_HOME` 或 PATH 上的 `java`，否则会直接报 `ERROR: JAVA_HOME is not set`。如果本机默认 JDK 比 21 新（Gradle 8.7 不支持），可在**用户级**配置文件里固定构建用的 JDK，这样不必往仓库里塞机器相关路径：
>
> ```properties
> # ~/.gradle/gradle.properties  （Windows: C:\Users\<你>\.gradle\gradle.properties）
> org.gradle.java.home=C:/path/to/jdk-17-or-21
> ```
>
> 找不到 `java` 时，命令行构建还需临时指定：`JAVA_HOME=/path/to/jdk ./gradlew :app:assembleDebug`（用 Android Studio 构建则无需此步，IDE 会自带 JDK）。

### Windows 端

需要 **.NET 8 SDK**。

```bash
dotnet build windows/MiToast.sln -c Release
```

发布为单文件 exe：

```bash
dotnet publish windows/src/MiToast/MiToast.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

技术栈：.NET 8（`net8.0-windows`）、WPF + WinForms（托盘图标），无第三方依赖。

## 使用

1. **电脑端**：运行 `MiToast.exe`，程序常驻系统托盘，自动开始搜索手机。双击托盘图标打开设置，右键打开快捷菜单。
2. **手机端**：安装 APK 后打开 MiToast，依次完成权限设置（点每一行即可跳转对应系统页面）：
   - **通知使用权限**（必须）— 允许读取并转发通知
   - **无障碍保活服务**（强烈建议）— 服务被系统清理后自动重启
   - **后台运行**（强烈建议）— 忽略电池优化，避免锁屏后网络被挂起
   - **Shizuku 授权**（可选）— 进阶保活，并让「打开 App」指令能在后台拉起应用
   - **自启动与省电策略**（小米 / HyperOS 机型）— 设为「无限制」
3. **启动同步**：在首页点「启动服务」，电脑端托盘提示「已连接到 Android 设备」后即可生效。

## 常见问题

**电脑端一直显示「正在搜索 Android 设备」**
确认手机与电脑在同一局域网（同一 Wi-Fi，且未开启 AP 隔离）；确认手机端已点「启动服务」；检查手机首页显示的「手机地址」是否与电脑同网段。部分路由器会拦截 UDP 广播，此时需检查路由器设置。

**锁屏后通知不同步**
把 MiToast 加入电池优化白名单，并把 MIUI / HyperOS 的省电策略设为「无限制」。

**通知没转发过来**
默认只转发非系统应用，检查「通知应用白名单」设置；另外手机通知栏里看不到的通知（被收入收纳区、低重要度、后台保活类）不会转发。

**「打开」按钮无效**
Android 10 起限制后台启动 Activity，建议开启 Shizuku 授权；未开启时仅在 MiToast 处于前台时可以拉起。

## 目录结构

```
MiToast/
├── android/                          # Android 客户端（Kotlin）
│   └── app/src/main/java/com/mitoast/
│       ├── MainActivity.kt           # Compose 权限引导页
│       ├── notification/             # 通知监听、过滤、字段解析
│       ├── network/                  # WebSocket 服务端、UDP 发现、连接管理
│       ├── accessibility/            # 无障碍保活服务
│       ├── shizuku/                  # Shizuku 授权与命令执行
│       ├── media/                    # 投屏 / 妙播设备管理
│       ├── prefs/                    # 通知白名单
│       ├── receiver/                 # 开机自启、网络变化
│       └── model/                    # 消息数据类
└── windows/                          # Windows 客户端（WPF）
    └── src/MiToast/
        ├── App.xaml.cs               # 单实例、启动/退出
        ├── MainWindow.xaml           # 透明通知宿主窗口
        ├── Network/                  # 设备发现与 WebSocket 客户端
        ├── UI/                       # 通知卡片
        ├── Windows/                  # 设置、历史窗口
        ├── Services/                 # 设置、历史持久化
        └── Models/
```

本地数据存放在 `%AppData%\MiToast\`（`settings.json`、`history.json`）。

## 说明

- 通信为局域网内**明文** WebSocket，未做加密与认证，请仅在可信网络中使用。
- 项目针对小米 / HyperOS 做了较多适配（焦点通知、妙播流转、系统通知收纳规则），其他 Android 机型功能可用但部分特性不适用。
