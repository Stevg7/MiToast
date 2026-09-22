package com.mitoast.network

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.util.Log
import kotlinx.coroutines.launch

class NetworkService : Service() {

    companion object {
        private const val TAG = "MiToastNetSvc"
        private const val CHANNEL_ID = "mitoast_network"
        private const val NOTIFICATION_ID = 1001
    }

    private val dndScope = kotlinx.coroutines.CoroutineScope(
        kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.IO)
    private var lastDndEnabled: Boolean? = null
    private var dndJob: kotlinx.coroutines.Job? = null

    // 锁屏后 Wi-Fi 芯片可能进入省电/休眠，导致 WebSocket 长连接发不出数据、
    // UDP 设备发现广播被过滤。服务运行期间持有高性能 Wi-Fi 锁与组播锁。
    private var wifiLock: WifiManager.WifiLock? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    override fun onCreate() {
        super.onCreate()
        Log.i(TAG, "NetworkService created")
        createNotificationChannel()
        val notification = buildNotification()

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
            } else {
                @Suppress("DEPRECATION")
                startForeground(NOTIFICATION_ID, notification)
            }
            Log.i(TAG, "NetworkService started as foreground")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start foreground", e)
        }

        acquireWifiLocks()
        NetworkManager.start()
        startDndMonitor()
    }

    private fun acquireWifiLocks() {
        try {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                WifiManager.WIFI_MODE_FULL_HIGH_PERF
            } else {
                @Suppress("DEPRECATION")
                WifiManager.WIFI_MODE_FULL
            }
            wifiLock = wifi.createWifiLock(mode, "MiToast:net-wifi").apply {
                setReferenceCounted(false)
                acquire()
            }
            multicastLock = wifi.createMulticastLock("MiToast:net-mcast").apply {
                setReferenceCounted(false)
                acquire()
            }
            Log.i(TAG, "WifiLock + MulticastLock acquired")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to acquire wifi locks", e)
        }
    }

    private fun releaseWifiLocks() {
        try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        try { multicastLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        wifiLock = null
        multicastLock = null
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        NetworkManager.start()
        return START_STICKY
    }

    override fun onDestroy() {
        super.onDestroy()
        dndJob?.cancel()
        releaseWifiLocks()
        NetworkManager.stop()
        Log.i(TAG, "NetworkService destroyed")
    }

    private fun startDndMonitor() {
        dndJob?.cancel()
        dndJob = dndScope.launch {
            while (true) {
                try {
                    val enabled = isDndEnabled()
                    if (enabled != lastDndEnabled) {
                        lastDndEnabled = enabled
                        NetworkManager.broadcastDndStatus(enabled)
                    }
                } catch (_: Exception) {}
                kotlinx.coroutines.delay(30_000)
            }
        }
    }

    private fun isDndEnabled(): Boolean {
        val nm = getSystemService(NOTIFICATION_SERVICE) as android.app.NotificationManager
        val filter = nm.currentInterruptionFilter
        return filter != android.app.NotificationManager.INTERRUPTION_FILTER_ALL &&
               filter != android.app.NotificationManager.INTERRUPTION_FILTER_UNKNOWN
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "MiToast 网络服务",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "保持 MiToast 网络转发服务运行"
                setShowBadge(false)
                enableLights(false)
                enableVibration(false)
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(): Notification {
        val text = if (NetworkManager.currentIp.isNotEmpty()) {
            "转发服务运行中 · ${NetworkManager.currentIp}:8080"
        } else {
            "转发服务运行中"
        }
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, CHANNEL_ID)
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
}
