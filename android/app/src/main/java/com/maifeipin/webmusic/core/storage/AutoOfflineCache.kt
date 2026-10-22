package com.maifeipin.webmusic.core.storage

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.util.Log
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.TokenManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.concurrent.ConcurrentHashMap

/**
 * 播放即缓存（"听过就离线"）。
 *
 * 服务端首包偶发 5~6 秒（SMB 冷读超时 → 500），ExoPlayer 退避重试会把感知延迟放大到十几秒。
 * 这里在播放开始后把整首落盘到本地离线库，下次播放直接读本地文件，彻底绕开该抖动。
 *
 * 设计约束：
 * 1. 延迟 [START_DELAY_MS] 再开始，先让在线首播拿到带宽；若此时已切歌则放弃本次缓存。
 * 2. 全局单并发（[downloadMutex]），不会和下一首的缓存抢带宽。
 * 3. 只缓存"当前仍在播放"的曲目，快速划过歌曲不会产生大量垃圾任务。
 * 4. 与手动下载撞车时的去重不在本类：`OfflineDownloadManager.downloadSong()` 内部按
 *    （服务器 + 账号 + 曲目）原子去重，这里只会复用同一个在途传输的结果。
 */
object AutoOfflineCache {

    private const val TAG = "AutoOfflineCache"
    private const val START_DELAY_MS = 4_000L

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val downloadMutex = Mutex()
    private val inFlight = ConcurrentHashMap.newKeySet<Int>()

    private val _cachedSongIds = MutableStateFlow<Set<Int>>(emptySet())

    /** 已由自动缓存落盘的曲目 id，UI 可据此提示/刷新列表 */
    val cachedSongIds: StateFlow<Set<Int>> = _cachedSongIds

    /**
     * 在曲目开始播放时调用。
     *
     * @param isStillCurrent 延迟结束后用于确认用户没有切走，返回 false 则放弃本次缓存
     */
    fun onTrackStarted(
        context: Context,
        tokenManager: TokenManager,
        song: MediaFileDto,
        isStillCurrent: () -> Boolean
    ) {
        val appContext = context.applicationContext
        val settings = AppSettings.getInstance(appContext)
        if (!settings.autoCacheEnabled) return
        if (song.id <= 0) return
        if (inFlight.contains(song.id)) return

        // 快照凭据，避免下载过程中切换账号导致串号
        val serverUrl = tokenManager.serverUrl
        val username = tokenManager.username
        val token = tokenManager.token
        if (token.isNullOrEmpty()) return

        scope.launch {
            if (inFlight.contains(song.id)) return@launch
            if (OfflineDownloadManager.isDownloaded(appContext, serverUrl, username, song.id)) {
                _cachedSongIds.update { it + song.id }
                return@launch
            }
            if (settings.autoCacheWifiOnly && !isOnWifi(appContext)) return@launch

            delay(START_DELAY_MS)
            if (!isStillCurrent()) return@launch

            inFlight.add(song.id)
            try {
                downloadMutex.withLock {
                    if (!isStillCurrent()) return@withLock
                    val result = OfflineDownloadManager.downloadSong(appContext, tokenManager, song)
                    result.onSuccess {
                        _cachedSongIds.update { ids -> ids + song.id }
                        Log.i(TAG, "Auto cached song ${song.id} (${it.sizeBytes} bytes)")
                    }.onFailure { e ->
                        // 缓存失败不影响在线播放，仅记录
                        Log.w(TAG, "Auto cache failed for song ${song.id}: ${e.message}")
                    }
                }
            } finally {
                inFlight.remove(song.id)
            }
        }
    }

    private fun isOnWifi(context: Context): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return false
        val network = cm.activeNetwork ?: return false
        val caps = cm.getNetworkCapabilities(network) ?: return false
        return caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
    }
}
