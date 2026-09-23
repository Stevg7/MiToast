package com.mitoast.network

import android.content.Context
import android.os.PowerManager
import android.util.Log
import com.mitoast.model.ClearNotificationMessage
import com.mitoast.model.NotificationMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

object NetworkManager {

    private const val TAG = "MiToastNetMgr"
    private const val WS_PORT = 8080

    /** 单次发送持锁的最长时间（兜底，异常路径防 WakeLock 泄漏） */
    private const val TRANSIENT_WAKELOCK_TIMEOUT_MS = 20_000L
    /** send() 仅把数据入队，等待 selector 线程真正刷出 socket 后再允许 CPU 休眠 */
    private const val WAKELOCK_HOLD_AFTER_SEND_MS = 2_000L

    private lateinit var discoveryService: DiscoveryService
    private var initialized = false
    private val mgrScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    @Volatile
    private var wsServer: MiToastWebSocketServer? = null

    private lateinit var appContext: android.content.Context

    /** 电脑端接入配对码（WS 服务创建时使用）。 */
    private var authToken: String = ""

    private var notificationListenerConnected = false

    @Volatile
    var isRunning = false
        private set

    @Volatile
    var currentIp: String = ""
        private set

    @Volatile
    var connectedClients: Int = 0
        private set

    @Volatile
    var listenerConnected: Boolean = false
        private set

    fun init(context: android.content.Context) {
        if (initialized) return
        initialized = true
        appContext = context.applicationContext
        authToken = com.mitoast.security.PairToken.get(appContext)
        discoveryService = DiscoveryService(WS_PORT)
        // 妙播设备上下线时主动推送给已连接的 PC
        com.mitoast.media.CastRouteManager.routesChangedListener = {
            broadcastCastDevices()
        }
        startWsWatchdog()
    }

    /** 把当前妙播设备列表/小米妙播投射状态广播给所有 PC 客户端。 */
    fun broadcastCastDevices() {
        if (!isRunning) return
        mgrScope.launch {
            try {
                wsServer?.broadcastCastDevices()
            } catch (e: Exception) {
                Log.e(TAG, "broadcastCastDevices failed", e)
            }
        }
    }

    /** 创建并启动一个新的 WS 服务端实例（带配对码接入认证） */
    private fun startWsServer() {
        try {
            wsServer = MiToastWebSocketServer(WS_PORT, authToken).also { it.start() }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start WS server", e)
        }
    }

    /**
     * 看门狗：应用重启/强停后端口可能被旧实例短暂占用，WebSocketServer 绑定失败后
     * 线程会永久退出。每 10 秒检测一次，端口未就绪则销毁旧实例并重建。
     */
    private fun startWsWatchdog() {
        mgrScope.launch {
            while (true) {
                delay(10_000)
                try {
                    if (isRunning && wsServer?.portReady != true) {
                        Log.w(TAG, "WS server not ready, recreating server instance")
                        try { wsServer?.stop(500) } catch (_: Exception) {}
                        startWsServer()
                        updateStatus()
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "WS watchdog error", e)
                }
            }
        }
    }

    fun start() {
        if (isRunning) return
        isRunning = true
        startWsServer()
        discoveryService.start()
        updateStatus()
    }

    fun stop() {
        if (!isRunning) return
        isRunning = false
        try { wsServer?.stop() } catch (e: Exception) { Log.e(TAG, "Stop WS error", e) }
        wsServer = null
        discoveryService.stop()
        updateStatus()
    }

    /**
     * 广播通知到所有 PC 客户端。
     *
     * 锁屏/Doze 场景下系统投递通知回调（NotificationListenerService.onNotificationPosted）
     * 时持有的短暂 WakeLock 会在回调返回后立刻释放，之后 CPU 可能马上休眠，导致协程里的
     * JSON 序列化与 socket 写出被推迟到下一个 Doze 维护窗口（数分钟后）。因此这里在
     * **回调线程上同步**获取短时 PARTIAL_WAKE_LOCK（保证回调返回前锁已持有），发送完成
     * 并等待数据真正刷出 socket 后再释放。
     */
    fun broadcastNotification(message: NotificationMessage) {
        if (!isRunning) return
        val wakeLock = acquireTransientWakeLock("notify-send")
        mgrScope.launch {
            try {
                wsServer?.broadcastNotification(message)
                connectedClients = wsServer?.getConnectedCount() ?: 0
                delay(WAKELOCK_HOLD_AFTER_SEND_MS)
            } catch (e: Exception) {
                Log.e(TAG, "broadcastNotification failed", e)
            } finally {
                releaseWakeLock(wakeLock)
            }
        }
    }

    fun broadcastClear(message: ClearNotificationMessage) {
        if (!isRunning) return
        val wakeLock = acquireTransientWakeLock("notify-clear")
        mgrScope.launch {
            try {
                wsServer?.broadcastClear(message)
                delay(WAKELOCK_HOLD_AFTER_SEND_MS)
            } catch (e: Exception) {
                Log.e(TAG, "broadcastClear failed", e)
            } finally {
                releaseWakeLock(wakeLock)
            }
        }
    }

    /** 获取带超时兜底的短时 WakeLock（超时自动释放，防止异常路径永久持锁耗电）。 */
    private fun acquireTransientWakeLock(tag: String): PowerManager.WakeLock? {
        return try {
            val pm = appContext.getSystemService(Context.POWER_SERVICE) as PowerManager
            pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "MiToast:$tag").apply {
                setReferenceCounted(false)
                acquire(TRANSIENT_WAKELOCK_TIMEOUT_MS)
            }
        } catch (e: Exception) {
            Log.e(TAG, "acquireTransientWakeLock failed", e)
            null
        }
    }

    private fun releaseWakeLock(wakeLock: PowerManager.WakeLock?) {
        try {
            if (wakeLock?.isHeld == true) wakeLock.release()
        } catch (_: Exception) {
        }
    }

    fun setNotificationListenerConnected(connected: Boolean) {
        notificationListenerConnected = connected
        listenerConnected = connected
    }

    fun executeOpenApp(packageName: String) {
        // Android 10+ 后台启动 Activity 限制（BAL）：App 在后台时直接 startActivity
        // 会被系统静默拦截。优先用 Shizuku（shell uid，有后台启动权限）执行 monkey 拉起，
        // Shizuku 不可用时回退常规启动（App 在前台时仍可生效）。
        CoroutineScope(Dispatchers.IO).launch {
            try {
                if (com.mitoast.shizuku.ShizukuHelper.isAvailable) {
                    val cmd = "monkey -p $packageName -c android.intent.category.LAUNCHER 1"
                    val result = com.mitoast.shizuku.ShizukuHelper.execute(cmd)
                    Log.d(TAG, "openApp via Shizuku: $result")
                    if (!result.startsWith("ERROR") && !result.startsWith("EXCEPTION")) {
                        return@launch
                    }
                }
                launchAppByIntent(packageName)
            } catch (e: Exception) {
                Log.e(TAG, "openApp via Shizuku failed, fallback to intent", e)
                launchAppByIntent(packageName)
            }
        }
    }

    private fun launchAppByIntent(packageName: String) {
        try {
            val intent = appContext.packageManager.getLaunchIntentForPackage(packageName)
            if (intent != null) {
                intent.addFlags(
                    android.content.Intent.FLAG_ACTIVITY_NEW_TASK or
                        android.content.Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED
                )
                appContext.startActivity(intent)
                Log.d(TAG, "openApp via intent: $packageName")
            } else {
                Log.w(TAG, "No launch intent for $packageName")
            }
        } catch (e: Exception) {
            Log.e(TAG, "openApp failed", e)
        }
    }

    fun broadcastDndStatus(enabled: Boolean) {
        if (!isRunning) return
        wsServer?.broadcastDndStatus(enabled)
    }

    fun updateStatus() {
        connectedClients = if (isRunning) wsServer?.getConnectedCount() ?: 0 else 0
        try {
            val interfaces = java.net.NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val iface = interfaces.nextElement()
                val addresses = iface.inetAddresses
                while (addresses.hasMoreElements()) {
                    val addr = addresses.nextElement()
                    if (!addr.isLoopbackAddress && addr.hostAddress.contains('.')) {
                        currentIp = addr.hostAddress
                        return
                    }
                }
            }
        } catch (_: Exception) {}
    }
}
