package com.mitoast.shizuku

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import rikka.shizuku.Shizuku

object ShizukuHelper {

    private const val TAG = "MiToastShizuku"
    private const val SHIZUKU_REQUEST_CODE = 10086

    /** Shizuku 已安装且服务正在运行（binder 可 ping 通） */
    val isRunning: Boolean get() {
        return try {
            Shizuku.pingBinder()
        } catch (e: Exception) { false }
    }

    val isAvailable: Boolean get() {
        return try {
            Shizuku.pingBinder() && Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED
        } catch (e: Exception) { false }
    }

    val hasPermission: Boolean get() {
        return try {
            Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED
        } catch (e: Exception) { false }
    }

    /** Shizuku 管理器官方包名（旧版/衍生版包名作为兜底） */
    private val SHIZUKU_PACKAGES = arrayOf(
        "moe.shizuku.privileged.api",
        "moe.rikka.shizuku"
    )

    /** 设备上是否安装了 Shizuku 管理器 App */
    fun isShizukuInstalled(context: Context): Boolean {
        return SHIZUKU_PACKAGES.any { pkg ->
            try {
                context.packageManager.getPackageInfo(pkg, 0)
                true
            } catch (e: Exception) {
                false
            }
        }
    }

    private val permissionListeners = mutableListOf<(Boolean) -> Unit>()

    private val listener = Shizuku.OnRequestPermissionResultListener { requestCode, grantResult ->
        if (requestCode == SHIZUKU_REQUEST_CODE) {
            val granted = grantResult == PackageManager.PERMISSION_GRANTED
            Log.i(TAG, "Shizuku permission result: granted=$granted")
            permissionListeners.forEach { it(granted) }
            permissionListeners.clear()
        }
    }

    fun addListener() {
        try {
            Shizuku.addRequestPermissionResultListener(listener)
            Log.i(TAG, "Shizuku listener registered")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to add Shizuku listener", e)
        }
    }

    fun requestPermission(context: Context, callback: (Boolean) -> Unit) {
        addListener()
        permissionListeners.add(callback)

        try {
            if (Shizuku.isPreV11()) {
                Log.i(TAG, "Shizuku pre-v11, requesting directly")
                Shizuku.requestPermission(SHIZUKU_REQUEST_CODE)
            } else {
                val myUid = android.os.Process.myUid()
                Log.i(TAG, "Shizuku v11+, requesting for uid=$myUid")
                Shizuku.requestPermission(SHIZUKU_REQUEST_CODE)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Shizuku request failed", e)
            callback(false)
        }
    }

    suspend fun execute(command: String): String = withContext(Dispatchers.IO) {
        try {
            if (!isAvailable) return@withContext "Shizuku not available"

            // Shizuku 13.x 中 newProcess 为私有 API，通过反射调用（运行时由 Shizuku 服务端实现）
            val newProcess = Shizuku::class.java.getDeclaredMethod(
                "newProcess",
                Array<String>::class.java,
                Array<String>::class.java,
                String::class.java
            ).apply { isAccessible = true }
            val process = newProcess.invoke(
                null, arrayOf("sh", "-c", command), null, null
            ) as Process

            val stdout = process.inputStream.bufferedReader().readText()
            val stderr = process.errorStream.bufferedReader().readText()
            val exitCode = process.waitFor()

            val output = stdout.trim()
            val err = stderr.trim()
            Log.d(TAG, "sh '$command' -> exit=$exitCode out='$output' err='$err'")

            if (exitCode == 0) output.ifEmpty { "OK" }
            else "ERROR($exitCode): ${err.ifEmpty { output }}"
        } catch (e: Exception) {
            Log.e(TAG, "execute failed: $command", e)
            "EXCEPTION: ${e.message}"
        }
    }

    fun openShizukuApp(context: Context) {
        try {
            val intent = SHIZUKU_PACKAGES
                .mapNotNull { context.packageManager.getLaunchIntentForPackage(it) }
                .firstOrNull()
                ?: Intent(Intent.ACTION_VIEW, android.net.Uri.parse("https://github.com/RikkaApps/Shizuku"))
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            context.startActivity(intent)
        } catch (e: Exception) {
            Log.e(TAG, "openShizukuApp failed", e)
        }
    }
}
