package com.maifeipin.webmusic.service

import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
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
        // 统一所有权释放，避免重复 release
        mediaSession?.run {
            release()
            mediaSession = null
        }
        com.maifeipin.webmusic.core.audio.EqualizerManager.release()
        playerAdapter.release()
        super.onDestroy()
    }

    companion object {
        private var activeService: PlaybackService? = null
        fun saveForUpdate() {
            val service = activeService ?: error("播放服务尚未连接，请稍后重试")
            com.maifeipin.webmusic.core.update.PlaybackSnapshot.save(service, service.playerAdapter.getPlayer())
        }
        const val NOTIFICATION_ID = 1001
    }
}
