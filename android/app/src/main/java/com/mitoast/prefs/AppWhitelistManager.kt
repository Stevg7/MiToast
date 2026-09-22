package com.mitoast.prefs

import android.content.Context
import android.content.pm.ApplicationInfo

/**
 * 通知同步白名单管理。
 *
 * 规则：
 * - 白名单中的应用始终允许转发（包括被显式勾选的系统应用，如短信 App）
 * - 白名单模式开启时：仅转发勾选的应用
 * - 白名单模式关闭时：转发所有非系统应用，系统应用默认过滤
 */
object AppWhitelistManager {

    private const val PREFS_NAME = "mitoast_prefs"
    private const val KEY_WHITELIST_ENABLED = "whitelist_enabled"
    private const val KEY_WHITELIST_PACKAGES = "whitelist_packages"

    /**
     * 开箱即放行的系统应用：短信、电话、日历、闹钟。
     * 这类通知用户明确希望同步到电脑，即使未开启白名单模式也允许转发。
     */
    private val DEFAULT_SYSTEM_ALLOWED = setOf(
        "com.android.mms",            // 短信
        "com.android.phone",          // 电话（来电/通话通知）
        "com.android.server.telecom",
        "com.android.calendar",       // 日历
        "com.miui.calendar",
        "com.android.deskclock",      // 闹钟/时钟
        "com.miui.alarmservice",
        "com.miui.securitycenter"     // 验证码识别（部分 HyperOS 机型由安全中心发出）
    )

    fun isWhitelistEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_WHITELIST_ENABLED, false)

    fun setWhitelistEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_WHITELIST_ENABLED, enabled).apply()
    }

    fun getWhitelist(context: Context): Set<String> =
        prefs(context).getStringSet(KEY_WHITELIST_PACKAGES, emptySet())?.toSet() ?: emptySet()

    fun isPackageSelected(context: Context, packageName: String): Boolean =
        getWhitelist(context).contains(packageName)

    fun setPackageSelected(context: Context, packageName: String, selected: Boolean) {
        val current = getWhitelist(context).toMutableSet()
        if (selected) current.add(packageName) else current.remove(packageName)
        prefs(context).edit().putStringSet(KEY_WHITELIST_PACKAGES, current).apply()
    }

    /**
     * 判断某个应用的通知是否允许转发到电脑。
     */
    fun isPackageAllowed(context: Context, packageName: String): Boolean {
        val whitelist = getWhitelist(context)
        if (whitelist.contains(packageName)) return true
        if (isWhitelistEnabled(context)) return false
        // 白名单模式关闭：转发用户安装的应用；系统应用默认过滤，
        // 但短信/电话/日历/闹钟等常见需同步系统应用默认放行。
        if (DEFAULT_SYSTEM_ALLOWED.contains(packageName)) return true
        return !isSystemApp(context, packageName)
    }

    fun isSystemApp(context: Context, packageName: String): Boolean {
        return try {
            val info = context.packageManager.getApplicationInfo(packageName, 0)
            (info.flags and ApplicationInfo.FLAG_SYSTEM) != 0
        } catch (_: Exception) {
            false
        }
    }

    private fun prefs(context: Context) =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
}
