package com.maifeipin.webmusic.core.update

import android.content.Context
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import com.maifeipin.webmusic.core.network.TokenManager
import org.json.JSONArray
import org.json.JSONObject

/** One-shot, account-bound upgrade snapshot. Restore paused; never autoplay after installation. */
object PlaybackSnapshot {
    private const val PREFS = "upgrade_playback"

    fun save(context: Context, player: Player) {
        val auth = TokenManager(context)
        val items = JSONArray()
        for (i in 0 until player.mediaItemCount) {
            val item = player.getMediaItemAt(i)
            val config = item.localConfiguration ?: error("播放队列缺少媒体地址，无法保存")
            items.put(JSONObject().put("id", item.mediaId).put("uri", config.uri.toString())
                .put("key", config.customCacheKey ?: "")
                .put("title", item.mediaMetadata.title?.toString() ?: "")
                .put("artist", item.mediaMetadata.artist?.toString() ?: "")
                .put("album", item.mediaMetadata.albumTitle?.toString() ?: ""))
        }
        val snapshot = JSONObject().put("savedVersion", com.maifeipin.webmusic.BuildConfig.VERSION_CODE).put("server", auth.serverUrl).put("user", auth.username ?: "")
            .put("items", items).put("index", player.currentMediaItemIndex.coerceAtLeast(0))
            .put("position", player.currentPosition.coerceAtLeast(0))
            .put("repeat", player.repeatMode).put("shuffle", player.shuffleModeEnabled)
        check(context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString("snapshot", snapshot.toString()).commit()) { "无法保存升级前播放状态" }
    }

    fun restore(context: Context, player: Player) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val raw = prefs.getString("snapshot", null) ?: return
        val auth = TokenManager(context)
        if (runCatching { JSONObject(raw).getInt("savedVersion") == com.maifeipin.webmusic.BuildConfig.VERSION_CODE }.getOrDefault(false)) return
        runCatching {
            val data = JSONObject(raw)
            if (data.getString("server") != auth.serverUrl || data.getString("user") != (auth.username ?: "")) return@runCatching
            val array = data.getJSONArray("items")
            val items = (0 until array.length()).map { i ->
                val item = array.getJSONObject(i)
                MediaItem.Builder().setMediaId(item.getString("id")).setUri(item.getString("uri"))
                    .apply { item.optString("key").takeIf { it.isNotEmpty() }?.let { setCustomCacheKey(it) } }
                    .setMediaMetadata(MediaMetadata.Builder().setTitle(item.optString("title"))
                        .setArtist(item.optString("artist")).setAlbumTitle(item.optString("album")).build()).build()
            }
            if (items.isNotEmpty()) {
                player.setMediaItems(items, data.getInt("index").coerceIn(items.indices), data.getLong("position"))
                player.repeatMode = data.getInt("repeat")
                player.shuffleModeEnabled = data.getBoolean("shuffle")
                player.playWhenReady = false
                player.prepare()
            }
        }
        prefs.edit().remove("snapshot").commit()
    }
}
