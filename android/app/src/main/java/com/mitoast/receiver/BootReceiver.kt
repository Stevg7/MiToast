package com.mitoast.receiver

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import com.mitoast.network.NetworkService
import com.mitoast.notification.NotificationMonitor

class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        Log.i(TAG, "Received broadcast: ${intent.action}")
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED,
            Intent.ACTION_LOCKED_BOOT_COMPLETED,
            "android.intent.action.QUICKBOOT_POWERON",
            "com.htc.intent.action.QUICKBOOT_POWERON",
            "android.intent.action.REBOOT",
            Intent.ACTION_USER_PRESENT -> {
                startServices(context)
            }
        }
    }

    private fun startServices(context: Context) {
        val netIntent = Intent(context, NetworkService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(netIntent)
        } else {
            context.startService(netIntent)
        }

        Log.i(TAG, "Boot services started")
    }

    companion object {
        private const val TAG = "MiToastBoot"
    }
}
