package com.mitoast.receiver

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.util.Log
import com.mitoast.network.NetworkManager

class NetworkStateReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ConnectivityManager.CONNECTIVITY_ACTION) {
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val networkInfo = cm.activeNetworkInfo
            val isConnected = networkInfo != null && networkInfo.isConnected

            Log.i(TAG, "Network state changed, connected=$isConnected")

            if (isConnected) {
                NetworkManager.updateStatus()
            }
        }
    }

    companion object {
        private const val TAG = "MiToastNet"
    }
}
