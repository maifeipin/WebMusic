package com.maifeipin.webmusic.core.player

import androidx.media3.common.Player

/**
 * 播放引擎抽象契约接口
 * 保证上层业务与底层具体播放器 (Media3 / 未来 LibVLC) 彻底解耦
 */
interface IMediaPlayerAdapter {

    /** 获取底层 Player 实例，供 MediaSession 统一接管 */
    fun getPlayer(): Player

    /** 准备并播放流媒体 */
    fun playMedia(mediaUrl: String, headers: Map<String, String> = emptyMap(), title: String = "", artist: String = "")

    fun play()
    fun pause()
    fun seekTo(positionMs: Long)
    fun stop()
    fun release()

    fun isPlaying(): Boolean
    fun getCurrentPosition(): Long
    fun getDuration(): Long

    fun addListener(listener: Player.Listener)
    fun removeListener(listener: Player.Listener)
}
