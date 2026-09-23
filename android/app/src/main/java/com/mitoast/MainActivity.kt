package com.mitoast

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Divider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.mitoast.accessibility.MiToastAccessibilityService
import com.mitoast.network.NetworkManager
import com.mitoast.network.NetworkService
import com.mitoast.notification.NotificationMonitor
import com.mitoast.prefs.AppWhitelistManager
import com.mitoast.shizuku.ShizukuHelper
import com.mitoast.ui.AppSelectActivity
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// ---------- 品牌配色 ----------
val BrandOrange = Color(0xFFFF6900)
val PageBg = Color(0xFFF5F5F7)
val TextDark = Color(0xFF1D1D1F)
val TextGray = Color(0xFF86868B)
val SuccessGreen = Color(0xFF34C759)
val DividerColor = Color(0xFFF0F0F2)

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        maybeRequestIgnoreBatteryOptimizations()
        setContent {
            MiToastTheme {
                MainScreen()
            }
        }
    }

    /**
     * 首次启动时引导用户把 MiToast 加入电池优化白名单：Doze 模式下非白名单应用的
     * 网络访问会被系统挂起（前台服务也不例外），锁屏后通知将无法实时发到电脑。
     * 只自动弹一次，后续用户可在权限设置页手动开启。
     */
    private fun maybeRequestIgnoreBatteryOptimizations() {
        try {
            val prefs = getSharedPreferences("mitoast_prefs", Context.MODE_PRIVATE)
            if (prefs.getBoolean("battery_opt_requested", false)) return
            prefs.edit().putBoolean("battery_opt_requested", true).apply()
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            if (pm.isIgnoringBatteryOptimizations(packageName)) return
            Handler(Looper.getMainLooper()).postDelayed({
                try {
                    requestIgnoreBatteryOptimizations(this)
                } catch (_: Exception) {
                }
            }, 1200)
        } catch (_: Exception) {
        }
    }
}

@Composable
fun MiToastTheme(content: @Composable () -> Unit) {
    val colorScheme = lightColorScheme(
        primary = BrandOrange,
        onPrimary = Color.White,
        secondary = BrandOrange,
        background = PageBg,
        onBackground = TextDark,
        surface = Color.White,
        onSurface = TextDark
    )
    MaterialTheme(colorScheme = colorScheme, content = content)
}

private data class UiState(
    val listenerGranted: Boolean = false,
    val listenerConnected: Boolean = false,
    val ignoringBattery: Boolean = false,
    val canPostNotifications: Boolean = true,
    val netRunning: Boolean = false,
    val ip: String = "",
    val clients: Int = 0,
    val whitelistEnabled: Boolean = false,
    val whitelistCount: Int = 0,
    val accessibilityEnabled: Boolean = false,
    val shizukuInstalled: Boolean = false,
    val shizukuRunning: Boolean = false,
    val shizukuGranted: Boolean = false,
    val isMiui: Boolean = false
)

@Composable
private fun MainScreen() {
    val context = LocalContext.current
    var state by remember { mutableStateOf(readUiState(context)) }

    // 服务重启进度：独立于 state 轮询，避免轮询刷新把进度状态覆盖掉
    val scope = rememberCoroutineScope()
    var restarting by remember { mutableStateOf(false) }
    var restartStage by remember { mutableStateOf("") }

    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) state = readUiState(context)
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    androidx.compose.runtime.LaunchedEffect(Unit) {
        while (true) {
            // 仅在界面可见时刷新；重启过程中加快轮询，让状态行及时跟上服务变化
            if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) {
                state = readUiState(context)
            }
            delay(if (restarting) 400 else 2000)
        }
    }

    val postNotificationLauncher = androidx.activity.compose.rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { _ -> state = readUiState(context) }

    // Shizuku 授权回调在 binder 线程，需切回主线程刷新 Compose 状态
    val mainHandler = remember { Handler(Looper.getMainLooper()) }

    /**
     * 重启/启动网络同步服务：先停掉旧实例，再启动并等待就绪，
     * 全程通过 restartStage 展示阶段进度（正在停止 → 正在启动 → 完成/失败）。
     */
    fun restartSyncService() {
        if (restarting) return
        scope.launch {
            restarting = true
            val wasRunning = NetworkManager.isRunning
            restartStage = if (wasRunning) "正在停止服务…" else "正在启动服务…"

            if (wasRunning) {
                context.stopService(Intent(context, NetworkService::class.java))
                // 等待 onDestroy → NetworkManager.stop() 真正退出
                var waited = 0L
                while (NetworkManager.isRunning && waited < 3000) {
                    delay(200)
                    waited += 200
                }
            }

            restartStage = "正在启动服务…"
            try {
                ContextCompat.startForegroundService(
                    context,
                    Intent(context, NetworkService::class.java)
                )
            } catch (e: Exception) {
                restartStage = "启动失败：${e.message ?: "系统拒绝了前台服务启动"}"
                state = readUiState(context)
                // 与完成提示一致，失败原因短暂停留后再收起
                delay(1600)
                restarting = false
                state = readUiState(context)
                return@launch
            }

            // 等待服务就绪（WebSocket 端口绑定完成），最多等 6 秒
            var waited = 0L
            while (!NetworkManager.isRunning && waited < 6000) {
                delay(300)
                waited += 300
            }

            restartStage = if (NetworkManager.isRunning) {
                if (wasRunning) "重启完成" else "启动完成"
            } else {
                "启动失败，请检查服务状态后重试"
            }
            state = readUiState(context)
            // 完成提示短暂停留后再收起进度条
            delay(1200)
            restarting = false
            state = readUiState(context)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(PageBg)
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // 头部
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                modifier = Modifier
                    .size(46.dp)
                    .clip(RoundedCornerShape(13.dp))
                    .background(BrandOrange),
                contentAlignment = Alignment.Center
            ) {
                Text("M", color = Color.White, fontSize = 24.sp, fontWeight = FontWeight.Bold)
            }
            Spacer(modifier = Modifier.width(14.dp))
            Column {
                Text("MiToast", fontSize = 24.sp, fontWeight = FontWeight.Bold, color = TextDark)
                Text("手机通知 · 实时同步到电脑", fontSize = 13.sp, color = TextGray)
            }
        }

        // 运行状态
        MiCard {
            Text("运行状态", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = TextDark)
            Spacer(modifier = Modifier.height(10.dp))
            StatusRow(
                "通知监听服务",
                when {
                    state.listenerConnected -> "已连接"
                    state.listenerGranted -> "已授权，等待连接"
                    else -> "未授权"
                },
                state.listenerConnected
            )
            StatusRow(
                "网络同步服务",
                when {
                    restarting -> "重启中…"
                    state.netRunning -> "运行中"
                    else -> "未运行"
                },
                state.netRunning && !restarting
            )
            StatusRow(
                "无障碍保活",
                if (state.accessibilityEnabled) "运行中" else "未开启",
                state.accessibilityEnabled
            )
            if (state.netRunning) {
                StatusRow("手机地址", state.ip.ifEmpty { "获取中…" }, state.ip.isNotEmpty(), showDot = false)
                StatusRow("已连接电脑", "${state.clients} 台", state.clients > 0, showDot = false)
            }
            // 电脑端接入配对码：在电脑端设置「连接 → 配对码」里填入，手机只接受配对码正确的连接
            StatusRow("配对码", com.mitoast.security.PairToken.get(context), true, showDot = false)
        }

        SectionHeader("权限设置")
        MiCard {
            PermissionRow(
                title = "通知使用权限",
                subtitle = "允许读取并转发通知到电脑",
                granted = state.listenerGranted
            ) {
                context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
            }
            Divider(color = DividerColor)
            PermissionRow(
                title = "无障碍保活服务",
                subtitle = "最强保活手段：服务被清理后自动重启同步",
                granted = state.accessibilityEnabled,
                grantedText = "已开启"
            ) {
                try {
                    context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                } catch (_: Exception) {
                }
            }
            Divider(color = DividerColor)
            PermissionRow(
                title = "Shizuku 授权（进阶保活）",
                subtitle = when {
                    state.shizukuGranted -> "已获得 Shizuku 授权，可强力重启服务"
                    !state.shizukuInstalled -> "未安装 Shizuku，点击查看安装方式（可选）"
                    !state.shizukuRunning -> "Shizuku 已安装但未运行，点击打开并启动"
                    else -> "Shizuku 运行中，点击授权保活"
                },
                granted = state.shizukuGranted,
                grantedText = "已授权",
                actionText = when {
                    state.shizukuGranted -> null
                    !state.shizukuInstalled -> "去安装 >"
                    !state.shizukuRunning -> "去启动 >"
                    else -> "去授权 >"
                }
            ) {
                when {
                    state.shizukuGranted -> {
                        // 已授权，点击打开 Shizuku 管理器查看状态
                        ShizukuHelper.openShizukuApp(context)
                    }
                    !state.shizukuInstalled || !state.shizukuRunning -> {
                        // 未安装或服务未运行：打开 Shizuku 管理器（未安装时跳转下载页）
                        ShizukuHelper.openShizukuApp(context)
                    }
                    else -> {
                        // Shizuku 运行中但未授权：发起授权请求
                        ShizukuHelper.requestPermission(context) {
                            mainHandler.post { state = readUiState(context) }
                        }
                    }
                }
            }
            Divider(color = DividerColor)
            PermissionRow(
                title = "后台运行（忽略电池优化）",
                subtitle = "避免锁屏 Doze 后网络被挂起、通知无法实时同步",
                granted = state.ignoringBattery
            ) {
                requestIgnoreBatteryOptimizations(context)
            }
            if (state.isMiui) {
                Divider(color = DividerColor)
                PermissionRow(
                    title = "自启动与省电策略（小米机型）",
                    subtitle = "允许自启动，并把省电策略设为「无限制」，锁屏后才不会被断网清理",
                    granted = false,
                    grantedText = "已设置",
                    actionText = "去设置 >"
                ) {
                    openMiuiAutoStartSettings(context)
                }
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                Divider(color = DividerColor)
                PermissionRow(
                    title = "显示通知",
                    subtitle = "用于前台服务保活",
                    granted = state.canPostNotifications
                ) {
                    postNotificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                }
            }
        }

        SectionHeader("同步设置")
        MiCard(modifier = Modifier.clickable {
            context.startActivity(Intent(context, AppSelectActivity::class.java))
        }) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        "通知应用白名单",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = TextDark
                    )
                    Spacer(modifier = Modifier.height(4.dp))
                    val desc = if (state.whitelistEnabled) {
                        "白名单模式 · 已选 ${state.whitelistCount} 个应用"
                    } else {
                        "当前同步全部非系统应用 · 点此自定义"
                    }
                    Text(desc, fontSize = 13.sp, color = TextGray)
                }
                Text(">", fontSize = 20.sp, color = TextGray)
            }
        }

        MiCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        "同步服务",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = TextDark
                    )
                    Text("开启局域网 WebSocket 与设备发现", fontSize = 13.sp, color = TextGray)
                }
                Button(
                    onClick = { restartSyncService() },
                    enabled = !restarting,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = BrandOrange,
                        contentColor = Color.White
                    )
                ) {
                    Text(
                        when {
                            restarting -> "重启中…"
                            state.netRunning -> "重启服务"
                            else -> "启动服务"
                        }
                    )
                }
            }
            if (restarting) {
                Spacer(modifier = Modifier.height(14.dp))
                LinearProgressIndicator(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(5.dp)
                        .clip(RoundedCornerShape(3.dp)),
                    color = BrandOrange,
                    trackColor = DividerColor
                )
                Spacer(modifier = Modifier.height(8.dp))
                Text(restartStage, fontSize = 12.sp, color = TextGray)
            }
        }

        Spacer(modifier = Modifier.height(30.dp))
    }
}

@Composable
private fun MiCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(20.dp),
        color = Color.White
    ) {
        Column(modifier = Modifier.padding(18.dp), content = content)
    }
}

@Composable
private fun SectionHeader(title: String) {
    Text(
        title,
        fontSize = 14.sp,
        fontWeight = FontWeight.SemiBold,
        color = TextGray,
        modifier = Modifier.padding(start = 4.dp)
    )
}

@Composable
private fun StatusRow(label: String, value: String, ok: Boolean, showDot: Boolean = true) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 5.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, fontSize = 14.sp, color = TextDark)
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (showDot) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .clip(CircleShape)
                        .background(if (ok) SuccessGreen else Color(0xFFE5E5EA))
                )
                Spacer(modifier = Modifier.width(8.dp))
            }
            Text(
                value,
                fontSize = 13.sp,
                color = if (ok) TextDark else TextGray,
                fontWeight = if (ok) FontWeight.Medium else FontWeight.Normal
            )
        }
    }
}

@Composable
private fun PermissionRow(
    title: String,
    subtitle: String,
    granted: Boolean,
    grantedText: String = "已开启",
    actionText: String? = null,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = TextDark)
            Text(subtitle, fontSize = 12.sp, color = TextGray)
        }
        if (granted) {
            Text(grantedText, fontSize = 13.sp, color = SuccessGreen, fontWeight = FontWeight.Medium)
        } else {
            Text(
                actionText ?: "去设置 >",
                fontSize = 13.sp,
                color = BrandOrange,
                fontWeight = FontWeight.Medium
            )
        }
    }
}

// ---------- 状态读取 ----------

private fun readUiState(context: Context): UiState {
    return UiState(
        listenerGranted = isNotificationListenerEnabled(context),
        listenerConnected = NotificationMonitor.isListenerConnected(),
        ignoringBattery = isIgnoringBatteryOptimizations(context),
        canPostNotifications = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.POST_NOTIFICATIONS
            ) == PackageManager.PERMISSION_GRANTED
        } else true,
        netRunning = NetworkManager.isRunning,
        ip = NetworkManager.currentIp,
        clients = NetworkManager.connectedClients,
        whitelistEnabled = AppWhitelistManager.isWhitelistEnabled(context),
        whitelistCount = AppWhitelistManager.getWhitelist(context).size,
        accessibilityEnabled = MiToastAccessibilityService.isAccessibilityEnabled(context),
        shizukuInstalled = ShizukuHelper.isShizukuInstalled(context),
        shizukuRunning = ShizukuHelper.isRunning,
        shizukuGranted = ShizukuHelper.hasPermission,
        isMiui = isMiuiRom()
    )
}

/** 检测是否为 MIUI/HyperOS（小米/红米机型），用于显示自启动引导。 */
private fun isMiuiRom(): Boolean {
    return try {
        val cl = Class.forName("android.os.SystemProperties")
        val getMethod = cl.getMethod("get", String::class.java)
        val version = getMethod.invoke(null, "ro.miui.ui.version.name") as? String
        version != null && version.isNotEmpty()
    } catch (_: Exception) {
        false
    }
}

/**
 * 打开 MIUI/HyperOS 的自启动管理页；不同系统版本组件名可能变化，
 * 依次尝试常见入口，最终回退到本应用的系统详情页（可在其中设置省电策略）。
 */
private fun openMiuiAutoStartSettings(context: Context) {
    val intents = listOf(
        Intent().setComponent(
            ComponentName(
                "com.miui.securitycenter",
                "com.miui.permcenter.autostart.AutoStartManagementActivity"
            )
        ),
        Intent("miui.intent.action.OP_AUTO_START").addCategory(Intent.CATEGORY_DEFAULT),
        Intent(
            Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
            Uri.parse("package:${context.packageName}")
        )
    )
    for (intent in intents) {
        try {
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            context.startActivity(intent)
            return
        } catch (_: Exception) {
        }
    }
}

private fun isNotificationListenerEnabled(context: Context): Boolean {
    val flat = Settings.Secure.getString(
        context.contentResolver,
        "enabled_notification_listeners"
    ) ?: return false
    return flat.contains(context.packageName)
}

private fun isIgnoringBatteryOptimizations(context: Context): Boolean {
    val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
    return pm.isIgnoringBatteryOptimizations(context.packageName)
}

private fun requestIgnoreBatteryOptimizations(context: Context) {
    try {
        val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
            .setData(Uri.parse("package:${context.packageName}"))
        context.startActivity(intent)
    } catch (_: Exception) {
        try {
            context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        } catch (_: Exception) {
        }
    }
}
