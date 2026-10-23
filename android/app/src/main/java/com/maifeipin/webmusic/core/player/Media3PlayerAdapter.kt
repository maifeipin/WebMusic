package com.maifeipin.webmusic.core.player

import android.content.Context
import android.net.Uri
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import com.maifeipin.webmusic.core.cache.MediaCacheManager
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

class Media3PlayerAdapter(
    private val context: Context,
    private val okHttpClient: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()
) : IMediaPlayerAdapter {

    private val player: ExoPlayer

    init {
        // 配置渲染工厂：优先 MediaCodec 系统硬件解码
        val renderersFactory = DefaultRenderersFactory(context.applicationContext).apply {
            setEnableAudioTrackPlaybackParams(true)
        }

        // 使用支持本地协议路由（file://, content://）与 HTTP 透明边播边存的数据源
        val dataSourceFactory = MediaCacheManager.createRoutedDataSourceFactory(
            context.applicationContext,
            okHttpClient
        )
        val mediaSourceFactory = DefaultMediaSourceFactory(context.applicationContext)
            .setDataSourceFactory(dataSourceFactory)

        // 配置标准媒体音频属性
        val audioAttributes = AudioAttributes.Builder()
            .setUsage(C.USAGE_MEDIA)
            .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
            .build()

        player = ExoPlayer.Builder(context.applicationContext, renderersFactory)
            .setMediaSourceFactory(mediaSourceFactory)
            .setAudioAttributes(audioAttributes, /* handleAudioFocus = */ true)
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
    }

    override fun getPlayer(): Player = player

    override fun playMedia(
        mediaUrl: String,
        headers: Map<String, String>,
        title: String,
        artist: String
    ) {
        val metadata = MediaMetadata.Builder()
            .setTitle(title.ifEmpty { "WebMusic 音频" })
            .setArtist(artist.ifEmpty { "未知艺术家" })
            .build()

        val mediaItem = MediaItem.Builder()
            .setUri(Uri.parse(mediaUrl))
            .setMediaMetadata(metadata)
            .build()

        player.setMediaItem(mediaItem)
        player.prepare()
        player.play()
    }

    /**
     * 将播放队列注入底层 ExoPlayer，实现真后台连播与锁屏上一曲/下一曲无缝支持
     * 并加入隔离缓存键：防止同服务器多账号串用与文件版本脏读
     */
    fun setPlaylist(
        songs: List<MediaFileDto>,
        startIndex: Int,
        serverUrl: String,
        username: String?
    ) {
        val mediaItems = songs.map { song ->
            val mediaUri = if (song.filePath != null && song.filePath.startsWith("/") && java.io.File(song.filePath).exists()) {
                Uri.fromFile(java.io.File(song.filePath))
            } else {
                Uri.parse(WebMusicNetwork.buildStreamUrl(serverUrl, song.id))
            }

            val metadata = MediaMetadata.Builder()
                .setTitle(song.title ?: "未知曲目 #${song.id}")
                .setArtist(song.artist ?: "未知艺术家")
                .setAlbumTitle(song.album ?: "未知专辑")
                .build()

            val cacheKey = WebMusicNetwork.generateSafeCacheKey(serverUrl, username, song)

            MediaItem.Builder()
                .setUri(mediaUri)
                .setMediaId(song.id.toString())
                .setCustomCacheKey(cacheKey)
                .setMediaMetadata(metadata)
                .build()
        }

        player.setMediaItems(mediaItems, startIndex, /* startPositionMs = */ 0L)
        player.prepare()
        player.play()
    }

    val audioSessionId: Int get() = player.audioSessionId

    fun setPlaybackMode(mode: PlaybackMode) {
        when (mode) {
            PlaybackMode.SEQUENCE -> {
                player.repeatMode = Player.REPEAT_MODE_ALL
                player.shuffleModeEnabled = false
            }
            PlaybackMode.REPEAT_ONE -> {
                player.repeatMode = Player.REPEAT_MODE_ONE
                player.shuffleModeEnabled = false
            }
            PlaybackMode.SHUFFLE -> {
                player.repeatMode = Player.REPEAT_MODE_ALL
                player.shuffleModeEnabled = true
            }
        }
    }

    fun seekToNext() {
        if (player.hasNextMediaItem()) {
            player.seekToNextMediaItem()
        }
    }

    fun seekToPrevious() {
        if (player.hasPreviousMediaItem()) {
            player.seekToPreviousMediaItem()
        }
    }

    override fun play() {
        player.play()
    }

    override fun pause() {
        player.pause()
    }

    override fun seekTo(positionMs: Long) {
        player.seekTo(positionMs)
    }

    override fun stop() {
        player.stop()
    }

    override fun release() {
        player.release()
    }

    override fun isPlaying(): Boolean = player.isPlaying

    override fun getCurrentPosition(): Long = player.currentPosition

    override fun getDuration(): Long = player.duration.coerceAtLeast(0L)

    override fun addListener(listener: Player.Listener) {
        player.addListener(listener)
    }

    override fun removeListener(listener: Player.Listener) {
        player.removeListener(listener)
    }
}
