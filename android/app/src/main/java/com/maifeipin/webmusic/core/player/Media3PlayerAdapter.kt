package com.maifeipin.webmusic.core.player

import android.content.Context
import android.net.Uri
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
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
        // 配置渲染工厂：优先 MediaCodec 系统硬件解码，保障省电与低发热
        val renderersFactory = DefaultRenderersFactory(context.applicationContext).apply {
            setEnableAudioTrackPlaybackParams(true)
        }

        // 使用支持 HTTP 206 Range 与透明边播边存的 LRU 缓存数据源
        val dataSourceFactory = com.maifeipin.webmusic.core.cache.MediaCacheManager
            .createCacheDataSourceFactory(context.applicationContext, okHttpClient)
        val mediaSourceFactory = DefaultMediaSourceFactory(context.applicationContext)
            .setDataSourceFactory(dataSourceFactory)

        // 配置标准媒体音频属性
        val audioAttributes = AudioAttributes.Builder()
            .setUsage(C.USAGE_MEDIA)
            .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
            .build()

        player = ExoPlayer.Builder(context.applicationContext, renderersFactory)
            .setMediaSourceFactory(mediaSourceFactory)
            .setAudioAttributes(audioAttributes, /* handleAudioFocus = */ true) // 完整接管系统音频焦点 (来电/导航避让)
            .setHandleAudioBecomingNoisy(true) // 耳机拔出防外放
            .setWakeMode(C.WAKE_MODE_NETWORK) // 防止系统深度休眠断流
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
