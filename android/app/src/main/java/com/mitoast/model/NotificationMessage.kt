package com.mitoast.model

import kotlinx.serialization.Serializable

@Serializable
data class NotificationMessage(
    val type: String = "notification",
    val id: String,
    val key: String,
    val packageName: String,
    val appName: String,
    val title: String,
    val content: String,
    val bigTitle: String = "",
    val bigText: String = "",
    val subText: String = "",
    val ticker: String = "",
    val hintTitle: String = "",
    val hintText: String = "",
    val iconBase64: String = "",
    val timestamp: Long,
    val category: String = "general",
    val isOngoing: Boolean = false,
    val groupKey: String = "",
    val peopleCount: Int = 0,
    val progress: NotificationProgress? = null,
    val mediaActions: List<MediaAction>? = null,
    /** 媒体是否正在播放（来自 MediaSession PlaybackState） */
    val mediaIsPlaying: Boolean = false,
    /** 媒体当前播放位置（毫秒），-1 表示未知 */
    val mediaPositionMs: Long = -1,
    /** 媒体总时长（毫秒），-1 表示未知 */
    val mediaDurationMs: Long = -1
)

@Serializable
data class NotificationProgress(
    val current: Int,
    val total: Int,
    val label: String
)

@Serializable
data class MediaAction(
    val name: String,
    val index: Int,
    /** 通知 action 的原始文案（如"已收藏/取消收藏/关闭歌词"），用于推断开关初始态 */
    val title: String = ""
)

/** 妙播/投放在内的播放设备（本机、蓝牙、Cast 设备、智能音箱等） */
@Serializable
data class CastDevice(
    val id: String,
    val name: String,
    /** phone / tv / bt / speaker */
    val deviceType: String,
    val selected: Boolean = false
)

@Serializable
data class CastDevicesMessage(
    val type: String = "cast_devices",
    val devices: List<CastDevice> = emptyList(),
    /**
     * 当前为 HyperOS 小米妙播生态（系统内置 com.milink.service）。
     * 小爱音箱/小米电视/小米电脑等 Wi-Fi 妙播设备不走标准 MediaRouter2，第三方无法枚举/直连，
     * 需在手机控制中心「小米妙播」面板选择，PC 端仅做提示与状态展示。
     */
    val miplaySupported: Boolean = false,
    /** 音频是否正通过小米妙播投放到音箱/电视/电脑（Settings.Global: miplay_audio_cast_state）。 */
    val miplayCasting: Boolean = false,
    /** 音频是否正妙播到车机（Settings.Global: ucar_casting_state）。 */
    val carCasting: Boolean = false,
    /** 妙播可由 PC 端反向控制（无障碍服务可用，可自动完成选择器操作）。 */
    val miplayControllable: Boolean = false
)

@Serializable
data class PingMessage(
    val type: String = "ping",
    val deviceName: String,
    val deviceModel: String,
    val version: String = "1.0.0"
)

@Serializable
data class ClearNotificationMessage(
    val type: String = "clear",
    val key: String
)
