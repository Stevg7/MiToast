package com.mitoast.simulate

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Binder
import android.os.Process
import android.util.Log
import com.mitoast.MiToastApp
import com.mitoast.model.ClearNotificationMessage
import com.mitoast.model.NotificationMessage
import com.mitoast.model.NotificationProgress
import com.mitoast.network.NetworkManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * 外卖流程模拟接收器（仅用于 ADB 真机联调）。
 *
 * 通过 adb 广播直接构造"美团外卖"从下单到送达的各阶段消息，与真实通知走完全相同的
 * 发送通道（NetworkManager.broadcastNotification），不依赖手机上安装美团/饿了么。
 * 所有阶段使用同一个通知 key，模拟真实外卖 App 持续更新同一条常驻通知的行为，
 * Windows 端会按 key 匹配并原地更新卡片（进度条推进），而不是弹出新卡片。
 *
 * 安全限制：只接受 adb shell（uid 2000）、root（uid 0）或应用自身（HyperOS 会把 shell
 * 显式广播代理为应用自身 uid 投递）发来的广播，其他第三方应用无法触发。
 *
 * 用法：
 *   全流程（下单→送达，约 40 秒，阶段间隔可用 --el interval 调整）：
 *     adb shell am broadcast -a com.mitoast.action.SIM_DELIVERY \
 *       -n com.mitoast/.simulate.SimulateReceiver
 *   只发单个阶段（1=已下单 2=已接单 3=商家制作 4=骑手取餐 5=配送中 6=已送达）：
 *     adb shell am broadcast -a com.mitoast.action.SIM_DELIVERY \
 *       -n com.mitoast/.simulate.SimulateReceiver --ei stage 5
 *   加快节奏（间隔 3 秒）：
 *     adb shell am broadcast -a com.mitoast.action.SIM_DELIVERY \
 *       -n com.mitoast/.simulate.SimulateReceiver --el interval 3000
 *   移除模拟卡片：
 *     adb shell am broadcast -a com.mitoast.action.SIM_DELIVERY \
 *       -n com.mitoast/.simulate.SimulateReceiver --ez clear true
 */
class SimulateReceiver : BroadcastReceiver() {

    companion object {
        private const val TAG = "MiToastSim"
        private const val ACTION_SIM_DELIVERY = "com.mitoast.action.SIM_DELIVERY"
        private const val ACTION_DUMP_CAST = "com.mitoast.action.DUMP_CAST"

        // 模拟美团外卖的包名/应用名；key 格式与真实 StatusBarNotification.key 一致
        private const val PKG = "com.sankuai.meituan"
        private const val APP_NAME = "美团外卖"
        private const val NOTIF_ID = 9527
        private const val SIM_KEY = "$PKG|mitoast_sim|$NOTIF_ID"

        // delivery 类别的阶段总数与 NotificationConverter 保持一致（共 7 段，外卖到 已送达 为第 6 段）
        private const val STAGE_TOTAL = 7

        private val simScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

        @Volatile
        private var sequenceJob: Job? = null

        /** 单个阶段的展示内容（文案关键词与 NotificationConverter 的阶段识别对齐） */
        private data class Stage(
            val current: Int,
            val label: String,
            val content: String,
            val bigText: String,
            val ongoing: Boolean
        )

        private val MERCHANT = "老上海馄饨·粥饭（科技园店）"
        private val ORDER_DETAIL = "鲜肉大馄饨×1、葱油拌面×1、酸梅汤×1\n合计 ¥32.80 · 预计12:35送达"

        private val STAGES = listOf(
            Stage(
                1, "已下单",
                "您的订单已下单成功，等待商家接单",
                "$MERCHANT\n$ORDER_DETAIL",
                true
            ),
            Stage(
                2, "已接单",
                "商家已接单，正在为您准备美食",
                "$MERCHANT\n商家已接单，正在备菜制作\n$ORDER_DETAIL",
                true
            ),
            Stage(
                3, "商家制作中",
                "商家制作中，骑手正赶往商家",
                "$MERCHANT\n商家正在出餐，骑手赶往商家取餐\n预计12:35送达",
                true
            ),
            Stage(
                4, "骑手已取餐",
                "骑手已取餐，正快马加鞭为您配送",
                "骑手王师傅已取餐\n距商家 0.1 公里 · 预计28分钟送达",
                true
            ),
            Stage(
                5, "配送中",
                "骑手配送中，距您1.2公里，预计28分钟送达",
                "骑手王师傅配送中\n距您 1.2 公里 · 预计28分钟送达\n电话可在订单详情联系骑手",
                true
            ),
            Stage(
                6, "已送达",
                "您的外卖已送达，祝您用餐愉快",
                "您的外卖已送达，请及时取餐\n如有问题可在订单页申请售后，祝您用餐愉快",
                false
            )
        )
    }

    override fun onReceive(context: Context, intent: Intent?) {
        intent ?: return
        if (intent.action != ACTION_SIM_DELIVERY && intent.action != ACTION_DUMP_CAST) return

        // 标准 Android 上 adb shell 广播的调用方为 SHELL_UID(2000)；HyperOS/MIUI 会把
        // 显式广播代理为应用自身 uid 投递，因此一并放行本应用 uid（其他应用 uid 仍拒绝）。
        val caller = Binder.getCallingUid()
        if (caller != Process.SHELL_UID && caller != Process.ROOT_UID &&
            caller != Process.myUid()
        ) {
            Log.w(TAG, "Reject simulate broadcast from non-shell uid=$caller")
            return
        }

        // 广播拉起进程时确保转发服务在运行
        if (!NetworkManager.isRunning) {
            MiToastApp.instance.startNetworkService()
        }

        // 调试：dump 当前妙播/音频路由设备列表到 logcat
        // adb shell am broadcast -a com.mitoast.action.DUMP_CAST -n com.mitoast/.simulate.SimulateReceiver
        if (intent.action == "com.mitoast.action.DUMP_CAST") {
            com.mitoast.media.CastRouteManager.dumpRoutes()
            return
        }

        if (intent.getBooleanExtra("clear", false)) {
            sequenceJob?.cancel()
            NetworkManager.broadcastClear(ClearNotificationMessage(key = SIM_KEY))
            Log.i(TAG, "Simulated delivery card cleared")
            return
        }

        val stageArg = intent.getIntExtra("stage", 0)
        val interval = intent.getLongExtra("interval", 8000L).coerceIn(500L, 60_000L)

        if (stageArg in 1..STAGES.size) {
            sequenceJob?.cancel()
            val stage = STAGES[stageArg - 1]
            sendStage(stage)
            Log.i(TAG, "Sent single stage $stageArg: ${stage.label}")
            return
        }

        // 全流程：取消上一轮未播完的序列，按间隔依次推送
        sequenceJob?.cancel()
        sequenceJob = simScope.launch {
            for ((index, stage) in STAGES.withIndex()) {
                if (index > 0) delay(interval)
                sendStage(stage)
                Log.i(TAG, "Sequence stage ${stage.current}/${STAGES.size}: ${stage.label}")
            }
        }
    }

    private fun sendStage(stage: Stage) {
        val now = System.currentTimeMillis()
        val message = NotificationMessage(
            id = "${PKG}_${NOTIF_ID}_$now",
            key = SIM_KEY,
            packageName = PKG,
            appName = APP_NAME,
            title = APP_NAME,
            content = stage.content,
            bigText = stage.bigText,
            subText = MERCHANT,
            timestamp = now,
            category = "delivery",
            isOngoing = stage.ongoing,
            progress = NotificationProgress(
                current = stage.current,
                total = STAGE_TOTAL,
                label = stage.label
            )
        )
        NetworkManager.broadcastNotification(message)
    }
}
