package com.mitoast.network

import android.util.Log
import com.mitoast.model.ClearNotificationMessage
import com.mitoast.model.NotificationMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.*

class MiToastWebSocketServer(port: Int, private val authToken: String) : org.java_websocket.server.WebSocketServer(
    java.net.InetSocketAddress(port)
) {

    companion object {
        private const val TAG = "MiToastWS"
        private const val AUTH_TIMEOUT_MS = 10_000L
        val json = kotlinx.serialization.json.Json {
            ignoreUnknownKeys = true
            encodeDefaults = true
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val connectedClients = java.util.concurrent.CopyOnWriteArrayList<org.java_websocket.WebSocket>()

    /** 已完成配对码认证的客户端。 */
    private val authedClients = java.util.concurrent.ConcurrentHashMap<org.java_websocket.WebSocket, Boolean>()

    /**
     * 端口是否已成功绑定。绑定失败（BindException）时服务线程会永久退出，
     * 由 NetworkManager 看门狗检测到 false 后重建实例重试。
     */
    @Volatile
    var portReady: Boolean = false
        private set

    override fun onOpen(conn: org.java_websocket.WebSocket?, handshake: org.java_websocket.handshake.ClientHandshake?) {
        conn ?: return
        connectedClients.add(conn)
        Log.d(TAG, "Client connected: ${conn.remoteSocketAddress}")

        // 接入认证：连接建立后 10 秒内必须发来正确配对码，否则掐断
        scope.launch {
            kotlinx.coroutines.delay(AUTH_TIMEOUT_MS)
            if (!authedClients.containsKey(conn)) {
                Log.w(TAG, "Client auth timeout, closing: ${conn.remoteSocketAddress}")
                try { conn.close(4001, "auth required") } catch (_: Exception) {}
            }
        }

        val ping = com.mitoast.model.PingMessage(
            deviceName = android.os.Build.MODEL,
            deviceModel = android.os.Build.MODEL
        )
        try { conn.send(json.encodeToString(ping)) } catch (_: Exception) {}
    }

    override fun onClose(conn: org.java_websocket.WebSocket?, code: Int, reason: String?, remote: Boolean) {
        conn ?: return
        connectedClients.remove(conn)
        authedClients.remove(conn)
        Log.d(TAG, "Client disconnected: ${conn.remoteSocketAddress} code=$code reason=$reason")
    }

    override fun onMessage(conn: org.java_websocket.WebSocket?, message: String?) {
        message ?: return
        conn ?: return
        Log.d(TAG, "Received from ${conn.remoteSocketAddress}: $message")
        try {
            val obj = kotlinx.serialization.json.Json.parseToJsonElement(message).jsonObject

            // 未认证连接：只接受配对码消息，其余一律拒绝
            if (!authedClients.containsKey(conn)) {
                if (obj["type"]?.jsonPrimitive?.contentOrNull == "auth" &&
                    com.mitoast.security.PairToken.matches(
                        com.mitoast.MiToastApp.instance,
                        obj["token"]?.jsonPrimitive?.contentOrNull
                    )
                ) {
                    authedClients[conn] = true
                    Log.i(TAG, "Client authenticated: ${conn.remoteSocketAddress}")
                } else {
                    Log.w(TAG, "Client auth failed, closing: ${conn.remoteSocketAddress}")
                    try { conn.close(4001, "auth required") } catch (_: Exception) {}
                }
                return
            }

            when (obj["type"]?.jsonPrimitive?.contentOrNull) {
                "open_app" -> {
                    val pkg = obj["packageName"]?.jsonPrimitive?.contentOrNull ?: return
                    NetworkManager.executeOpenApp(pkg)
                }
                "media_action" -> {
                    val key = obj["key"]?.jsonPrimitive?.contentOrNull ?: return
                    val idx = obj["actionIndex"]?.jsonPrimitive?.intOrNull ?: return
                    com.mitoast.notification.NotificationMonitor.getInstance()?.executeMediaAction(key, idx)
                }
                "cast_query" -> {
                    scope.launch {
                        broadcastPayload(json.encodeToString(buildCastDevicesMessage()))
                    }
                }
                "cast_transfer" -> {
                    val routeId = obj["routeId"]?.jsonPrimitive?.contentOrNull ?: return
                    scope.launch {
                        val ok = com.mitoast.media.CastRouteManager.transfer(routeId)
                        Log.d(TAG, "cast_transfer -> $routeId ok=$ok")
                        // 流转后稍等路由状态刷新，再把最新设备列表（含选中态/妙播状态）推给 PC
                        kotlinx.coroutines.delay(600)
                        broadcastPayload(json.encodeToString(buildCastDevicesMessage()))
                    }
                }
                "miplay_switch" -> {
                    // PC 端请求切换小米妙播：target="local" 切回本机；"device" 投放到音箱/电视
                    val target = obj["target"]?.jsonPrimitive?.contentOrNull ?: "local"
                    Log.d(TAG, "miplay_switch -> $target")
                    com.mitoast.accessibility.MiToastAccessibilityService.switchMiPlay(target) { ok, detail ->
                        Log.i(TAG, "miplay_switch result ok=$ok detail=$detail")
                        // 切换后妙播状态键会变化（observer 会推送一次），这里兜底再推一次最新状态
                        scope.launch {
                            kotlinx.coroutines.delay(1200)
                            broadcastPayload(json.encodeToString(buildCastDevicesMessage()))
                        }
                    }
                }
                "history_sync_request" -> {
                    // Windows 端请求同步离线期间的通知历史：
                    // since 为 Windows 端历史最新时间戳，按时间升序返回最多 2000 条，
                    // Windows 端满批次会继续用新游标请求下一批，直至追平。
                    val since = obj["since"]?.jsonPrimitive?.longOrNull ?: 0L
                    Log.d(TAG, "history_sync_request since=$since")
                    try {
                        val entries = com.mitoast.history.HistoryStore.entriesSince(since, 2000)
                        Log.i(TAG, "history_sync -> ${entries.size} entries since=$since")
                        conn?.send(json.encodeToString(com.mitoast.model.HistorySyncMessage(notifications = entries)))
                    } catch (e: Exception) {
                        Log.e(TAG, "history_sync send failed", e)
                    }
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "onMessage parse error", e)
        }
    }

    override fun onError(conn: org.java_websocket.WebSocket?, ex: Exception?) {
        Log.e(TAG, "WebSocket error", ex)
        // conn == null 表示服务端级错误（如端口绑定失败），标记为未就绪供看门狗重建
        if (conn == null) portReady = false
    }

    override fun onStart() {
        portReady = true
        Log.d(TAG, "WebSocket server started on port $port")
    }

    fun broadcastNotification(message: NotificationMessage) {
        scope.launch {
            val jsonStr = json.encodeToString(message)
            val dead = mutableListOf<org.java_websocket.WebSocket>()
            for (client in connectedClients) {
                try {
                    if (client.isOpen) client.send(jsonStr) else dead.add(client)
                } catch (e: Exception) {
                    Log.e(TAG, "Failed to send to client", e)
                    dead.add(client)
                }
            }
            dead.forEach { connectedClients.remove(it) }
        }
    }

    fun broadcastClear(message: ClearNotificationMessage) {
        scope.launch {
            val jsonStr = json.encodeToString(message)
            val dead = mutableListOf<org.java_websocket.WebSocket>()
            for (client in connectedClients) {
                try {
                    if (client.isOpen) client.send(jsonStr) else dead.add(client)
                } catch (e: Exception) {
                    Log.e(TAG, "Failed to send clear to client", e)
                    dead.add(client)
                }
            }
            dead.forEach { connectedClients.remove(it) }
        }
    }

    fun broadcastDndStatus(enabled: Boolean) {
        scope.launch {
            val jsonStr = "{\"type\":\"dnd_status\",\"enabled\":$enabled}"
            val dead = mutableListOf<org.java_websocket.WebSocket>()
            for (client in connectedClients) {
                try { if (client.isOpen) client.send(jsonStr) else dead.add(client) }
                catch (e: Exception) { dead.add(client) }
            }
            dead.forEach { connectedClients.remove(it) }
        }
    }

    /** 主动广播妙播设备列表（路由/妙播状态变化时由 NetworkManager 调用）。 */
    fun broadcastCastDevices() {
        scope.launch {
            broadcastPayload(json.encodeToString(buildCastDevicesMessage()))
        }
    }

    /** 组装妙播设备消息：标准 MediaRouter2 路由 + HyperOS 小米妙播生态状态。 */
    private fun buildCastDevicesMessage(): com.mitoast.model.CastDevicesMessage {
        val mgr = com.mitoast.media.CastRouteManager
        val supported = mgr.miplaySupported()
        return com.mitoast.model.CastDevicesMessage(
            devices = mgr.getDevices(),
            miplaySupported = supported,
            miplayCasting = mgr.miplayCasting(),
            carCasting = mgr.carCasting(),
            miplayControllable = supported &&
                    com.mitoast.accessibility.MiToastAccessibilityService.isRunning()
        )
    }

    /** 向所有已连接客户端广播原始 JSON（妙播设备列表等）。 */
    private fun broadcastPayload(jsonStr: String) {
        val dead = mutableListOf<org.java_websocket.WebSocket>()
        for (client in connectedClients) {
            try {
                if (client.isOpen) client.send(jsonStr) else dead.add(client)
            } catch (e: Exception) {
                Log.e(TAG, "Failed to send payload to client", e)
                dead.add(client)
            }
        }
        dead.forEach { connectedClients.remove(it) }
    }

    fun getConnectedCount(): Int = connectedClients.size
}
