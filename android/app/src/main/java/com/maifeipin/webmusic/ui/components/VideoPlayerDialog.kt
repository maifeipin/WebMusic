package com.maifeipin.webmusic.ui.components

import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import com.maifeipin.webmusic.core.cache.MediaCacheManager
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import java.io.File

/**
 * 全屏沉浸式 MV 视频播放器对话框
 * - 支持流媒体与离线本地视频播放
 * - 结合 MediaCacheManager 实现边播边存分片与 HTTP 206 Range 高效拖拽
 * - 包含全屏控制器、顶部标题栏以及离线状态指示
 */
@Composable
fun VideoPlayerDialog(
    song: MediaFileDto,
    serverUrl: String,
    token: String?,
    localFilePath: String? = null,
    onDismiss: () -> Unit
) {
    val context = LocalContext.current
    val exoPlayer = remember {
        ExoPlayer.Builder(context).build().apply {
            playWhenReady = true
        }
    }

    LaunchedEffect(song.id, localFilePath) {
        if (!localFilePath.isNullOrEmpty() && File(localFilePath).exists()) {
            val mediaItem = MediaItem.fromUri(Uri.fromFile(File(localFilePath)))
            exoPlayer.setMediaItem(mediaItem)
        } else {
            val streamUrl = "${serverUrl.trimEnd('/')}/api/media/stream/${song.id}"
            val okHttpClient = WebMusicNetwork.createClientWithToken(token ?: "")
            val cacheDataSourceFactory = MediaCacheManager.createCacheDataSourceFactory(
                context.applicationContext,
                okHttpClient
            )
            val mediaSourceFactory = DefaultMediaSourceFactory(context.applicationContext)
                .setDataSourceFactory(cacheDataSourceFactory)
            val mediaSource = mediaSourceFactory.createMediaSource(MediaItem.fromUri(Uri.parse(streamUrl)))
            exoPlayer.setMediaSource(mediaSource)
        }
        exoPlayer.prepare()
    }

    DisposableEffect(exoPlayer) {
        onDispose {
            exoPlayer.stop()
            exoPlayer.release()
        }
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            decorFitsSystemWindows = false
        )
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
        ) {
            // 视频渲染器视图
            AndroidView(
                factory = { ctx ->
                    PlayerView(ctx).apply {
                        player = exoPlayer
                        useController = true
                        setShowNextButton(false)
                        setShowPreviousButton(false)
                        controllerAutoShow = true
                        controllerShowTimeoutMs = 3500
                    }
                },
                modifier = Modifier.fillMaxSize()
            )

            // 顶部导航与视频标题信息栏
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .align(Alignment.TopCenter)
                    .background(Color.Black.copy(alpha = 0.65f))
                    .statusBarsPadding()
                    .padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = onDismiss) {
                    Icon(
                        imageVector = Icons.Default.ArrowBack,
                        contentDescription = "返回",
                        tint = Color.White
                    )
                }
                Column(
                    modifier = Modifier
                        .weight(1f)
                        .padding(start = 8.dp)
                ) {
                    Text(
                        text = song.title ?: "未知 MV",
                        color = Color.White,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Text(
                        text = "${song.artist ?: "未知艺术家"}${if (!song.album.isNullOrEmpty()) " • ${song.album}" else ""}",
                        color = Color.LightGray,
                        fontSize = 12.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }

                if (!localFilePath.isNullOrEmpty() && File(localFilePath).exists()) {
                    Surface(
                        color = GreenPrimary.copy(alpha = 0.2f),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.padding(end = 8.dp)
                    ) {
                        Text(
                            text = "已离线",
                            color = GreenPrimary,
                            fontSize = 12.sp,
                            fontWeight = FontWeight.Bold,
                            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp)
                        )
                    }
                }
            }
        }
    }
}
