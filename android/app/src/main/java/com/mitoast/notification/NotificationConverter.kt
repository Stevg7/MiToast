package com.mitoast.notification

import android.app.Notification
import android.graphics.Bitmap
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Drawable
import android.os.Build
import android.os.Bundle
import android.service.notification.StatusBarNotification
import com.mitoast.MiToastApp
import com.mitoast.model.NotificationMessage
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.Base64
import java.util.regex.Pattern

object NotificationConverter {

    private val PICKUP_CODE_PATTERNS = listOf(
        Pattern.compile("取餐码[：: ]*([A-Za-z0-9\\-]+)"),
        Pattern.compile("取餐号[：: ]*([A-Za-z0-9\\-]+)"),
        Pattern.compile("取餐柜[：: ]*([A-Za-z0-9\\-]+)"),
        Pattern.compile("餐柜号[：: ]*([A-Za-z0-9\\-]+)"),
        Pattern.compile("柜号[：: ]*([A-Za-z0-9\\-]+)"),
        Pattern.compile("取货码[：: ]*([A-Za-z0-9\\-]+)"),
        Pattern.compile("提货码[：: ]*([A-Za-z0-9\\-]+)"),
        Pattern.compile("([A-Za-z]\\d{1,3})")
    )

    fun convert(sbn: StatusBarNotification, appName: String): NotificationMessage {
        val n = sbn.notification
        val extras = n.extras

        val title = extras.getCharSequence("android.title")?.toString().orEmpty()
        val text = extras.getCharSequence("android.text")?.toString().orEmpty()
        val bigTitle = extras.getCharSequence("android.bigTitle")?.toString().orEmpty()
        val bigText = extras.getCharSequence("android.bigText")?.toString().orEmpty()
        val subText = extras.getCharSequence("android.subText")?.toString().orEmpty()
        val subBig = extras.getCharSequence("android.subBig")?.toString().orEmpty()

        val ticker = n.tickerText?.toString().orEmpty()
        val stableKey = buildStableKey(sbn)

        // 媒体信息要先取：带播放会话/媒体控件的通知属于媒体类，分类需要这个信号
        val mediaActions = extractMediaActions(n)
        val mediaInfo = if (mediaActions != null) extractMediaInfo(extras) else null

        val category = NotificationCategory.classify(
            NotificationCategory.Input(
                packageName = sbn.packageName,
                title = title,
                text = text,
                bigText = bigText,
                subText = subText,
                androidCategory = n.category.orEmpty(),
                hasMedia = mediaActions != null || mediaInfo != null,
                hasProgress = extras.containsKey("android.progress")
            )
        )
        val progress = buildProgress(category, title, text, bigText, extras)
        val isOngoing = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            sbn.isOngoing || (n.flags and Notification.FLAG_ONGOING_EVENT) != 0
        } else {
            (n.flags and Notification.FLAG_ONGOING_EVENT) != 0
        }

        val (hintTitle, hintText) = buildHintInfo(
            sbn, title, text, bigTitle, bigText, subText, subBig, ticker
        )

        val peopleCount = extras.getCharSequenceArray("android.people")?.size ?: 0
        val groupKey = n.group.orEmpty()

        val hyperData = tryExtractHyperNotification(extras)

        return NotificationMessage(
            id = "${sbn.packageName}_${sbn.id}_${sbn.postTime}",
            key = stableKey,
            packageName = sbn.packageName,
            appName = appName,
            title = title.ifEmpty { hyperData?.hintTitle?.takeIf { it.isNotEmpty() } ?: appName },
            content = text.ifEmpty { hyperData?.hintText.orEmpty() },
            bigTitle = bigTitle,
            bigText = bigText,
            subText = subText,
            ticker = hyperData?.ticker ?: ticker,
            hintTitle = hyperData?.hintTitle ?: hintTitle,
            hintText = hyperData?.hintText ?: hintText,
            iconBase64 = extractLargeIconBase64(sbn),
            timestamp = sbn.postTime,
            category = category,
            isOngoing = isOngoing,
            groupKey = groupKey,
            peopleCount = peopleCount,
            progress = hyperData?.progress ?: progress,
            mediaActions = mediaActions,
            mediaIsPlaying = mediaInfo?.isPlaying ?: false,
            mediaPositionMs = mediaInfo?.positionMs ?: -1L,
            mediaDurationMs = mediaInfo?.durationMs ?: -1L
        )
    }

    private data class MediaSessionInfo(
        val isPlaying: Boolean,
        val positionMs: Long,
        val durationMs: Long
    )

    /**
     * 从通知携带的 MediaSession Token 构造 MediaController，
     * 读取播放状态/当前位置/总时长（通知监听权限允许获取活跃媒体会话）。
     */
    private fun extractMediaInfo(extras: Bundle): MediaSessionInfo? {
        return try {
            val token = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                extras.getParcelable(
                    Notification.EXTRA_MEDIA_SESSION,
                    android.media.session.MediaSession.Token::class.java
                )
            } else {
                @Suppress("DEPRECATION")
                extras.getParcelable<android.media.session.MediaSession.Token>(
                    Notification.EXTRA_MEDIA_SESSION
                )
            } ?: return null

            val controller = android.media.session.MediaController(
                MiToastApp.instance, token
            )
            val state = controller.playbackState ?: return null
            val duration = controller.metadata?.getLong(
                android.media.MediaMetadata.METADATA_KEY_DURATION
            ) ?: -1L

            MediaSessionInfo(
                isPlaying = state.state == android.media.session.PlaybackState.STATE_PLAYING,
                positionMs = state.position.coerceAtLeast(0L),
                durationMs = if (duration > 0) duration else -1L
            )
        } catch (_: Exception) {
            null
        }
    }

    private fun buildStableKey(sbn: StatusBarNotification): String {
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                val sbnKey = sbn.key
                if (sbnKey != null && sbnKey.isNotEmpty()) return sbnKey
            }
            "${sbn.packageName}_${sbn.id}"
        } catch (_: Throwable) {
            "${sbn.packageName}_${sbn.id}"
        }
    }

    private fun buildProgress(
        category: String,
        title: String,
        text: String,
        bigText: String,
        extras: Bundle
    ): com.mitoast.model.NotificationProgress? {
        return when (category) {
            NotificationCategory.PICKUP -> extractPickupProgress(title, text, bigText)
            NotificationCategory.DELIVERY -> extractDeliveryProgress(title, text, bigText)
            else -> {
                if (extras.containsKey("android.progress")) {
                    val current = extras.getInt("android.progress")
                    val max = extras.getInt("android.progressMax")
                    val indeterminate = extras.getBoolean("android.progressIndeterminate", false)
                    if (!indeterminate && max > 0 && current >= 0) {
                        com.mitoast.model.NotificationProgress(
                            current = current,
                            total = max,
                            label = extras.getCharSequence("android.progressMessage")?.toString().orEmpty()
                        )
                    } else null
                } else null
            }
        }
    }

    private fun extractPickupProgress(
        title: String, text: String, bigText: String
    ): com.mitoast.model.NotificationProgress? {
        val combined = "$title $text $bigText"

        val stages = listOf(
            "已下单" to 1, "下单成功" to 1,
            "已支付" to 2, "支付成功" to 2,
            "制作中" to 3, "正在制作" to 3, "准备中" to 3,
            "已制作" to 4, "制作完成" to 4, "已出炉" to 4,
            "已准备好" to 5, "已备好" to 5, "可领取" to 5, "可取餐" to 5,
            "已领取" to 6, "已取餐" to 6
        )

        var currentStage = 1
        var matchedLabel = "订单处理中"
        for ((keyword, stage) in stages) {
            if (combined.contains(keyword) && stage > currentStage) {
                currentStage = stage
                matchedLabel = keyword
            }
        }

        val code = extractPickupCode(title, text, bigText)
        val eta = extractEtaText(title, text, bigText)
        val location = extractLocationHint(combined)

        val extras = mutableListOf<String>()
        code?.let { extras.add("取餐码 $it") }
        eta?.let { extras.add(it) }
        location?.let { extras.add(it) }

        if (extras.isNotEmpty()) matchedLabel = "$matchedLabel · ${extras.joinToString(" · ")}"

        return com.mitoast.model.NotificationProgress(
            current = currentStage,
            total = 6,
            label = matchedLabel
        )
    }

    private fun extractPickupCode(vararg texts: String): String? {
        for (text in texts) {
            for (pattern in PICKUP_CODE_PATTERNS) {
                val m = pattern.matcher(text)
                if (m.find()) {
                    val group = m.group(1)
                    if (group != null) return group
                }
            }
        }
        return null
    }

    private fun extractLocationHint(combined: String): String? {
        val patterns = listOf(
            Pattern.compile("(.+?店)"),
            Pattern.compile("(.+?店\\d+)"),
            Pattern.compile("(.+?广场店)"),
            Pattern.compile("(.+?商场店)"),
            Pattern.compile("前往(.+?店)"),
            Pattern.compile("到店地址[：: ]*(.+)")
        )
        for (pattern in patterns) {
            val m = pattern.matcher(combined)
            if (m.find()) return m.group(1)
        }
        return null
    }

    private fun extractDeliveryProgress(
        title: String, text: String, bigText: String
    ): com.mitoast.model.NotificationProgress? {
        val combined = "$title $text $bigText"

        val stages = listOf(
            "已下单" to 1,
            "已接单" to 2,
            "商家确认" to 3, "商家制作" to 3, "准备中" to 3,
            "骑手取餐" to 4, "骑手已取" to 4, "配送员已取" to 4,
            "骑手配送" to 5, "配送中" to 5, "派送中" to 5, "骑手正在" to 5,
            "已送达" to 6,
            "已签收" to 7
        )

        var currentStage = 1
        var matchedLabel = "订单处理中"
        for ((keyword, stage) in stages) {
            if (combined.contains(keyword) && stage > currentStage) {
                currentStage = stage
                matchedLabel = keyword
            }
        }

        val eta = extractEtaText(title, text, bigText)
        if (eta != null) matchedLabel = "$matchedLabel · $eta"

        return com.mitoast.model.NotificationProgress(
            current = currentStage,
            total = 7,
            label = matchedLabel
        )
    }

    private val etaPatterns = listOf(
        Pattern.compile("(\\d+)[分钟]后到达"),
        Pattern.compile("预计(\\d+)[分钟]"),
        Pattern.compile("还有(\\d+)[分钟]"),
        Pattern.compile("(\\d+):(\\d+)[到达左右]?"),
        Pattern.compile("(\\d+\\.?\\d*)公里")
    )

    private fun extractEtaText(vararg texts: String): String? {
        for (text in texts) {
            for (pattern in etaPatterns) {
                val m = pattern.matcher(text)
                if (m.find()) {
                    return m.group()
                }
            }
        }
        return null
    }

    private fun buildHintInfo(
        sbn: StatusBarNotification,
        title: String, text: String,
        bigTitle: String, bigText: String,
        subText: String, subBig: String,
        ticker: String
    ): Pair<String, String> {
        val hintTitle = subText.ifEmpty { subBig }
        val hintText = if (bigText.isNotEmpty() && bigText != text) bigText else ""

        val finalHintTitle = hintTitle.ifEmpty {
            if (title.isEmpty() && text.isNotEmpty()) text.substringBefore('\n').take(30) else ""
        }

        return finalHintTitle to hintText
    }

    private data class HyperData(
        val ticker: String,
        val hintTitle: String,
        val hintText: String,
        val progress: com.mitoast.model.NotificationProgress?
    )

    private fun tryExtractHyperNotification(extras: Bundle): HyperData? {
        try {
            // HyperOS 焦点通知 V3（超级岛/实况通知）：模板参数为 JSON 字符串，
            // 文案在 param_v2.chatInfo（title/content）、ticker 在 param_v2.ticker
            extras.getString("miui.focus.param")?.let { json ->
                try {
                    val v2 = JSONObject(json).optJSONObject("param_v2")
                    val chat = v2?.optJSONObject("chatInfo")
                    val ticker = v2?.optString("ticker").orEmpty()
                    val title = chat?.optString("title").orEmpty()
                    val content = chat?.optString("content").orEmpty()
                    if (ticker.isNotEmpty() || title.isNotEmpty() || content.isNotEmpty()) {
                        return HyperData(ticker, title, content, null)
                    }
                } catch (_: Exception) {
                }
            }

            var hyperBundle: Bundle? = null
            for (key in extras.keySet()) {
                if (key.contains("hyper", ignoreCase = true) ||
                    key.contains("focus", ignoreCase = true)) {
                    val value = extras.get(key)
                    if (value is Bundle) {
                        hyperBundle = value
                        break
                    }
                }
            }
            hyperBundle ?: return null

            val baseInfo = hyperBundle.getBundle("baseInfo")
            val hintInfo = hyperBundle.getBundle("hintInfo")

            val baseTitle = baseInfo?.getString("title").orEmpty()
            val baseContent = baseInfo?.getString("content").orEmpty()
            val hintTitle = hintInfo?.getString("title").orEmpty()
            val hintContent = hintInfo?.getString("content").orEmpty()

            val ticker = hyperBundle.getString("ticker").orEmpty()
            val progressLabel = hyperBundle.getString("progressLabel").orEmpty()
            val progressCurrent = hyperBundle.getInt("progressCurrent", -1)
            val progressTotal = hyperBundle.getInt("progressTotal", 0)

            val progress = if (progressCurrent >= 0 && progressTotal > 0) {
                com.mitoast.model.NotificationProgress(progressCurrent, progressTotal, progressLabel)
            } else null

            val outTitle = hintTitle.ifEmpty { baseTitle }
            val outContent = hintContent.ifEmpty { baseContent }
            // 全部为空时返回 null，避免空 HyperData 覆盖正常的 hint/ticker 字段
            if (ticker.isEmpty() && outTitle.isEmpty() && outContent.isEmpty() && progress == null) {
                return null
            }
            return HyperData(ticker, outTitle, outContent, progress)
        } catch (_: Exception) {
            return null
        }
    }

    private fun extractLargeIconBase64(sbn: StatusBarNotification): String {
        return try {
            val context = MiToastApp.instance
            val n = sbn.notification

            // 优先使用 Icon 形式的大图标（API 23+），其次旧式 Bitmap，最后回退小图标
            val drawable: Drawable? = n.getLargeIcon()?.loadDrawable(context)
                ?: @Suppress("DEPRECATION") n.largeIcon?.let { BitmapDrawable(context.resources, it) }
                ?: n.smallIcon?.loadDrawable(context)
            if (drawable == null) return ""

            val width = drawable.intrinsicWidth.coerceAtLeast(1)
            val height = drawable.intrinsicHeight.coerceAtLeast(1)

            val scaledWidth = width.coerceAtMost(128)
            val scaledHeight = height.coerceAtMost(128)

            val bitmap = Bitmap.createBitmap(scaledWidth, scaledHeight, Bitmap.Config.ARGB_8888)
            val canvas = android.graphics.Canvas(bitmap)
            drawable.setBounds(0, 0, scaledWidth, scaledHeight)
            drawable.draw(canvas)

            val os = ByteArrayOutputStream()
            bitmap.compress(Bitmap.CompressFormat.PNG, 75, os)
            Base64.getEncoder().encodeToString(os.toByteArray())
        } catch (_: Exception) {
            ""
        }
    }

    private val MEDIA_PREV_KEYWORDS = listOf("上一曲","上一首","previous","prev","backward","后退")
    private val MEDIA_NEXT_KEYWORDS = listOf("下一曲","下一首","next","forward","前进")
    private val MEDIA_FAVORITE_KEYWORDS = listOf("收藏","喜欢","favorite","favourite","like")
    private val MEDIA_LYRIC_KEYWORDS = listOf("歌词")

    /**
     * 遍历通知 actions，按 title 关键词识别媒体控件：
     * favorite（收藏/♥）、prev（上一曲）、play/pause（播放/暂停）、next（下一曲）、lyric（歌词）。
     * 注意"播放列表/播放模式"不能误判为 play。
     */
    private fun extractMediaActions(n: Notification): List<com.mitoast.model.MediaAction>? {
        return try {
            val actions = n.actions ?: return null
            val result = mutableListOf<com.mitoast.model.MediaAction>()
            actions.forEachIndexed { idx, action ->
                val rawTitle = action.title?.toString().orEmpty()
                val title = rawTitle.lowercase()
                if (title.isEmpty()) return@forEachIndexed
                val matched = when {
                    MEDIA_FAVORITE_KEYWORDS.any { title.contains(it) } -> "favorite"
                    MEDIA_LYRIC_KEYWORDS.any { title.contains(it) } -> "lyric"
                    MEDIA_PREV_KEYWORDS.any { title.contains(it) } -> "prev"
                    MEDIA_NEXT_KEYWORDS.any { title.contains(it) } -> "next"
                    title.contains("暂停") || title.contains("pause") -> "pause"
                    (title == "播放" || title.contains("播放/暂停") || title.contains("播放／暂停")
                        || title == "play" || title.endsWith(" play"))
                        && !title.contains("列表") && !title.contains("模式") -> "play"
                    else -> return@forEachIndexed
                }
                result.add(com.mitoast.model.MediaAction(name = matched, index = idx, title = rawTitle))
            }
            if (result.isEmpty()) null else result
        } catch (_: Throwable) { null }
    }
}
