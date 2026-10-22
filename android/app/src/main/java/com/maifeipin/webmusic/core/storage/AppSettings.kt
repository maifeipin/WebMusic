package com.maifeipin.webmusic.core.storage

import android.content.Context

/**
 * 与登录凭据无关的本机偏好设置。
 * 单独一份 SharedPreferences，避免和 TokenManager 的 auth 数据混在一起被 clear() 连带清空。
 */
class AppSettings private constructor(context: Context) {

    private val prefs = context.applicationContext
        .getSharedPreferences("webmusic_settings", Context.MODE_PRIVATE)

    /** 播放过的曲目自动落盘到本地离线库，之后再播直接走本地文件 */
    var autoCacheEnabled: Boolean
        get() = prefs.getBoolean(KEY_AUTO_CACHE, true)
        set(value) = prefs.edit().putBoolean(KEY_AUTO_CACHE, value).apply()

    /** 仅在 Wi-Fi 下自动缓存，避免走移动流量 */
    var autoCacheWifiOnly: Boolean
        get() = prefs.getBoolean(KEY_AUTO_CACHE_WIFI_ONLY, true)
        set(value) = prefs.edit().putBoolean(KEY_AUTO_CACHE_WIFI_ONLY, value).apply()

    companion object {
        private const val KEY_AUTO_CACHE = "auto_cache_enabled"
        private const val KEY_AUTO_CACHE_WIFI_ONLY = "auto_cache_wifi_only"

        @Volatile
        private var instance: AppSettings? = null

        fun getInstance(context: Context): AppSettings {
            return instance ?: synchronized(this) {
                instance ?: AppSettings(context).also { instance = it }
            }
        }
    }
}
