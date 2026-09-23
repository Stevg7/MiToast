package com.mitoast

import android.app.Application
import android.content.Intent
import android.os.Build
import android.util.Log
import com.mitoast.network.NetworkManager
import com.mitoast.network.NetworkService
import com.mitoast.notification.NotificationMonitor

class MiToastApp : Application() {

    companion object {
        lateinit var instance: MiToastApp
            private set
        private const val TAG = "MiToastApp"
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
        NetworkManager.init(this)
        // 本地历史通知存储（离线期间记录，电脑端重连后增量同步）
        com.mitoast.history.HistoryStore.init(this)
        // 妙播：启动 MediaRouter2 路由扫描（API 30+ 生效，低版本自动降级）
        try {
            com.mitoast.media.CastRouteManager.init(this)
            com.mitoast.media.CastRouteManager.start()
        } catch (e: Exception) {
            Log.e(TAG, "CastRouteManager init failed", e)
        }
        Log.i(TAG, "Application created, instance ready")
        // 进程启动后主动确保服务存活：MIUI 上应用被强停/更新后，系统不会自动重新
        // 绑定 NotificationListenerService，需要应用主动 requestRebind。
        ensureServicesAlive()
    }

    override fun onTerminate() {
        super.onTerminate()
        Log.w(TAG, "Application terminated")
    }

    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        when (level) {
            TRIM_MEMORY_UI_HIDDEN, TRIM_MEMORY_MODERATE -> {
                Log.w(TAG, "onTrimMemory level=$level, services may be killed soon")
                ensureServicesAlive()
            }
            TRIM_MEMORY_COMPLETE -> {
                Log.e(TAG, "onTrimMemory COMPLETE, system may kill us")
                ensureServicesAlive()
            }
        }
    }

    private fun ensureServicesAlive() {
        try {
            if (!NotificationMonitor.isListenerConnected()) {
                Log.i(TAG, "Listener not connected, requesting rebind")
                NotificationMonitor.requestRebind(this)
            }

            if (!NetworkManager.isRunning) {
                Log.i(TAG, "Network service not running, starting")
                startNetworkService()
            }
        } catch (e: Exception) {
            Log.e(TAG, "ensureServicesAlive failed", e)
        }
    }

    fun startNetworkService() {
        val intent = Intent(this, NetworkService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
    }
}
