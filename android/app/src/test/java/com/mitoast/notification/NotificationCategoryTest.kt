package com.mitoast.notification

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * 通知分类的用例覆盖。样例取自真实通知的形状（银行短信、外卖、群聊、推送等），
 * 个人内容一律替换为等价的占位文案。
 */
class NotificationCategoryTest {

    private fun classify(
        pkg: String,
        title: String = "",
        text: String = "",
        bigText: String = "",
        subText: String = "",
        androidCategory: String = "",
        hasMedia: Boolean = false,
        hasProgress: Boolean = false
    ): String = NotificationCategory.classify(
        NotificationCategory.Input(
            packageName = pkg,
            title = title,
            text = text,
            bigText = bigText,
            subText = subText,
            androidCategory = androidCategory,
            hasMedia = hasMedia,
            hasProgress = hasProgress
        )
    )

    // ---------- 资金类 ----------

    @Test
    fun `银行消费短信归支付`() {
        assertEquals(
            NotificationCategory.PAYMENT,
            classify(
                pkg = "com.android.mms",
                title = "工商银行",
                text = "尾号3198卡9月24日04:21支出(消费某商户)19.87元，余额2,356.36元。【工商银行】"
            )
        )
    }

    @Test
    fun `支付宝交易提醒归支付`() {
        assertEquals(
            NotificationCategory.PAYMENT,
            classify(
                pkg = "com.eg.android.AlipayGphone",
                title = "交易提醒",
                text = "你有一笔35.25元的支出，点击领取3个支付宝积分。"
            )
        )
    }

    @Test
    fun `支付宝自动续费签约归支付`() {
        assertEquals(
            NotificationCategory.PAYMENT,
            classify(
                pkg = "com.eg.android.AlipayGphone",
                title = "签约成功通知",
                text = "账户135******32开通夸克网盘SVIP会员自动续费，发生消费时商户可自动从你的账户扣款。"
            )
        )
    }

    @Test
    fun `支付宝里的快递签收按订单处理`() {
        assertEquals(
            NotificationCategory.ORDER,
            classify(
                pkg = "com.eg.android.AlipayGphone",
                title = "快递通知",
                text = "您的包裹已签收，感谢使用。"
            )
        )
    }

    // ---------- 验证码 ----------

    @Test
    fun `验证码短信归验证码`() {
        assertEquals(
            NotificationCategory.VERIFICATION,
            classify(
                pkg = "com.android.mms",
                title = "某应用",
                text = "【某应用】验证码4973，用于登录。泄露有风险，如非本人操作，请忽略本条短信。"
            )
        )
    }

    // ---------- 消息 ----------

    @Test
    fun `QQ群聊不再被当成进度类`() {
        assertEquals(
            NotificationCategory.CHAT,
            classify(
                pkg = "com.tencent.mobileqq",
                title = "某群",
                text = "[有人@我]某人: 内容",
                androidCategory = "msg"
            )
        )
    }

    @Test
    fun `微信普通消息归消息`() {
        assertEquals(
            NotificationCategory.CHAT,
            classify(pkg = "com.tencent.mm", title = "张三", text = "晚上一起吃饭吗？")
        )
    }

    @Test
    fun `群聊里出现订单字样仍是消息`() {
        assertEquals(
            NotificationCategory.CHAT,
            classify(
                pkg = "com.tencent.mobileqq",
                title = "某群",
                text = "订单我看到了，明天就发货"
            )
        )
    }

    @Test
    fun `企业微信归消息而不是外卖`() {
        assertEquals(
            NotificationCategory.CHAT,
            classify(
                pkg = "com.tencent.wework",
                title = "王五",
                text = "明天的会议材料发我一份"
            )
        )
    }

    @Test
    fun `微信里的支付凭证带金额时归支付`() {
        assertEquals(
            NotificationCategory.PAYMENT,
            classify(
                pkg = "com.tencent.mm",
                title = "微信支付",
                text = "微信支付凭证，向某商户付款10.00元"
            )
        )
    }

    @Test
    fun `微信里的登录验证码归验证码`() {
        assertEquals(
            NotificationCategory.VERIFICATION,
            classify(pkg = "com.tencent.mm", title = "登录", text = "你的验证码是 1234，5分钟内有效")
        )
    }

    @Test
    fun `内容平台私信归消息`() {
        assertEquals(
            NotificationCategory.CHAT,
            classify(pkg = "tv.danmaku.bili", title = "某人", text = "回复了你：说得对")
        )
    }

    // ---------- 媒体 ----------

    @Test
    fun `带媒体会话归音乐`() {
        assertEquals(
            NotificationCategory.MUSIC,
            classify(pkg = "com.tencent.qqmusic", title = "晴天", text = "周杰伦", hasMedia = true)
        )
    }

    @Test
    fun `音乐应用无媒体会话也归音乐`() {
        assertEquals(
            NotificationCategory.MUSIC,
            classify(pkg = "com.netease.cloudmusic", title = "每日推荐", text = "为你推荐 30 首")
        )
    }

    // ---------- 外卖与取餐 ----------

    @Test
    fun `骑手配送归外卖`() {
        assertEquals(
            NotificationCategory.DELIVERY,
            classify(pkg = "com.sankuai.meituan", title = "骑手已取餐", text = "骑手正在为您配送")
        )
    }

    @Test
    fun `外卖平台订单已送达归外卖`() {
        assertEquals(
            NotificationCategory.DELIVERY,
            classify(pkg = "com.sankuai.meituan", title = "订单已送达", text = "祝您用餐愉快")
        )
    }

    @Test
    fun `取餐码归到店取餐`() {
        assertEquals(
            NotificationCategory.PICKUP,
            classify(pkg = "com.sankuai.meituan", title = "餐已准备好", text = "取餐码 A12，请到店领取")
        )
    }

    @Test
    fun `外卖平台无阶段文案也归外卖`() {
        assertEquals(
            NotificationCategory.DELIVERY,
            classify(pkg = "com.ele.me", title = "优惠券到账", text = "满30减8")
        )
    }

    // ---------- 电商与推送 ----------

    @Test
    fun `电商发货通知归订单`() {
        assertEquals(
            NotificationCategory.ORDER,
            classify(pkg = "com.taobao.taobao", title = "已发货", text = "您的订单已发货，运单号已生成")
        )
    }

    @Test
    fun `电商营销消息归推广`() {
        assertEquals(
            NotificationCategory.PROMO,
            classify(
                pkg = "com.taobao.taobao",
                title = "某旗舰店",
                text = "亲亲 看到消息及时联系我哦，到时候活动结束了就损失啦"
            )
        )
    }

    @Test
    fun `拼多多营销推送不再被误判成外卖`() {
        assertEquals(
            NotificationCategory.PROMO,
            classify(pkg = "com.xunmeng.pinduoduo", title = "限时秒杀", text = "整点开抢")
        )
    }

    @Test
    fun `视频平台推荐归推广`() {
        assertEquals(
            NotificationCategory.PROMO,
            classify(pkg = "tv.danmaku.bili", title = "某个视频标题", text = "某UP主")
        )
    }

    @Test
    fun `Android标成推广的直接归推广`() {
        assertEquals(
            NotificationCategory.PROMO,
            classify(pkg = "com.example.app", title = "活动", androidCategory = "promo")
        )
    }

    // ---------- 日程与系统 ----------

    @Test
    fun `课程通知归日程`() {
        assertEquals(
            NotificationCategory.SCHEDULE,
            classify(pkg = "com.chaoxing.mobile", title = "通知", text = "通知：课程通知")
        )
    }

    @Test
    fun `Android标成提醒的归日程`() {
        assertEquals(
            NotificationCategory.SCHEDULE,
            classify(pkg = "com.example.app", title = "开会", androidCategory = "reminder")
        )
    }

    @Test
    fun `充电实况归系统`() {
        assertEquals(
            NotificationCategory.SYSTEM,
            classify(
                pkg = "com.spark.noticeflow.hyper",
                title = "5.38W",
                text = "正在充电  电量96%  4.23V  1273mA  29.6°C"
            )
        )
    }

    @Test
    fun `系统应用通知归系统`() {
        assertEquals(
            NotificationCategory.SYSTEM,
            classify(pkg = "com.miui.securitycenter", title = "退出快充加速", text = "息屏后极致加速")
        )
    }

    @Test
    fun `摄像头隐私提示归系统`() {
        assertEquals(
            NotificationCategory.SYSTEM,
            classify(
                pkg = "com.microsoft.emmx",
                title = "有一个网站正在使用您的摄像头",
                text = "点按即可返回"
            )
        )
    }

    // ---------- 进度与兜底 ----------

    @Test
    fun `下载进度归进度类`() {
        assertEquals(
            NotificationCategory.PROGRESS,
            classify(pkg = "com.android.chrome", title = "正在下载", text = "45%", androidCategory = "progress")
        )
    }

    @Test
    fun `带进度 extras 的未知应用归进度类`() {
        assertEquals(
            NotificationCategory.PROGRESS,
            classify(pkg = "com.example.app", title = "同步中", hasProgress = true)
        )
    }

    @Test
    fun `无任何信号的未知应用归未分类`() {
        assertEquals(
            NotificationCategory.GENERAL,
            classify(pkg = "com.example.app", title = "你好", text = "这是一条普通通知")
        )
    }

    @Test
    fun `阶段进度条只对配送取餐与进度类开放`() {
        assertEquals(
            setOf(
                NotificationCategory.DELIVERY,
                NotificationCategory.PICKUP,
                NotificationCategory.PROGRESS
            ),
            NotificationCategory.STAGE_BAR_CATEGORIES
        )
    }
}
