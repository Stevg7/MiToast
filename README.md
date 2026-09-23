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
- **历史记录**：最近 10000 条通知持久化到本地（相同内容自动合并，手机离线期间的通知重连后自动同步补齐），可随时回看
- **历史通知 MCP 接口**：附带的 `MiToastMcp.exe` 实现标准 MCP（stdio）服务器，AI Agent 可直接查询与统计历史通知
- **系统托盘**：现代风格托盘菜单与设置窗口，可调位置、卡片宽度、屏幕边距、卡片间距、紧凑度
- **连接可靠性**：多网卡广播发现、记住上次连上的地址、可手动指定手机地址、连接时绕开系统代理（电脑上拨着代理软件也能连）；连接状态在托盘菜单与设置窗口里可见，长时间搜不到会弹提示引导

## 工作原理

### 设备发现

电脑端向 `UDP 9000` 广播 `MTOAST_DISCOVER_REQUEST`，手机端收到后单播回复：

```
MTOAST_DISCOVER_RESPONSE|<手机IP>|<WebSocket端口>
```

拿到地址后，电脑端作为 **客户端** 连接手机的 WebSocket 服务（`ws://<手机IP>:8080`）。候选地址按下面的顺序收集，然后**并发竞速**，谁先连上就用谁（单个地址最多等 5 秒）：

1. **手动指定的地址 / 上次连上的地址** —— 直接连，跳过发现环节；
2. **广播发现** —— 对每块网卡的子网广播地址**分别**发送发现请求（而不是只发一次 `255.255.255.255`：笔记本往往同时插着网线、连着 Wi-Fi，还挂着 VPN / Hyper-V / WSL 虚拟网卡，只发一次很容易打偏），并在 2.5 秒窗口内持续接收响应；响应的来源地址优先于手机自报的地址（手机上可能还挂着 VPN 或多张卡）；
3. **扫描本机网段**（默认关闭）—— 广播被完全屏蔽时的兜底，逐个探测本网段 8080 端口。扫出来的地址视为不可信，连上后会先确认对方确实是 MiToast 再采用。

连接建立后，手机端会先下发一条握手消息（`ping`，含机型），电脑端据此在托盘显示「已连接 手机型号（地址）」。断开后按指数退避重连（1 秒起，最长 30 秒）；本机网络变化（切换 Wi-Fi、插拔网线、VPN 起停）时会立刻重新搜索，不等 TCP 自己超时。

> **校园网**：如果 Wi-Fi 开了客户端隔离（AP 隔离），广播和直连都可能被拦截，这时只有「手动填写手机地址」这条路——手机端 MiToast 首页会显示手机地址。

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
| `history_sync` | `notifications`（升序列表，每批最多 2000 条） | 响应电脑端 `history_sync_request` 的离线历史增量 |

电脑 → 手机：

| type | 载荷 | 说明 |
| --- | --- | --- |
| `ping` | — | 保活 |
| `auth` | `token`（配对码） | 接入认证：连接建立后必须最先发送，配对码错误会被手机端立即断开 |
| `open_app` | `packageName` | 打开手机上的应用 |
| `media_action` | `key`、`actionIndex` | 触发该通知第 index 个媒体按钮 |
| `cast_query` | — | 请求刷新设备列表 |
| `cast_transfer` | `routeId` | 把媒体流转到指定设备 |
| `miplay_switch` | `target`（`local` / `device`） | 切换小米妙播输出 |
| `history_sync_request` | `since`（电脑端历史最新时间戳） | 认证通过后请求同步离线期间的通知历史，满批次自动续拉 |

## 安全说明

- **历史通知访问锁**：打开历史通知页面、以及 Agent 通过 MCP 查询历史前，都会弹出系统「Windows 安全中心」身份验证（Windows Hello PIN / 指纹 / 人脸，无 Hello 时回退本机密码验证），验证通过才显示/返回数据；一次验证约 2 分钟内免重复弹窗，验证失败不会缓存。
- **历史数据静态加密**：`history.json` 落盘前经 Windows DPAPI（当前用户凭据）加密，换用户、拷贝文件、拆盘读取均无法解密；旧版本产生的明文历史文件在下次保存时自动迁移为密文。
- **手机端接入认证**：手机首页「运行状态」卡片显示 6 位配对码，电脑端在设置「连接 → 配对码」里填入；手机只接受配对码正确的电脑连接，未认证 / 配错码的连接会在 10 秒内被断开。配对码在电脑端同样以 DPAPI 加密存储（settings.json 中只有密文）。
- **MCP 接口**：`MiToastMcp.exe` 仅以 stdio 方式由本机客户端拉起，读取历史前需通过上述系统身份验证；它本身不监听任何网络端口。
- **已知限制**：配对码与通知内容在局域网内以明文 WebSocket 传输（未做 TLS），防御的是局域网内设备的随意接入；对传输层窃听要求高的场景需后续引入加密通道。

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
dotnet publish windows/src/MiToast/MiToast.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

> 后两个参数不能省。只写 `PublishSingleFile=true` 时，WPF 的原生库（`wpfgfx_cor3.dll`、`PresentationNative_cor3.dll` 等）会被留在 exe 旁边的目录里，单独把 exe 拷走会启动失败；加上 `IncludeNativeLibrariesForSelfExtract` 才会真正内嵌（首次启动解压到临时目录），再用 `EnableCompressionInSingleFile` 压缩，体积从约 150 MB 降到约 70 MB。

技术栈：.NET 8（`net8.0-windows`）、WPF + WinForms（托盘图标），无第三方依赖。

#### 历史通知 MCP 服务器

`windows/src/MiToastMcp` 是一个标准 MCP（stdio 传输）服务器，供 AI Agent 读取 MiToast 的历史通知并总结。构建：

```bash
dotnet build windows/src/MiToastMcp/MiToastMcp.csproj -c Release
```

在支持 MCP 的客户端（如 ZCode、Claude Desktop）里注册 stdio 服务器，命令指向 `MiToastMcp.exe`，例如 ZCode：

```bash
zcode mcp add mitoast-history -- stdio F:\path\to\MiToastMcp.exe
```

暴露的工具（均为只读，直接读取 `%AppData%\MiToast\history.json`，无需主程序运行）：

| 工具 | 参数 | 说明 |
| --- | --- | --- |
| `query_notifications` | `limit`（默认 50，最大 500）、`app`、`keyword`、`category`、`since`（Unix 毫秒） | 按条件查询历史通知，时间倒序 |
| `notification_stats` | `hours`（默认 24） | 按应用和分类统计通知数量分布 |

通知分类（`category`）在安卓端采集时确定，取值：

| 取值 | 含义 | 取值 | 含义 |
| --- | --- | --- | --- |
| `chat` | 即时消息、私信与回复 | `promo` | 营销推广、内容推荐 |
| `music` | 音乐/媒体播放 | `system` | 系统、设备状态、隐私提示 |
| `payment` | 支付、银行、账单、转账 | `schedule` | 日程、课程、会议 |
| `verification` | 验证码 | `progress` | 其它带进度的任务 |
| `delivery` | 外卖与即时配送 | `pickup` | 到店取餐/自提 |
| `order` | 电商订单与快递物流 | `general` | 未归类 |

分类规则见 `android/.../notification/NotificationCategory.kt`（纯 Kotlin，可用 `./gradlew :app:testDebugUnitTest` 跑单测）。分类是采集时的属性，旧记录保留入库时的取值，不会随规则调整而改写。

## 使用

1. **电脑端**：运行 `MiToast.exe`，程序常驻系统托盘，自动开始搜索手机。双击托盘图标打开设置，右键打开快捷菜单。若一直搜不到手机（校园网、公共 Wi-Fi 常见），在「设置 → 连接」里手动填写手机地址。
2. **手机端**：安装 APK 后打开 MiToast，依次完成权限设置（点每一行即可跳转对应系统页面）：
   - **通知使用权限**（必须）— 允许读取并转发通知
   - **无障碍保活服务**（强烈建议）— 服务被系统清理后自动重启
   - **后台运行**（强烈建议）— 忽略电池优化，避免锁屏后网络被挂起
   - **Shizuku 授权**（可选）— 进阶保活，并让「打开 App」指令能在后台拉起应用
   - **自启动与省电策略**（小米 / HyperOS 机型）— 设为「无限制」
3. **启动同步**：在首页点「启动服务」，电脑端托盘提示「已连接到 Android 设备」后即可生效。

## 常见问题

**电脑端一直显示「正在搜索 Android 设备」**
先确认手机与电脑在同一网络（同一 Wi-Fi 或同一校园网，且路由器/交换机没有开启 AP 隔离），并确认手机端已点「启动服务」。

校园网和公共 Wi-Fi 普遍屏蔽设备发现广播，这是最常见的原因。解决办法：把手机端 MiToast 首页显示的「手机地址」填到**设置 → 连接 → 手机地址**里（填一次会一直记住，之后直接按这个地址连）。也可以点托盘菜单的「重新搜索设备」手动触发一次重新搜索。

电脑上挂着代理软件（Clash 之类）不影响连接：程序连局域网时会绕开系统代理。若同一网段里还有其他设备开着 8080 端口，程序会先确认对方是 MiToast 才会采用。

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
        ├── Network/                  # 设备发现（多网卡广播、网段扫描）与 WebSocket 客户端
        ├── UI/                       # 通知卡片
        ├── Windows/                  # 设置、历史窗口
        ├── Services/                 # 设置、历史持久化
        └── Models/
```

本地数据存放在 `%AppData%\MiToast\`（`settings.json`、`history.json`）。

## 说明

- 通信为局域网内**明文** WebSocket，未做加密与认证，请仅在可信网络中使用。
- 「设置 → 连接 → 扫描本网段」默认**关闭**：它会在广播搜不到手机时逐个探测本网段端口，部分校园网把端口扫描视为违规行为，请确认网络使用规定后再开启。
- 项目针对小米 / HyperOS 做了较多适配（焦点通知、妙播流转、系统通知收纳规则），其他 Android 机型功能可用但部分特性不适用。
