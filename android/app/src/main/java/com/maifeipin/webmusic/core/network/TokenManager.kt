package com.maifeipin.webmusic.core.network

import android.content.Context
import android.content.SharedPreferences

class TokenManager(context: Context) {
    private val prefs: SharedPreferences =
        context.getSharedPreferences("webmusic_auth_prefs", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = prefs.getString("server_url", "https://music.maifeipin.com") ?: "https://music.maifeipin.com"
        set(value) {
            val normalized = value.trimEnd('/')
            val current = prefs.getString("server_url", null)
            if (current != null && current != normalized) {
                // 切换新服务器地址时，自动清除旧凭据
                clear()
            }
            prefs.edit().putString("server_url", normalized).apply()
        }

    var token: String?
        get() = prefs.getString("jwt_token", null)
        set(value) = prefs.edit().putString("jwt_token", value).apply()

    var username: String?
        get() = prefs.getString("username", null)
        set(value) = prefs.edit().putString("username", value).apply()

    fun clear() {
        prefs.edit().remove("jwt_token").remove("username").apply()
    }
}
