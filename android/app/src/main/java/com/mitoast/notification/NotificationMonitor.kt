package com.mitoast.notification

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.service.notification.NotificationListenerService
import android.service.notification.NotificationListenerService.Ranking
import android.service.notification.NotificationListenerService.RankingMap
import android.service.notification.StatusBarNotification
import android.util.Log
import com.mitoast.MiToastApp
import com.mitoast.model.ClearNotificationMessage
import com.mitoast.network.NetworkManager
import com.mitoast.prefs.AppWhitelistManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class NotificationMonitor : NotificationListenerService() {

    companion object {
        private const val TAG = "MiToastNL"
        private const val FOREGROUND_CHANNEL_ID = "mitoast_listener"
        private const val FOREGROUND_NOTIFICATION_ID = 1002
        private const val LISTENER_REBIND_DELAY_MS = 1500L

        // Notification.CATEGORY_SERVICE（"service"，API 29 引入），用字面量兼容低版本
        private const val CATEGORY_SERVICE = "service"



        private var instance: NotificationMonitor? = null
        private var isForeground = false

        fun isListenerConnected(): Boolean = instance?.isBound == true

        fun getInstance(): NotificationMonitor? = instance

        /** 静态重绑定（API 24+），供外部组件调用 */
        fun requestRebind(context: Context) {
            try {
                NotificationListenerService.requestRebind(
                    ComponentName(context, NotificationMonitor::class.java)
                )
            } catch (e: Exception) {
                Log.e(TAG, "requestRebind failed", e)
            }
        }
    }

    @Volatile
    private var isBound = false

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var rebinding = false

    private val mediaActionsByKey = java.util.concurrent.ConcurrentHashMap<String, List<Notification.Action>>()

    override fun onCreate() {
        super.onCreate()
        instance = this
        startAsForeground()
        Log.i(TAG, "NotificationMonitor created")
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        isBound = true
        rebinding = false
        NetworkManager.setNotificationListenerConnected(true)
        startAsForeground()
        Log.i(TAG, "NotificationListener connected")
    }

    override fun onListenerDisconnected() {
        super.onListenerDisconnected()
        isBound = false
        NetworkManager.setNotificationListenerConnected(false)
        Log.w(TAG, "NotificationListener disconnected, scheduling rebind")

        scope.launch {
            rebindWithDelay()
        }
    }

    private suspend fun rebindWithDelay() {
        if (rebinding) return
        rebinding = true
        var attempt = 0
        // 守卫 instance === this：系统重绑时会销毁旧实例并新建实例，旧实例的协程
        // 不应继续 requestRebind（否则会反复打断新实例的绑定）。
        while (rebinding && !isBound && instance === this@NotificationMonitor) {
            attempt++
            val delayMs = (LISTENER_REBIND_DELAY_MS * (1 + attempt * 0.5)).toLong()
            Log.i(TAG, "Attempt $attempt to rebind NotificationListener in ${delayMs}ms")
            delay(delayMs)
            try {
                requestRebind(this@NotificationMonitor)
                if (isBound) break
            } catch (e: Exception) {
                Log.e(TAG, "Rebind failed", e)
            }
        }
        rebinding = false
    }

    @Volatile
    private var rankingMap: RankingMap? = null

    override fun onNotificationRankingUpdate(rankingMap: RankingMap?) {
        super.onNotificationRankingUpdate(rankingMap)
        this.rankingMap = rankingMap
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        sbn ?: return
        if (!shouldForward(sbn)) return

        val appName = getAppName(sbn.packageName)
        val message = NotificationConverter.convert(sbn, appName)

        // 直接在回调（binder）线程上调用：broadcastNotification 内部会同步获取短时
        // WakeLock 后再异步发送，保证锁屏/Doze 下系统回调持锁释放前我们的锁已拿到，
        // 发送不会因 CPU 休眠被推迟到下一个维护窗口。
        NetworkManager.broadcastNotification(message)
        if (sbn.packageName == "com.android.mms") {
            Log.i(TAG, "MMS forwarded: key=${message.key} title=${message.title} content=${message.content.take(30)}")
        }

        val key = buildKey(sbn)
        val actions = sbn.notification.actions
        if (actions != null && actions.isNotEmpty()) {
            mediaActionsByKey[key] = actions.toList()
        } else {
            mediaActionsByKey.remove(key)
        }
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        sbn ?: return
        val stableKey = buildKey(sbn)
        NetworkManager.broadcastClear(ClearNotificationMessage(key = stableKey))
        mediaActionsByKey.remove(stableKey)
    }

    fun executeMediaAction(key: String, actionIndex: Int): Boolean {
        val actions = mediaActionsByKey[key] ?: return false
        if (actionIndex < 0 || actionIndex >= actions.size) return false
        val action = actions[actionIndex]
        return try {
            action.actionIntent?.send() ?: return false
            true
        } catch (e: Exception) {
            Log.e(TAG, "executeMediaAction failed", e)
            false
        }
    }

    private fun shouldForward(sbn: StatusBarNotification): Boolean {
        if (sbn.packageName == packageName) return false
        // 白名单过滤：白名单模式下仅转发勾选应用；默认过滤系统级应用
        if (!AppWhitelistManager.isPackageAllowed(this, sbn.packageName)) {
            Log.d(TAG, "Notification filtered by whitelist: ${sbn.packageName}")
            return false
        }
        // 通知栏可见性过滤：监听服务能收到所有已发布通知，但低重要度通知
        // （MIN/NONE）不显示状态栏图标，在 MIUI/HyperOS 上还会被收入"通知收纳"，
        // 用户在通知栏里根本看不到，这类通知不应转发到电脑端。
        if (!isVisibleInShade(sbn)) return false
        val extras = sbn.notification.extras
        val title = extras.getCharSequence("android.title")?.toString().orEmpty()
        val text = extras.getCharSequence("android.text")?.toString().orEmpty()
        val bigText = extras.getCharSequence("android.bigText")?.toString().orEmpty()
        val bigTitle = extras.getCharSequence("android.bigTitle")?.toString().orEmpty()
        val subText = extras.getCharSequence("android.subText")?.toString().orEmpty()
        val t = sbn.notification.tickerText?.toString().orEmpty()
        // HyperOS 焦点通知的文案在 miui.focus.param JSON 里（标准字段可能为空）
        val hasFocusContent = extras.containsKey("miui.focus.param")

        return title.isNotBlank() || text.isNotBlank() || bigText.isNotBlank() ||
               bigTitle.isNotBlank() || subText.isNotBlank() || t.isNotBlank() ||
               hasFocusContent
    }

    /**
     * 判断通知是否真的会出现在手机通知栏中。
     *
     * NotificationListenerService 能收到系统中所有已发布通知，以下几类在通知栏里不可见：
     * 1. 渠道重要度为 IMPORTANCE_MIN/NONE 的通知：无状态栏图标、不发声；
     *    MIUI/HyperOS 会把它们收进"通知收纳/不重要通知"，下拉通知栏看不到；
     * 2. 分组摘要通知（FLAG_GROUP_SUMMARY）：系统自动聚合摘要（AUTOGROUP_SUMMARY，
     *    HyperOS 用它折叠后台服务通知）无转发价值；App 自定义分组在同组存在子通知时，
     *    通知栏展开后显示的是子通知，摘要不单独展示，转发会与子通知重复；
     * 3. 后台保活用的前台服务通知：FGS 常驻、category 为空或 service、无操作按钮
     *    （妙享/音效/投屏/小爱语音/系统安全组件等），HyperOS 不把它们显示在通知栏，
     *    而是折叠进后台运行/收纳区；媒体（transport/MediaStyle）、HyperOS 焦点实况
     *    （miui.focus.* 超级岛卡片）、通话、导航、下载进度等用户可见的前台服务通知
     *    不受影响；
     * 4. 组名/渠道名带 autogroup/hide_/foreground_service/keepalive 等系统保留特征：
     *    MIUI/HyperOS 把这些通知折叠进自动分组或后台运行区，通知栏不直接展示。
     */
    private fun isVisibleInShade(sbn: StatusBarNotification): Boolean {
        val n = sbn.notification ?: return false
        val importance = resolveImportance(sbn)
        // IMPORTANCE_UNSPECIFIED(-100) 表示系统未给出有效排名，按可见处理避免漏转发
        if (importance != NotificationManager.IMPORTANCE_UNSPECIFIED &&
            importance <= NotificationManager.IMPORTANCE_MIN
        ) {
            Log.d(TAG, "Skip low-importance notification (imp=$importance): ${sbn.packageName}")
            return false
        }
        if (isHiddenGroupSummary(sbn.packageName, n)) {
            Log.d(TAG, "Skip group summary: ${sbn.packageName}")
            return false
        }
        if (isBackgroundServiceNoise(n)) {
            Log.d(TAG, "Skip background-service notification: ${sbn.packageName} cat=${n.category}")
            return false
        }
        // HyperOS/MIUI 折叠到后台/收纳区的通知：组名或渠道名带 autogroup/hide_/
        // foreground_service/keepalive 等系统保留特征，通知栏不直接展示
        if (isSystemHiddenName(n.group) || isSystemHiddenName(n.channelId)) {
            Log.d(TAG, "Skip auto-grouped hidden notification: ${sbn.packageName}" +
                " group=${n.group} channel=${n.channelId}")
            return false
        }
        return true
    }

    /**
     * 分组摘要是否应跳过：系统/MIUI 合成的聚合摘要（无 group、或 group 名为
     * autogroup/Aggregate_/hide_foreground 等系统保留特征）不展示独立内容；
     * App 自定义分组摘要在同组存在子通知时，通知栏展开后只显示子通知。
     */
    private fun isHiddenGroupSummary(packageName: String, n: Notification): Boolean {
        if (n.flags and Notification.FLAG_GROUP_SUMMARY == 0) return false
        val group = n.group
        if (group == null) return true
        if (isSystemHiddenName(group)) return true
        return try {
            activeNotifications?.any { other ->
                other.packageName == packageName &&
                    other.notification != null &&
                    other.notification.group == group &&
                    other.notification.flags and Notification.FLAG_GROUP_SUMMARY == 0
            } == true
        } catch (_: Throwable) {
            false
        }
    }

    /**
     * MIUI/HyperOS 折叠后台通知时使用的系统保留命名特征（组名或渠道名）：
     * *_autogroup（系统自动分组）、hide_*（隐藏前台服务）、Aggregate_*（通知栏
     * 静默/提醒区聚合摘要）、foreground_service/background_service/keepalive
     * （后台保活渠道）。命中这些名称的通知不会在通知栏直接展示。
     */
    private fun isSystemHiddenName(name: String?): Boolean {
        val g = name?.lowercase() ?: return false
        return g.contains("autogroup") || g.contains("hide_") ||
            g.contains("aggregate_") || g.contains("foreground_service") ||
            g.contains("background_service") || g.contains("keepalive")
    }

    /**
     * HyperOS 焦点通知（超级岛/实况通知）：携带 miui.focus.param（焦点模板 JSON）
     * 或 miui.focus.pics（焦点图标包），必定在超级岛和通知栏展示（实时功率、温度、
     * 物流实况、自定义实况卡片等），必须放行。
     */
    private fun isMiuiFocusNotification(n: Notification): Boolean {
        val e = n.extras ?: return false
        return e.containsKey("miui.focus.param") || e.containsKey("miui.focus.pics")
    }

    /**
     * 后台保活/状态类前台服务通知（通知栏不展示）。媒体、焦点实况、通话、导航、
     * 下载进度等用户可见的前台服务通知通过 category / MediaSession / 焦点 extras /
     * 操作按钮识别并放行。
     */
    private fun isBackgroundServiceNoise(n: Notification): Boolean {
        if (n.flags and Notification.FLAG_FOREGROUND_SERVICE == 0) return false
        val cat = n.category
        // 媒体播放（音乐/视频投屏等）：通知栏常驻可见
        if (cat == Notification.CATEGORY_TRANSPORT) return false
        if (n.extras?.containsKey(Notification.EXTRA_MEDIA_SESSION) == true) return false
        // HyperOS 焦点/实况通知（超级岛卡片）：通知栏必定可见
        if (isMiuiFocusNotification(n)) return false
        // 其余显式类别（call/navigation/progress/alarm/reminder/message 等）均为用户可见场景；
        // CATEGORY_SERVICE("service", API 29) 与无类别一样视为后台服务通知。
        if (cat != null && cat != CATEGORY_SERVICE) return false
        // 带操作按钮的前台服务通知（录音停止、下载取消等）通常在通知栏可见
        if (!n.actions.isNullOrEmpty()) return false
        return true
    }

    /**
     * 取通知的最终生效重要度：优先用系统实时 Ranking（系统裁决后的有效值，已包含
     * 渠道重要度、勿扰策略等），Ranking 缺失时回退旧版 priority 映射。
     */
    private fun resolveImportance(sbn: StatusBarNotification): Int {
        rankingMap?.let { map ->
            try {
                val ranking = Ranking()
                if (map.getRanking(sbn.key, ranking)) return ranking.importance
            } catch (_: Throwable) {
            }
        }
        return when (sbn.notification.priority) {
            Notification.PRIORITY_MIN -> NotificationManager.IMPORTANCE_MIN
            Notification.PRIORITY_LOW -> NotificationManager.IMPORTANCE_LOW
            Notification.PRIORITY_DEFAULT -> NotificationManager.IMPORTANCE_DEFAULT
            Notification.PRIORITY_HIGH -> NotificationManager.IMPORTANCE_HIGH
            else -> NotificationManager.IMPORTANCE_UNSPECIFIED
        }
    }

    private fun buildKey(sbn: StatusBarNotification): String {
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                val k = sbn.key
                if (k != null && k.isNotEmpty()) return k
            }
            "${sbn.packageName}_${sbn.id}"
        } catch (_: Throwable) {
            "${sbn.packageName}_${sbn.id}"
        }
    }

    private fun getAppName(packageName: String): String {
        return try {
            val pm = MiToastApp.instance.packageManager
            val info = pm.getApplicationInfo(packageName, 0)
            pm.getApplicationLabel(info).toString()
        } catch (_: android.content.pm.PackageManager.NameNotFoundException) {
            packageName
        }
    }

    private fun startAsForeground() {
        if (isForeground) return
        isForeground = true
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
                val existing = nm.getNotificationChannel(FOREGROUND_CHANNEL_ID)
                if (existing == null) {
                    val channel = NotificationChannel(
                        FOREGROUND_CHANNEL_ID,
                        "MiToast 通知监听",
                        NotificationManager.IMPORTANCE_LOW
                    ).apply {
                        description = "保持 MiToast 通知监听服务运行"
                        setShowBadge(false)
                        enableLights(false)
                        enableVibration(false)
                    }
                    nm.createNotificationChannel(channel)
                }
            }

            val notification = buildForegroundNotification()
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(FOREGROUND_NOTIFICATION_ID, notification,
                    android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            } else {
                @Suppress("DEPRECATION")
                startForeground(FOREGROUND_NOTIFICATION_ID, notification)
            }
            Log.i(TAG, "NotificationListenerService started as foreground")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start foreground", e)
            isForeground = false
        }
    }

    private fun buildForegroundNotification(): Notification {
        val text = if (isBound) "通知监听已连接 · 转发服务运行中" else "通知监听连接中..."
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, FOREGROUND_CHANNEL_ID)
                .setContentTitle("MiToast")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setOngoing(true)
                .setWhen(0)
                .setShowWhen(false)
                .build()
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
                .setContentTitle("MiToast")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setOngoing(true)
                .build()
        }
    }

    override fun onBind(intent: Intent?): IBinder? {
        val binder = super.onBind(intent)
        instance = this
        startAsForeground()
        return binder
    }

    override fun onDestroy() {
        super.onDestroy()
        // 取消本实例的全部协程，避免僵尸重连循环
        scope.cancel()
        // 系统重绑时旧实例销毁、新实例可能已创建；只有当前活跃实例才允许清空静态状态，
        // 防止旧实例的销毁覆盖新实例的 connected 状态。
        if (instance === this) {
            instance = null
            isBound = false
            NetworkManager.setNotificationListenerConnected(false)
        }
        Log.i(TAG, "NotificationMonitor destroyed")
    }
}
