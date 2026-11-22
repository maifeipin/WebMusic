package com.maifeipin.webmusic.service

import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.media3.common.Player
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import com.maifeipin.webmusic.R
import com.maifeipin.webmusic.WebMusicApplication
import com.maifeipin.webmusic.core.network.TokenManager
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import com.maifeipin.webmusic.core.player.Media3PlayerAdapter
import com.maifeipin.webmusic.ui.MainActivity

class PlaybackService : MediaSessionService() {

    private var mediaSession: MediaSession? = null
    lateinit var playerAdapter: Media3PlayerAdapter
        private set

    override fun onCreate() {
        super.onCreate()
        val tokenManager = TokenManager(this)
        val okHttpClient = WebMusicNetwork.createClient(tokenManager)
        playerAdapter = Media3PlayerAdapter(this, okHttpClient)
        activeService = this
        com.maifeipin.webmusic.core.update.PlaybackSnapshot.restore(this, playerAdapter.getPlayer())

        val sessionActivityIntent = Intent(this, MainActivity::class.java)
        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            sessionActivityIntent,
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) PendingIntent.FLAG_IMMUTABLE else 0
        )

        mediaSession = MediaSession.Builder(this, playerAdapter.getPlayer())
            .setSessionActivity(pendingIntent)
            .build()

        // 绑定音频均衡器至当前 ExoPlayer audioSessionId
        com.maifeipin.webmusic.core.audio.EqualizerManager.attachAudioSession(this, playerAdapter.audioSessionId)
        playerAdapter.getPlayer().addListener(object : androidx.media3.common.Player.Listener {
            override fun onAudioSessionIdChanged(audioSessionId: Int) {
                com.maifeipin.webmusic.core.audio.EqualizerManager.attachAudioSession(this@PlaybackService, audioSessionId)
            }
        })

        startForegroundNotification()
    }

    private fun startForegroundNotification() {
        val notification = NotificationCompat.Builder(this, WebMusicApplication.CHANNEL_ID)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle("WebMusic 播放服务就绪")
            .setContentText("准备播放流媒体...")
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()

        // 启动前台服务，保证后台运行不被系统回收
        startForeground(NOTIFICATION_ID, notification)
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? {
        return mediaSession
    }

    override fun onDestroy() {
        if (activeService === this) activeService = null
        isVideoPlayerActive = false
        activeVideoPlayer = null
        // 统一所有权释放，避免重复 release
        mediaSession?.run {
            runCatching { player = playerAdapter.getPlayer() }
            release()
            mediaSession = null
        }
        com.maifeipin.webmusic.core.audio.EqualizerManager.release()
        playerAdapter.release()
        super.onDestroy()
    }

    companion object {
        private var activeService: PlaybackService? = null
        private var activeVideoPlayer: Player? = null

        @Volatile
        var isVideoPlayerActive: Boolean = false
            private set

        /**
         * 视频播放器打开时接管唯一 MediaSession：
         * 1. 强制暂停后台音频播放器（保留其播放列表与当前进度）；
         * 2. 将系统锁屏/通知栏控制卡片切换至当前视频播放器（支持 ExoPlayer 与 LibVLC 桥接器）。
         */
        fun attachVideoPlayer(videoPlayer: Player) {
            val service = activeService ?: return
            val audioPlayer = service.playerAdapter.getPlayer()
            if (audioPlayer.isPlaying || audioPlayer.playWhenReady) {
                audioPlayer.pause()
            }
            activeVideoPlayer = videoPlayer
            isVideoPlayerActive = true
            service.mediaSession?.player = videoPlayer
        }

        /**
         * 视频播放器关闭（或音频重新发起播放）时归还 MediaSession 给音频播放器，
         * 保持音频队列与暂停位置不变。
         */
        fun detachVideoPlayer(videoPlayer: Player? = null) {
            val service = activeService ?: run {
                isVideoPlayerActive = false
                activeVideoPlayer = null
                return
            }
            if (videoPlayer != null && activeVideoPlayer != null && activeVideoPlayer !== videoPlayer) {
                return
            }
            isVideoPlayerActive = false
            activeVideoPlayer = null
            val audioPlayer = service.playerAdapter.getPlayer()
            if (service.mediaSession?.player !== audioPlayer) {
                service.mediaSession?.player = audioPlayer
            }
        }

        fun saveForUpdate() {
            val service = activeService ?: error("播放服务尚未连接，请稍后重试")
            com.maifeipin.webmusic.core.update.PlaybackSnapshot.save(service, service.playerAdapter.getPlayer())
        }
        const val NOTIFICATION_ID = 1001
    }
}
