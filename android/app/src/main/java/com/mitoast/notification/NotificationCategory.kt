package com.mitoast.notification

/**
 * 通知分类：把通知归入内容类型，供卡片展示、历史列表标签，以及 AI Agent 通过
 * MCP `query_notifications` 的 `category` 参数按类过滤。
 *
 * 只做包名/文案/Android 通知属性的纯字符串判断，不引用任何 Android API，
 * 因此可以用普通 JVM 单测覆盖真实用例（见 `NotificationCategoryTest`）。
 *
 * 分类在采集时确定一次并写入历史，之后不再变化；取值只增不改，
 * 避免旧历史的语义随规则调整而漂移。
 */
object NotificationCategory {

    // ---------- 取值（新增值请同步：Windows 历史列表标签、MCP 工具说明、README）----------

    /** 即时消息：QQ/微信/企业微信/钉钉等 IM，以及内容平台的私信与回复。 */
    const val CHAT = "chat"

    /** 音乐/媒体播放：带 MediaSession 或媒体控件的通知。 */
    const val MUSIC = "music"

    /** 支付、银行、账单、转账、自动续费等资金变动。 */
    const val PAYMENT = "payment"

    /** 验证码、登录校验码。 */
    const val VERIFICATION = "verification"

    /** 外卖/即时配送，带配送阶段进度。 */
    const val DELIVERY = "delivery"

    /** 到店取餐/自提，带取餐阶段进度。 */
    const val PICKUP = "pickup"

    /** 电商订单、快递物流、售后。 */
    const val ORDER = "order"

    /** 营销推广与内容推荐（商店活动、视频/资讯推送）。 */
    const val PROMO = "promo"

    /** 系统、设备状态、权限与隐私提示。 */
    const val SYSTEM = "system"

    /** 日程、课程、会议、打卡提醒。 */
    const val SCHEDULE = "schedule"

    /** 其它带进度的任务（下载/上传等），保留给进度条展示。 */
    const val PROGRESS = "progress"

    /** 未归类。 */
    const val GENERAL = "general"

    /**
     * 能驱动卡片阶段进度条的分类。阶段条的每一段代表一个业务阶段，
     * 其它分类即使携带进度也不能画（进度含义不同，段数会失控）。
     */
    val STAGE_BAR_CATEGORIES = setOf(DELIVERY, PICKUP, PROGRESS)

    /** 分类输入：与 [com.mitoast.model.NotificationMessage] 的文本字段一一对应。 */
    data class Input(
        val packageName: String,
        val title: String = "",
        val text: String = "",
        val bigText: String = "",
        val subText: String = "",
        /** Android `Notification.category`（msg/promo/reminder/sys…），未设置时为空串。 */
        val androidCategory: String = "",
        /** 带 MediaSession 或可识别的媒体控件。 */
        val hasMedia: Boolean = false,
        /** extras 里带 `android.progress`。 */
        val hasProgress: Boolean = false
    )

    // ---------- 包名集合 ----------

    private val MEDIA_PACKAGES = setOf(
        "com.netease.cloudmusic", "com.tencent.qqmusic", "com.kugou.android",
        "cn.kuwo.player", "com.ximalaya.ting.android", "com.spotify.music",
        "com.google.android.apps.youtube.music"
    )

    private val IM_PACKAGES = setOf(
        "com.tencent.mm", "com.tencent.mobileqq", "com.tencent.tim", "com.tencent.wework",
        "com.alibaba.android.rimet", "com.ss.android.lark",
        "org.telegram.messenger", "com.whatsapp", "com.facebook.orca"
    )

    /** 外卖/即时配送平台（餐饮为主，区别于电商快递）。 */
    private val DELIVERY_PACKAGES = setOf(
        "com.sankuai.meituan", "com.meituan",
        "com.ele.me", "com.eleme",
        "com.mcdonalds.app", "com.mcdonalds",
        "com.yumc.kfc", "com.yumc.pizza", "com.kfc.mobile",
        "com.starbucks.cn", "com.starbucks",
        "com.burgerking", "com.burgerking.cn", "com.dicos"
    )

    /** 电商平台：命中订单关键词归 order，否则按营销推送归 promo。 */
    private val COMMERCE_PACKAGES = setOf(
        "com.taobao.taobao", "com.tmall.wireless", "com.jingdong.app.mall",
        "com.jingdong.mobile", "com.xunmeng.pinduoduo", "com.suning.mobile.ebuy",
        "com.dangdang.buy", "com.achievo.vipshop", "com.taobao.idlefish",
        "com.kaola", "com.taobao.litetao"
    )

    /** 内容平台：默认按内容推荐处理，命中私信/回复等关键词时归 chat。 */
    private val FEED_PACKAGES = setOf(
        "tv.danmaku.bili", "com.zhihu.android", "com.sina.weibo",
        "com.ss.android.article.news", "com.ss.android.ugc.aweme",
        "com.smile.gifmaker", "com.xingin.xhs", "com.douban.frodo",
        "com.netease.newsreader.activity"
    )

    private val SCHEDULE_PACKAGES = setOf(
        "com.chaoxing.mobile", "com.android.calendar", "com.miui.calendar",
        "com.coloros.calendar", "com.xiaomi.xiaoailite"
    )

    /** 包名里出现这些片段视为银行/支付类 App。 */
    private val PAYMENT_PACKAGE_HINTS = listOf("bank", "pay", "wallet", "credit", "tenpay")

    /** 包名以这些前缀开头的视为系统应用。 */
    private val SYSTEM_PACKAGE_PREFIXES = listOf("android", "com.android.", "com.miui.")

    // ---------- 关键词 ----------

    private val PICKUP_KEYWORDS = listOf(
        "取餐码", "取餐号", "取餐柜", "餐柜号", "取货码", "提货码",
        "到店自提", "门店自提", "自提", "已准备好", "制作完成", "可领取",
        "已出炉", "已制作", "请到店", "堂食"
    )

    private val DELIVERY_KEYWORDS = listOf(
        "外卖", "配送", "骑手", "派送", "送达", "正在为您配送",
        "商家已接单", "预计送达", "正在赶来"
    )

    private val VERIFICATION_KEYWORDS = listOf(
        "验证码", "校验码", "动态密码", "动态码", "登录码", "一次性密码", "短信密码"
    )

    private val PAYMENT_KEYWORDS = listOf(
        "支出", "消费", "余额", "扣款", "入账", "交易提醒", "账单",
        "已支付", "支付成功", "支付失败", "支付凭证", "付款", "收款", "转账", "到账",
        "信用卡", "借记卡", "储蓄卡", "尾号", "自动续费", "免密支付", "退款"
    )

    private val ORDER_KEYWORDS = listOf(
        "订单", "已发货", "已签收", "待收货", "物流", "包裹", "快递",
        "运单", "派件", "揽收", "退货", "售后", "换货", "补发", "已出库"
    )

    private val CHAT_KEYWORDS = listOf(
        "@我", "@了你", "有人@", "新消息", "特别关心", "回复了你", "评论了你", "私信"
    )

    private val SCHEDULE_KEYWORDS = listOf(
        "课程", "课表", "上课", "打卡", "签到", "会议", "日程", "待办",
        "作业", "截止", "考试", "开课", "签到码"
    )

    private val PROMO_KEYWORDS = listOf(
        "旗舰店", "优惠", "活动", "秒杀", "领券", "优惠券", "上新",
        "开播", "直播", "抽奖", "满减", "包邮", "限时", "为你推荐",
        "推荐", "降价", "到手价", "福利"
    )

    private val SYSTEM_KEYWORDS = listOf(
        "正在充电", "充电中", "已充满", "电量", "剩余电量",
        "摄像头", "麦克风", "录屏", "屏幕录制", "系统更新"
    )

    /** 金额/卡号细节：IM 里的资金文案需要这些细节才认定为支付通知。 */
    private val MONEY_DETAIL = Regex("""\d+(?:\.\d+)?\s*元|尾号\s*\d{3,4}|余额|账户\s*\d""")

    private val ANDROID_CATEGORY_CHAT = setOf("msg", "email", "call", "missed_call", "social")
    private val ANDROID_CATEGORY_SCHEDULE = setOf("reminder", "alarm", "event")
    private val ANDROID_CATEGORY_PROMO = setOf("promo", "recommendation")
    private const val ANDROID_CATEGORY_PROGRESS = "progress"
    private val ANDROID_CATEGORY_SYSTEM = setOf(
        "sys", "service", "err", "status", "transport", "navigation",
        "location_sharing", "stopwatch", "workout"
    )

    /**
     * 归类一条通知。规则从「信号最明确」到「最笼统」依次判定：
     * 媒体会话 → IM 包名 → 内容关键词 → Android 通知属性 → 平台默认。
     */
    fun classify(input: Input): String {
        val pkg = input.packageName
        val combined = "${input.title} ${input.text} ${input.bigText} ${input.subText}"

        // 1) 带媒体会话的必然是播放类，与 App 无关（浏览器网页播放也算）。
        if (input.hasMedia || pkg in MEDIA_PACKAGES) return MUSIC

        // 2) IM 单独走一条窄路：只有验证码/带金额细节的支付能压过消息本身，
        //    否则 IM 里的「订单」「活动」等词会让群聊被误判成电商/营销通知。
        if (pkg in IM_PACKAGES) {
            if (VERIFICATION_KEYWORDS.any { combined.contains(it) }) return VERIFICATION
            if (PAYMENT_KEYWORDS.any { combined.contains(it) } && MONEY_DETAIL.containsMatchIn(combined)) {
                return PAYMENT
            }
            return CHAT
        }

        // 3) 内容关键词：这些词指向性极强，优先于 App 自身的粗分类。
        if (VERIFICATION_KEYWORDS.any { combined.contains(it) }) return VERIFICATION
        if (PICKUP_KEYWORDS.any { combined.contains(it) }) return PICKUP
        if (DELIVERY_KEYWORDS.any { combined.contains(it) } || pkg in DELIVERY_PACKAGES) return DELIVERY
        if (ORDER_KEYWORDS.any { combined.contains(it) }) return ORDER
        if (PAYMENT_KEYWORDS.any { combined.contains(it) } || looksLikePaymentApp(pkg)) return PAYMENT

        // 4) 交互类文案：内容平台的私信/回复算消息，而不是推荐流。
        if (CHAT_KEYWORDS.any { combined.contains(it) }) return CHAT

        if (SCHEDULE_KEYWORDS.any { combined.contains(it) } ||
            pkg in SCHEDULE_PACKAGES ||
            input.androidCategory in ANDROID_CATEGORY_SCHEDULE
        ) {
            return SCHEDULE
        }

        if (PROMO_KEYWORDS.any { combined.contains(it) } ||
            input.androidCategory in ANDROID_CATEGORY_PROMO
        ) {
            return PROMO
        }

        // Android 标成 progress 的（下载/上传等后台任务）按进度类处理，不归系统。
        if (input.androidCategory == ANDROID_CATEGORY_PROGRESS) return PROGRESS

        // 5) 系统/设备：Android 的 sys/service/status 等分类，或系统包名与设备关键词。
        if (input.androidCategory in ANDROID_CATEGORY_SYSTEM ||
            SYSTEM_KEYWORDS.any { combined.contains(it) } ||
            SYSTEM_PACKAGE_PREFIXES.any { pkg.startsWith(it) }
        ) {
            return SYSTEM
        }

        // 6) Android 自己标成 msg 的（第三方 IM、短信类插件）算消息。
        if (input.androidCategory in ANDROID_CATEGORY_CHAT) return CHAT

        // 7) 平台默认：电商与内容平台的推送以营销/推荐为主。
        if (pkg in COMMERCE_PACKAGES || pkg in FEED_PACKAGES) return PROMO

        // 8) 剩下的只要带进度，仍走进度条展示。
        if (input.hasProgress) return PROGRESS

        return GENERAL
    }

    private fun looksLikePaymentApp(packageName: String): Boolean {
        val lower = packageName.lowercase()
        return PAYMENT_PACKAGE_HINTS.any { lower.contains(it) }
    }
}
