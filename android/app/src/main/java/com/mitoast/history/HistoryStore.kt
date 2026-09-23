package com.mitoast.history

import android.content.Context
import com.mitoast.model.NotificationMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/**
 * 手机端本地历史通知存储。
 *
 * 通知到达时即记录（无论电脑是否在线），电脑端重连后通过 history_sync_request
 * 增量同步离线期间的通知（entriesSince 按时间游标返回）。
 * 持久化为内部存储 JSON 文件（最新在前），内存/磁盘上限 10000 条，
 * 与 Windows 端相同的"相同内容合并"去重，跨端合并幂等。
 */
object HistoryStore {

    private const val MAX_ITEMS = 10000
    private const val SAVE_DEBOUNCE_MS = 1500L
    private const val FILE_NAME = "history.json"

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lock = Any()

    private var appContext: Context? = null
    private var items: MutableList<NotificationMessage>? = null
    private var dirty = false
    private var saveScheduled = false

    /** 应用启动时调用：加载本地历史文件。 */
    fun init(context: Context) {
        appContext = context.applicationContext
        scope.launch { load() }
    }

    private fun load() {
        val ctx = appContext ?: return
        val list = try {
            val file = java.io.File(ctx.filesDir, FILE_NAME)
            if (file.exists()) {
                json.decodeFromString<List<NotificationMessage>>(file.readText())
            } else {
                emptyList()
            }
        } catch (_: Exception) {
            emptyList()
        }
        synchronized(lock) {
            items = list.take(MAX_ITEMS).toMutableList()
        }
    }

    /**
     * 记录一条通知：剥离图标、按"应用 + 标题 + 正文"相同内容合并（取最新置顶），
     * 防抖 1.5s 落盘。通知监听回调线程上可安全调用。
     */
    fun record(message: NotificationMessage) {
        synchronized(lock) {
            val list = items
            if (list == null) {
                // 首次 load 尚未完成时先落到内存，load 完成后以文件内容为准（文件此刻可能为空，
                // 极端场景下首条通知可能丢失，可接受）
                items = mutableListOf()
            }
            val target = items!!
            removeDuplicate(target, message)
            target.add(0, message.copy(iconBase64 = ""))
            if (target.size > MAX_ITEMS) {
                target.subList(MAX_ITEMS, target.size).clear()
            }
            dirty = true
        }
        scheduleSave()
    }

    private fun removeDuplicate(list: MutableList<NotificationMessage>, message: NotificationMessage) {
        val app = message.appName.ifEmpty { message.packageName }
        val title = message.bigTitle.ifEmpty { message.title }
        val content = message.bigText.ifEmpty { message.content }
        val idx = list.indexOfFirst { m ->
            val mApp = m.appName.ifEmpty { m.packageName }
            val mTitle = m.bigTitle.ifEmpty { m.title }
            val mContent = m.bigText.ifEmpty { m.content }
            mApp == app && mTitle == title && mContent == content
        }
        if (idx >= 0) list.removeAt(idx)
    }

    /** 返回时间戳晚于 sinceMs 的通知（按时间升序，用于增量同步游标推进），最多 limit 条。 */
    fun entriesSince(sinceMs: Long, limit: Int): List<NotificationMessage> {
        synchronized(lock) {
            val list = items ?: return emptyList()
            return list.asSequence()
                .filter { it.timestamp > sinceMs }
                .sortedBy { it.timestamp }
                .take(limit)
                .toList()
        }
    }

    fun size(): Int = synchronized(lock) { items?.size ?: 0 }

    private fun scheduleSave() {
        synchronized(lock) {
            if (saveScheduled) return
            saveScheduled = true
        }
        scope.launch {
            delay(SAVE_DEBOUNCE_MS)
            synchronized(lock) { saveScheduled = false }
            save()
        }
    }

    private fun save() {
        val ctx = appContext ?: return
        val snapshot: List<NotificationMessage>
        synchronized(lock) {
            if (!dirty) return
            dirty = false
            snapshot = items?.toList() ?: return
        }
        try {
            val file = java.io.File(ctx.filesDir, FILE_NAME)
            val tmp = java.io.File(ctx.filesDir, "$FILE_NAME.tmp")
            tmp.writeText(json.encodeToString(snapshot))
            if (!tmp.renameTo(file)) {
                file.writeText(tmp.readText())
            }
        } catch (_: Exception) {
            // 写盘失败下次变更再补写
            synchronized(lock) { dirty = true }
        }
    }
}
