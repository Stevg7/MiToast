package com.mitoast.accessibility

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Path
import android.os.Build
import android.provider.Settings
import android.util.Log
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityManager
import android.view.accessibility.AccessibilityNodeInfo
import com.mitoast.network.NetworkManager
import com.mitoast.network.NetworkService
import com.mitoast.notification.NotificationMonitor
import com.mitoast.shizuku.ShizukuHelper
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

@SuppressLint("MissingPermission")
class MiToastAccessibilityService : android.accessibilityservice.AccessibilityService() {

    companion object {
        private const val TAG = "MiToastAcc"
        private const val HEALTH_CHECK_INTERVAL_MS = 20_000L

        @Volatile
        private var instance: MiToastAccessibilityService? = null

        fun isRunning(): Boolean = instance != null

        fun getInstance(): MiToastAccessibilityService? = instance

        fun isAccessibilityEnabled(context: Context): Boolean {
            val am = context.getSystemService(Context.ACCESSIBILITY_SERVICE) as AccessibilityManager
            return am.isEnabled && isServiceEnabled(context)
        }

        private fun isServiceEnabled(context: Context): Boolean {
            val expected = "${context.packageName}/${MiToastAccessibilityService::class.java.name}"
            val enabled = Settings.Secure.getString(
                context.contentResolver,
                Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
            ) ?: return false
            return enabled.split(":").any { it.trim() == expected }
        }

        /**
         * 通过无障碍自动化切换小米妙播放设备：
         * target="local" 切回本机；target="device" 切到可用的妙播设备（优先音箱/电视）。
         * 需要无障碍服务已连接。结果回调在任意线程。
         */
        fun switchMiPlay(target: String, onResult: (Boolean, String) -> Unit) {
            val svc = instance
            if (svc == null) {
                onResult(false, "无障碍服务未运行")
                return
            }
            svc.scope.launch {
                svc.switchLock.withLock {
                    svc.doSwitchMiPlay(target, onResult)
                }
            }
        }
    }

    private val switchLock = Mutex()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var healthCheckJob: kotlinx.coroutines.Job? = null
    private var consecutiveFailures = 0

    /** 屏幕点亮 / 用户解锁时触发健康检查（无障碍事件中没有屏幕事件，用广播接收） */
    private val screenReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                Intent.ACTION_SCREEN_ON -> {
                    Log.d(TAG, "Screen ON detected, kicking services")
                    kickstartAllServices()
                }
                Intent.ACTION_USER_PRESENT -> {
                    Log.d(TAG, "User present, verifying services")
                    scope.launch { delay(1000); healthCheck() }
                }
            }
        }
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        Log.i(TAG, "AccessibilityService connected - strongest keepalive layer active")
        consecutiveFailures = 0

        try {
            val filter = IntentFilter().apply {
                addAction(Intent.ACTION_SCREEN_ON)
                addAction(Intent.ACTION_USER_PRESENT)
            }
            registerReceiver(screenReceiver, filter)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to register screen receiver", e)
        }

        startHealthCheckLoop()
        kickstartAllServices()
    }

    override fun onInterrupt() {
        Log.w(TAG, "AccessibilityService interrupted")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // 屏幕/解锁事件通过 BroadcastReceiver 处理；窗口变化时顺手做一次轻量检查
        if (event?.eventType == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) {
            // 不做任何打扰用户的操作，仅利用该事件维持服务活跃
        }
    }

    private fun startHealthCheckLoop() {
        healthCheckJob?.cancel()
        healthCheckJob = scope.launch {
            while (instance != null) {
                healthCheck()
                delay(HEALTH_CHECK_INTERVAL_MS)
            }
        }
    }

    private fun healthCheck() {
        var allGood = true

        if (!NetworkManager.isRunning) {
            Log.w(TAG, "NetworkService is NOT running, restarting...")
            startNetworkService()
            allGood = false
        }

        if (!NotificationMonitor.isListenerConnected()) {
            Log.w(TAG, "NotificationListener is NOT connected, rebinding...")
            try {
                NotificationMonitor.requestRebind(this)
            } catch (e: Exception) {
                Log.e(TAG, "requestRebind failed", e)
            }
            allGood = false
        }

        if (ShizukuHelper.isAvailable && !ShizukuHelper.hasPermission) {
            Log.i(TAG, "Shizuku available but no permission yet")
        }

        if (allGood) {
            consecutiveFailures = 0
        } else {
            consecutiveFailures++
            Log.w(TAG, "Health check failure count: $consecutiveFailures")

            if (consecutiveFailures >= 3) {
                Log.e(TAG, "Persistent failures, attempting Shizuku-based force start")
                forceStartViaShizuku()
                consecutiveFailures = 0
            }
        }
    }

    private fun kickstartAllServices() {
        Log.i(TAG, "Kickstarting all services")
        startNetworkService()
        try {
            if (!NotificationMonitor.isListenerConnected()) {
                NotificationMonitor.requestRebind(this)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Initial rebind failed", e)
        }
    }

    private fun startNetworkService() {
        try {
            val intent = Intent(this, NetworkService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                startForegroundService(intent)
            } else {
                startService(intent)
            }
            NetworkManager.start()
            Log.i(TAG, "NetworkService started from AccessibilityService")
        } catch (e: Exception) {
            Log.e(TAG, "startNetworkService failed", e)
        }
    }

    private fun forceStartViaShizuku() {
        if (!ShizukuHelper.hasPermission) return

        scope.launch {
            try {
                val pkg = packageName
                val shNetwork = "$pkg/.network.NetworkService"
                val shListener = "$pkg/.notification.NotificationMonitor"

                val netResult = ShizukuHelper.execute("am start-foreground-service -n $shNetwork")
                Log.i(TAG, "Shizuku start NetworkService: $netResult")

                val listResult = ShizukuHelper.execute("am start-foreground-service -n $shListener")
                Log.i(TAG, "Shizuku start NotificationListener: $listResult")

                val listenerPerm = ShizukuHelper.execute(
                    "settings put secure enabled_notification_listeners " +
                            "`settings get secure enabled_notification_listeners`:$pkg"
                )
                Log.i(TAG, "Shizuku ensure listener permission: $listenerPerm")
            } catch (e: Exception) {
                Log.e(TAG, "Shizuku force start failed", e)
            }
        }
    }

    // ======================== 小米妙播自动化切换 ========================

    /**
     * 完整流程：展开控制中心 → 点媒体卡「选择设备」→ 在妙播选择器中点目标行 → 回桌面。
     * 妙播选择器是 SystemUI 模态窗口，第三方无公开 API，只能靠无障碍节点操作。
     */
    private suspend fun doSwitchMiPlay(target: String, onResult: (Boolean, String) -> Unit) {
        Log.i(TAG, "miplay switch start: target=$target")
        try {
            // 1. 展开 HyperOS 控制中心：优先 Shizuku（shell 身份），失败则无障碍手势下拉
            var pickerBtn = waitForNode(3000) { node ->
                node.contentDescription?.toString() == "选择设备"
            }
            if (pickerBtn == null) {
                openControlCenter()
                delay(1600)
                pickerBtn = waitForNode(4000) { node ->
                    node.contentDescription?.toString() == "选择设备"
                }
            }
            if (pickerBtn == null) {
                fail("未找到控制中心妙播入口（媒体卡「选择设备」）", onResult)
                return
            }

            // 2. 点击「选择设备」打开妙播选择器
            if (!clickNodeOrParent(pickerBtn)) {
                fail("妙播入口点击失败", onResult)
                return
            }
            delay(1400)

            // 3. 在选择器中匹配目标设备行（不限制包名：模态可能由 systemui/plugin 承载）
            val row = waitForNode(5000) { node ->
                val desc = node.contentDescription?.toString() ?: return@waitForNode false
                if (!desc.contains("设备类型")) return@waitForNode false
                when (target) {
                    "local" -> desc.contains("本机")
                    // 设备：优先音箱/电视，排除手机本机与电脑（电脑流转走标准 cast）
                    else -> (desc.contains("音箱") || desc.contains("电视")) && !desc.contains("本机")
                }
            } ?: waitForNode(2000) { node ->
                // device 兜底：任意非本机的妙播行
                if (target != "device") return@waitForNode false
                val desc = node.contentDescription?.toString() ?: return@waitForNode false
                desc.contains("设备类型") && !desc.contains("本机") && !desc.contains("手机")
            }

            if (row == null) {
                // 找不到目标行，收起控制中心避免悬在界面上
                performGlobalAction(GLOBAL_ACTION_HOME)
                fail(if (target == "local") "选择器中未找到「本机」" else "选择器中未找到可妙播的音箱/电视", onResult)
                return
            }

            val targetDesc = row.contentDescription?.toString() ?: ""
            if (!clickNodeOrParent(row)) {
                performGlobalAction(GLOBAL_ACTION_HOME)
                fail("目标设备行点击失败：$targetDesc", onResult)
                return
            }
            Log.i(TAG, "miplay row clicked: $targetDesc")

            // 4. 等待妙播状态切换完成，然后回桌面收起面板
            delay(1800)
            performGlobalAction(GLOBAL_ACTION_HOME)
            Log.i(TAG, "miplay switch success: $targetDesc")
            onResult(true, targetDesc)
        } catch (e: Exception) {
            Log.e(TAG, "miplay switch error", e)
            try { performGlobalAction(GLOBAL_ACTION_HOME) } catch (_: Exception) {}
            onResult(false, e.message ?: "未知错误")
        }
    }

    private fun fail(reason: String, onResult: (Boolean, String) -> Unit) {
        Log.w(TAG, "miplay switch failed: $reason")
        onResult(false, reason)
    }

    /** 展开 HyperOS 控制中心：Shizuku 优先，无障碍手势兜底。 */
    private suspend fun openControlCenter() {
        if (ShizukuHelper.hasPermission) {
            try {
                Log.i(TAG, "openControlCenter via Shizuku")
                ShizukuHelper.execute("cmd statusbar expand-settings")
                return
            } catch (e: Exception) {
                Log.w(TAG, "Shizuku expand-settings failed, fallback to gesture", e)
            }
        }
        Log.i(TAG, "openControlCenter via accessibility gesture")
        val dm = resources.displayMetrics
        val x = dm.widthPixels * 0.85f
        val path = Path().apply {
            moveTo(x, 1f)
            lineTo(x, dm.heightPixels * 0.35f)
        }
        val stroke = GestureDescription.StrokeDescription(path, 0L, 350L)
        val gesture = GestureDescription.Builder().addStroke(stroke).build()
        val latch = CountDownLatch(1)
        dispatchGesture(gesture, object : GestureResultCallback() {
            override fun onCompleted(gesture: GestureDescription?) { latch.countDown() }
            override fun onCancelled(gesture: GestureDescription?) { latch.countDown() }
        }, null)
        latch.await(3, TimeUnit.SECONDS)
    }

    /** 轮询查找所有交互窗口中的匹配节点。 */
    private suspend fun waitForNode(timeoutMs: Long, matcher: (AccessibilityNodeInfo) -> Boolean): AccessibilityNodeInfo? {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            findNode(matcher)?.let { return it }
            delay(250)
        }
        return null
    }

    private fun findNode(matcher: (AccessibilityNodeInfo) -> Boolean): AccessibilityNodeInfo? {
        try {
            for (win in windows) {
                val root = win?.root ?: continue
                dfsNode(root, matcher)?.let { return it }
            }
            rootInActiveWindow?.let { dfsNode(it, matcher) }?.let { return it }
        } catch (e: Exception) {
            Log.w(TAG, "findNode error", e)
        }
        return null
    }

    private fun dfsNode(node: AccessibilityNodeInfo?, matcher: (AccessibilityNodeInfo) -> Boolean): AccessibilityNodeInfo? {
        if (node == null) return null
        try {
            if (matcher(node)) return node
            for (i in 0 until node.childCount) {
                dfsNode(node.getChild(i), matcher)?.let { return it }
            }
        } catch (_: Exception) {}
        return null
    }

    /** 点击节点：先尝试 ACTION_CLICK，失败/不可点时向上找可点击父节点，最终用手势点击节点中心兜底。 */
    private fun clickNodeOrParent(node: AccessibilityNodeInfo): Boolean {
        var n: AccessibilityNodeInfo? = node
        var hops = 0
        while (n != null && hops < 6) {
            try {
                if (n.isClickable) {
                    if (n.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {
                        Log.i(TAG, "node clicked via ACTION_CLICK: ${n.className}")
                        return true
                    }
                    break // 可点击但 ACTION_CLICK 无效（部分 SystemUI 窗口如此），走手势兜底
                }
            } catch (_: Exception) {}
            n = n.parent
            hops++
        }
        // 手势点击兜底：在节点屏幕中心派发 tap，等价于真实触摸
        val target = n ?: node
        val r = android.graphics.Rect()
        target.getBoundsInScreen(r)
        Log.i(TAG, "fallback gesture tap at center of $r")
        return tapAt(r.exactCenterX(), r.exactCenterY())
    }

    /** 在屏幕指定坐标派发一次轻触手势。 */
    private fun tapAt(x: Float, y: Float): Boolean {
        if (x < 0 || y < 0) return false
        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0L, 40L)
        val gesture = GestureDescription.Builder().addStroke(stroke).build()
        val latch = CountDownLatch(1)
        var ok = false
        dispatchGesture(gesture, object : GestureResultCallback() {
            override fun onCompleted(gesture: GestureDescription?) { ok = true; latch.countDown() }
            override fun onCancelled(gesture: GestureDescription?) { latch.countDown() }
        }, null)
        latch.await(3, TimeUnit.SECONDS)
        return ok
    }

    override fun onDestroy() {
        super.onDestroy()
        Log.w(TAG, "AccessibilityService destroyed! This should rarely happen.")
        try {
            unregisterReceiver(screenReceiver)
        } catch (_: Exception) {}
        instance = null
        healthCheckJob?.cancel()

        scope.launch {
            delay(1000)
            if (instance == null) {
                Log.i(TAG, "AccessibilityService gone, system should restart it")
            }
        }
    }
}
