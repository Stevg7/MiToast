package com.mitoast.media

import android.content.Context
import android.database.ContentObserver
import android.media.MediaRoute2Info
import android.media.MediaRouter2
import android.media.RouteDiscoveryPreference
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import com.mitoast.model.CastDevice
import java.util.concurrent.Executors

/**
 * 妙播（MediaRouter2）路由管理：
 * - 扫描系统可投放设备（蓝牙、Cast/投屏设备、智能音箱等）
 * - 提供设备列表给 PC 端展示
 * - PC 端选择设备后调用 transferTo 将音乐流转过去
 *
 * MediaRouter2 / MediaRoute2Info 需要 Android R（API 30）+；低版本仅返回"本机"。
 */
object CastRouteManager {

    private const val TAG = "CastRouteManager"
    private const val LOCAL_ROUTE_ID = "local"

    // HyperOS 小米妙播状态键（公开可读，小米妙播 SDK 文档同样使用这些键）
    private const val SETTING_MIPLAY_CAST_STATE = "miplay_audio_cast_state"
    private const val SETTING_UCAR_CAST_STATE = "ucar_casting_state"
    private const val SETTING_VOLUME_PANEL_MIPLAY = "volume_panel_support_miplay"
    private const val PACKAGE_MILINK = "com.milink.service"

    private var appContext: Context? = null

    @Volatile
    private var started = false

    private val executor = Executors.newSingleThreadExecutor()

    // API 30+ 的 MediaRouter2 相关对象（低版本保持 null）
    private var router2: MediaRouter2? = null
    private var routeCallback: MediaRouter2.RouteCallback? = null
    private val routes = mutableListOf<MediaRoute2Info>()

    /** 路由列表变化回调（设备上下线），由 NetworkManager 订阅后推送给 PC */
    @Volatile
    var routesChangedListener: (() -> Unit)? = null

    fun init(context: Context) {
        appContext = context.applicationContext
    }

    /** 启动路由扫描（幂等）。 */
    fun start() {
        if (started) return
        started = true
        // 小米妙播投射状态监听不依赖 MediaRouter2，全版本可用
        startMiPlayStateWatcher()
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            Log.i(TAG, "MediaRouter2 requires API 30+, cast list will only contain local device")
            return
        }
        startRouter2()
    }

    /**
     * 监听 HyperOS 小米妙播投射状态（Settings.Global 公开键，普通应用可读）。
     *
     * 小爱音箱/小米电视等 Wi-Fi 妙播设备走小米私有 MiLink 协议，不进入标准 MediaRouter2
     * 路由表，第三方无法枚举或直连切换；但系统会把"是否正在妙播投放"同步到
     * [SETTING_MIPLAY_CAST_STATE] / [SETTING_UCAR_CAST_STATE]（值 1=投射中）。
     * 状态变化时复用 routesChangedListener 通道，把最新状态随 cast_devices 推给 PC。
     */
    private fun startMiPlayStateWatcher() {
        val ctx = appContext ?: return
        try {
            val resolver = ctx.contentResolver
            val observer = object : ContentObserver(Handler(Looper.getMainLooper())) {
                override fun onChange(selfChange: Boolean) {
                    Log.i(TAG, "miplay state changed: audio=${miplayCasting()} car=${carCasting()}")
                    try { routesChangedListener?.invoke() } catch (_: Throwable) {}
                }
            }
            resolver.registerContentObserver(
                Settings.Global.getUriFor(SETTING_MIPLAY_CAST_STATE), false, observer
            )
            resolver.registerContentObserver(
                Settings.Global.getUriFor(SETTING_UCAR_CAST_STATE), false, observer
            )
            Log.i(TAG, "MiPlay state watcher started, miplaySupported=${miplaySupported()}")
        } catch (e: Throwable) {
            Log.e(TAG, "startMiPlayStateWatcher failed", e)
        }
    }

    /** 是否为 HyperOS 小米妙播生态（存在妙播服务，或系统声明音量面板支持妙播）。 */
    fun miplaySupported(): Boolean {
        val ctx = appContext ?: return false
        return try {
            Settings.Secure.getInt(ctx.contentResolver, SETTING_VOLUME_PANEL_MIPLAY, 0) == 1 ||
                ctx.packageManager.getPackageInfo(PACKAGE_MILINK, 0) != null
        } catch (_: Throwable) {
            false
        }
    }

    /** 音频是否正通过小米妙播投放到音箱/电视/电脑等设备。 */
    fun miplayCasting(): Boolean = readGlobalInt(SETTING_MIPLAY_CAST_STATE) == 1

    /** 音频是否正妙播到车机。 */
    fun carCasting(): Boolean = readGlobalInt(SETTING_UCAR_CAST_STATE) == 1

    private fun readGlobalInt(key: String): Int {
        val ctx = appContext ?: return 0
        return try {
            Settings.Global.getInt(ctx.contentResolver, key, 0)
        } catch (_: Throwable) {
            0
        }
    }

    private fun startRouter2() {
        val ctx = appContext ?: return
        try {
            val router = MediaRouter2.getInstance(ctx)
            router2 = router

            val cb = object : MediaRouter2.RouteCallback() {
                override fun onRoutesUpdated(list: MutableList<MediaRoute2Info>) {
                    synchronized(routes) {
                        routes.clear()
                        routes.addAll(list)
                    }
                    Log.d(TAG, "routes updated: ${list.size}")
                    try { routesChangedListener?.invoke() } catch (_: Throwable) {}
                }
            }
            routeCallback = cb

            // 音频直播 + 远程音频投放（Cast/蓝牙/音箱均覆盖），主动扫描
            val features = listOf(
                MediaRoute2Info.FEATURE_LIVE_AUDIO,
                MediaRoute2Info.FEATURE_REMOTE_AUDIO_PLAYBACK,
                MediaRoute2Info.FEATURE_REMOTE_PLAYBACK
            )
            val preference = RouteDiscoveryPreference.Builder(features, true).build()
            router.registerRouteCallback(executor, cb, preference)

            // 立即填充当前已有路由（回调到达前先有数据）
            synchronized(routes) {
                routes.clear()
                routes.addAll(router.routes)
            }
            Log.i(TAG, "MediaRouter2 scan started, initial routes: ${routes.size}")
        } catch (e: Throwable) {
            Log.e(TAG, "MediaRouter2 init failed", e)
        }
    }

    /** 获取当前可投放设备列表（首项为本机）。 */
    fun getDevices(): List<CastDevice> {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            return listOf(
                CastDevice(id = LOCAL_ROUTE_ID, name = "本机", deviceType = "phone", selected = true)
            )
        }

        val router = router2
        val snapshot = synchronized(routes) { routes.toList() }

        // 当前正在播放的路由（所有 RoutingController 的选中路由并集）
        val selectedIds = buildSet {
            try {
                router?.controllers?.forEach { controller ->
                    controller.selectedRoutes?.let { addAll(it.map { r -> r.id }) }
                }
            } catch (_: Throwable) {}
        }

        var systemRoute: MediaRoute2Info? = null
        val remote = mutableListOf<MediaRoute2Info>()
        for (route in snapshot) {
            try {
                if (route.isSystemRoute || route.type == MediaRoute2Info.TYPE_BUILTIN_SPEAKER) {
                    if (systemRoute == null) systemRoute = route
                } else {
                    remote.add(route)
                }
            } catch (_: Throwable) {}
        }

        val devices = mutableListOf<CastDevice>()
        val localId = systemRoute?.id ?: LOCAL_ROUTE_ID

        // 没有任何非系统路由被选中时，视为在本机播放
        val remoteSelected = selectedIds.any { id ->
            snapshot.any { it.id == id && !it.isSystemRoute && it.type != MediaRoute2Info.TYPE_BUILTIN_SPEAKER }
        }
        devices.add(
            CastDevice(
                id = localId,
                name = "本机",
                deviceType = "phone",
                selected = !remoteSelected || localId in selectedIds
            )
        )

        for (route in remote) {
            try {
                devices.add(
                    CastDevice(
                        id = route.id,
                        name = route.name?.toString()?.ifEmpty { "未知设备" } ?: "未知设备",
                        deviceType = mapDeviceType(route.type),
                        selected = route.id in selectedIds
                    )
                )
            } catch (_: Throwable) {}
        }
        return devices
    }

    /**
     * 音乐流转到指定设备（routeId 为 "local" 或系统路由 id 时回流到本机）。
     * @return 是否成功发起流转
     */
    fun transfer(routeId: String): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            Log.w(TAG, "transfer requires API 30+")
            return false
        }
        return try {
            val router = router2 ?: return false
            val target = synchronized(routes) { routes.firstOrNull { it.id == routeId } } ?: return false
            router.transferTo(target)
            Log.i(TAG, "transfer to: ${target.name} ($routeId)")
            true
        } catch (e: Throwable) {
            Log.e(TAG, "transfer failed", e)
            false
        }
    }

    /** 调试：把 MediaRouter2 原始路由与转换后的设备列表打到 logcat（ADB 联调用）。 */
    fun dumpRoutes() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            Log.i(TAG, "dumpRoutes: API < 30, local device only")
            return
        }
        val router = router2
        if (router == null) {
            Log.w(TAG, "dumpRoutes: router2 == null (not started?)")
            return
        }
        val snapshot = synchronized(routes) { routes.toList() }
        Log.i(TAG, "==== raw routes (${snapshot.size}) ====")
        snapshot.forEach { r ->
            try {
                Log.i(
                    TAG,
                    "route: id=${r.id} | name=${r.name} | type=${r.type} | " +
                        "system=${r.isSystemRoute} | features=${r.features}"
                )
            } catch (_: Throwable) {}
        }
        val devices = getDevices()
        Log.i(TAG, "==== cast devices (${devices.size}) ====")
        devices.forEach { d ->
            Log.i(TAG, "device: id=${d.id} | name=${d.name} | type=${d.deviceType} | selected=${d.selected}")
        }
    }

    /** MediaRoute2Info 设备类型常量 → PC 端图标类型。 */
    private fun mapDeviceType(type: Int): String = when (type) {
        MediaRoute2Info.TYPE_REMOTE_TV,
        MediaRoute2Info.TYPE_HDMI,
        MediaRoute2Info.TYPE_REMOTE_AUDIO_VIDEO_RECEIVER -> "tv"
        MediaRoute2Info.TYPE_BLUETOOTH_A2DP,
        MediaRoute2Info.TYPE_BLE_HEADSET,
        MediaRoute2Info.TYPE_HEARING_AID,
        MediaRoute2Info.TYPE_USB_ACCESSORY,
        MediaRoute2Info.TYPE_USB_DEVICE,
        MediaRoute2Info.TYPE_USB_HEADSET,
        MediaRoute2Info.TYPE_WIRED_HEADPHONES,
        MediaRoute2Info.TYPE_WIRED_HEADSET -> "bt"
        else -> "speaker"
    }
}
