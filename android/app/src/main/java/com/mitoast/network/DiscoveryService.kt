package com.mitoast.network

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

class DiscoveryService(
    private val wsPort: Int,
    private val discoveryPort: Int = 9000
) {
    companion object {
        private const val TAG = "MiToastDiscovery"
        private const val DISCOVERY_REQUEST = "MTOAST_DISCOVER_REQUEST"
        private const val DISCOVERY_RESPONSE = "MTOAST_DISCOVER_RESPONSE"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var running = false
    private var socket: DatagramSocket? = null

    fun start() {
        if (running) return
        running = true
        scope.launch {
            while (running) {
                try {
                    socket = DatagramSocket(discoveryPort)
                    socket?.soTimeout = 5000
                    Log.d(TAG, "Discovery service started on port $discoveryPort")
                    listenLoop()
                } catch (e: Exception) {
                    // 端口被旧实例占用等场景：关闭旧 socket 后 5 秒重试，不永久退出
                    Log.e(TAG, "Discovery start failed, retrying in 5s", e)
                    try { socket?.close() } catch (_: Exception) {}
                    socket = null
                    kotlinx.coroutines.delay(5000)
                }
            }
        }
    }

    fun stop() {
        running = false
        try {
            socket?.close()
        } catch (_: Exception) {}
        socket = null
    }

    private suspend fun listenLoop() = withContext(Dispatchers.IO) {
        val buffer = ByteArray(1024)
        while (running) {
            try {
                val packet = DatagramPacket(buffer, buffer.size)
                socket?.receive(packet)
                val received = String(packet.data, 0, packet.length)
                if (received.startsWith(DISCOVERY_REQUEST)) {
                    val clientAddress = packet.address
                    val response = "$DISCOVERY_RESPONSE|${getLocalIpAddress()}|$wsPort"
                    val responseBytes = response.toByteArray()
                    val responsePacket = DatagramPacket(
                        responseBytes, responseBytes.size,
                        clientAddress, packet.port
                    )
                    socket?.send(responsePacket)
                    Log.d(TAG, "Responded to discovery from ${clientAddress.hostAddress}")
                }
            } catch (_: SocketTimeoutException) {
                continue
            } catch (e: Exception) {
                if (running) Log.e(TAG, "Discovery receive error", e)
            }
        }
    }

    private fun getLocalIpAddress(): String {
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val iface = interfaces.nextElement()
                val addresses = iface.inetAddresses
                while (addresses.hasMoreElements()) {
                    val addr = addresses.nextElement()
                    if (!addr.isLoopbackAddress && addr.hostAddress.contains('.')) {
                        return addr.hostAddress
                    }
                }
            }
        } catch (_: Exception) {}
        return "127.0.0.1"
    }
}
