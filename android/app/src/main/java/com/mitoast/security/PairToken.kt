package com.mitoast.security

import android.content.Context

/**
 * 电脑端接入配对码：手机只接受配对码正确的电脑连接。
 *
 * 首次使用时生成 6 位大写字母数字码（去掉易混淆字符），存入 SharedPreferences，
 * 显示在首页「运行状态」卡片里，用户在电脑端设置中填入。
 */
object PairToken {

    private const val PREFS_NAME = "mitoast_prefs"
    private const val KEY_PAIR_TOKEN = "pair_token"

    private const val ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    private const val LENGTH = 6

    /** 获取配对码（首次调用自动生成并持久化）。 */
    fun get(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        var token = prefs.getString(KEY_PAIR_TOKEN, null)
        if (token.isNullOrEmpty()) {
            token = generate()
            prefs.edit().putString(KEY_PAIR_TOKEN, token).apply()
        }
        return token
    }

    /** 校验电脑端发来的配对码（大小写不敏感）。 */
    fun matches(context: Context, token: String?): Boolean {
        if (token.isNullOrEmpty()) return false
        return token.trim().uppercase() == get(context)
    }

    private fun generate(): String {
        val sb = StringBuilder(LENGTH)
        val random = java.security.SecureRandom()
        repeat(LENGTH) {
            sb.append(ALPHABET[random.nextInt(ALPHABET.length)])
        }
        return sb.toString()
    }
}
