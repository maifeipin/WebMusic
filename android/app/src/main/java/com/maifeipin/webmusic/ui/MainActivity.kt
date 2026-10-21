package com.maifeipin.webmusic.ui

import android.content.ComponentName
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import com.maifeipin.webmusic.core.network.*
import com.maifeipin.webmusic.service.PlaybackService
import com.maifeipin.webmusic.ui.theme.DarkSurface
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import com.maifeipin.webmusic.ui.theme.WebMusicTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {

    private lateinit var tokenManager: TokenManager
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var mediaController: MediaController? = null

    // 播放状态监听
    private val isPlayingState = mutableStateOf(false)
    private val currentMediaTitle = mutableStateOf("未播放")
    private val currentMediaArtist = mutableStateOf("")
    private val currentProgress = mutableStateOf(0f)
    private val durationMs = mutableStateOf(0L)
    private val currentPositionMs = mutableStateOf(0L)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        tokenManager = TokenManager(this)

        initMediaController()

        setContent {
            WebMusicTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    SpikeConsoleScreen(
                        tokenManager = tokenManager,
                        isPlaying = isPlayingState.value,
                        title = currentMediaTitle.value,
                        artist = currentMediaArtist.value,
                        progress = currentProgress.value,
                        duration = durationMs.value,
                        currentPos = currentPositionMs.value,
                        onPlayPauseClick = { togglePlayPause() },
                        onSeekTo = { posMs -> seekTo(posMs) },
                        onPlayItem = { item -> playMediaItem(item) }
                    )
                }
            }
        }

        startProgressTracker()
    }

    private fun initMediaController() {
        val sessionToken = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        controllerFuture = MediaController.Builder(this, sessionToken).buildAsync()
        controllerFuture?.addListener({
            mediaController = controllerFuture?.get()
            mediaController?.addListener(object : Player.Listener {
                override fun onIsPlayingChanged(isPlaying: Boolean) {
                    isPlayingState.value = isPlaying
                }

                override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                    currentMediaTitle.value = mediaItem?.mediaMetadata?.title?.toString() ?: "未知曲目"
                    currentMediaArtist.value = mediaItem?.mediaMetadata?.artist?.toString() ?: ""
                }
            })
        }, MoreExecutors.directExecutor())
    }

    private fun togglePlayPause() {
        mediaController?.let {
            if (it.isPlaying) it.pause() else it.play()
        }
    }

    private fun seekTo(posMs: Long) {
        mediaController?.seekTo(posMs)
    }

    private fun playMediaItem(item: MediaFileDto) {
        val streamUrl = WebMusicNetwork.buildStreamUrl(
            tokenManager.serverUrl,
            item.id
        )
        val title = item.title ?: "未知曲目 #${item.id}"
        val artist = item.artist ?: "未知艺术家"
        val album = item.album ?: "未知专辑"

        currentMediaTitle.value = title
        currentMediaArtist.value = artist

        // 构造包含完整元数据的 MediaMetadata，确保系统锁屏与通知栏正确展示曲目信息
        val metadata = MediaMetadata.Builder()
            .setTitle(title)
            .setArtist(artist)
            .setAlbumTitle(album)
            .build()

        val mediaItem = MediaItem.Builder()
            .setUri(streamUrl)
            .setMediaId(item.id.toString())
            .setMediaMetadata(metadata)
            .build()

        mediaController?.run {
            setMediaItem(mediaItem)
            prepare()
            play()
        }
    }

    private fun startProgressTracker() {
        lifecycleScope.launch {
            while (true) {
                mediaController?.let { controller ->
                    if (controller.isPlaying) {
                        val dur = controller.duration.coerceAtLeast(1L)
                        val pos = controller.currentPosition.coerceAtLeast(0L)
                        durationMs.value = dur
                        currentPositionMs.value = pos
                        currentProgress.value = (pos.toFloat() / dur.toFloat()).coerceIn(0f, 1f)
                    }
                }
                delay(500)
            }
        }
    }

    override fun onDestroy() {
        controllerFuture?.let { MediaController.releaseFuture(it) }
        super.onDestroy()
    }
}

@Composable
fun SpikeConsoleScreen(
    tokenManager: TokenManager,
    isPlaying: Boolean,
    title: String,
    artist: String,
    progress: Float,
    duration: Long,
    currentPos: Long,
    onPlayPauseClick: () -> Unit,
    onSeekTo: (Long) -> Unit,
    onPlayItem: (MediaFileDto) -> Unit
) {
    var serverUrl by remember { mutableStateOf(tokenManager.serverUrl) }
    var username by remember { mutableStateOf(tokenManager.username ?: "") }
    var password by remember { mutableStateOf("") }
    var loginStatus by remember { mutableStateOf(if (tokenManager.token != null) "已登录 (Token 保存有效)" else "未登录") }
    val mediaList = remember { mutableStateListOf<MediaFileDto>() }
    var isLoadingList by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp)
    ) {
        Text("WebMusic Spike 验证控制台", style = MaterialTheme.typography.titleLarge, color = GreenPrimary)
        Text("Phase 0: 核心 Range 流媒体 & 音频焦点 & 锁屏验证", style = MaterialTheme.typography.bodySmall, color = Color.Gray)

        Spacer(modifier = Modifier.height(12.dp))

        // 服务端配置与登录卡片
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(containerColor = DarkSurface)
        ) {
            Column(modifier = Modifier.padding(12.dp)) {
                OutlinedTextField(
                    value = serverUrl,
                    onValueChange = {
                        serverUrl = it
                        tokenManager.serverUrl = it
                    },
                    label = { Text("服务端 Base URL") },
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(modifier = Modifier.height(8.dp))
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(
                        value = username,
                        onValueChange = { username = it },
                        label = { Text("账号") },
                        modifier = Modifier.weight(1f)
                    )
                    OutlinedTextField(
                        value = password,
                        onValueChange = { password = it },
                        label = { Text("密码") },
                        modifier = Modifier.weight(1f)
                    )
                }
                Spacer(modifier = Modifier.height(8.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Button(
                        onClick = {
                            scope.launch {
                                try {
                                    val api = WebMusicNetwork.createApi(tokenManager)
                                    val res = withContext(Dispatchers.IO) {
                                        api.login(LoginRequest(username, password))
                                    }
                                    tokenManager.token = res.token
                                    tokenManager.username = username
                                    loginStatus = "登录成功! Token 已保存"
                                } catch (e: Exception) {
                                    loginStatus = "登录失败: ${e.message}"
                                }
                            }
                        },
                        colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary)
                    ) {
                        Text("登录获取 Token", color = Color.Black)
                    }
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(loginStatus, style = MaterialTheme.typography.bodySmall, color = Color.LightGray)
                }
            }
        }

        Spacer(modifier = Modifier.height(12.dp))

        // 媒体库拉取与列表 (对齐后端 GET /api/media)
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("媒体库曲目 (/api/media)", style = MaterialTheme.typography.titleMedium)
            IconButton(onClick = {
                scope.launch {
                    isLoadingList = true
                    try {
                        val api = WebMusicNetwork.createApi(tokenManager)
                        val pagedResult = withContext(Dispatchers.IO) {
                            api.getMediaFiles(page = 1, pageSize = 30)
                        }
                        mediaList.clear()
                        mediaList.addAll(pagedResult.files)
                        loginStatus = "拉取成功: 共 ${pagedResult.total} 首歌曲"
                    } catch (e: Exception) {
                        loginStatus = "获取媒体失败: ${e.message} (若401请重新登录)"
                    } finally {
                        isLoadingList = false
                    }
                }
            }) {
                Icon(Icons.Default.Refresh, contentDescription = "拉取曲目")
            }
        }

        LazyColumn(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
        ) {
            items(mediaList) { item ->
                ListItem(
                    headlineContent = { Text(item.title ?: "未知标题", color = Color.White) },
                    supportingContent = { Text("${item.artist ?: "未知艺术家"} • ${item.album ?: "未知专辑"}", color = Color.Gray) },
                    modifier = Modifier
                        .clickable { onPlayItem(item) }
                        .padding(vertical = 4.dp)
                )
            }
        }

        // 底部播放控制条
        Card(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 8.dp),
            colors = CardDefaults.cardColors(containerColor = DarkSurface)
        ) {
            Column(modifier = Modifier.padding(12.dp)) {
                Text(title, style = MaterialTheme.typography.titleMedium, color = GreenPrimary)
                if (artist.isNotEmpty()) {
                    Text(artist, style = MaterialTheme.typography.bodySmall, color = Color.LightGray)
                }
                Spacer(modifier = Modifier.height(4.dp))
                Slider(
                    value = progress,
                    onValueChange = { newProgress ->
                        val targetMs = (newProgress * duration).toLong()
                        onSeekTo(targetMs)
                    },
                    modifier = Modifier.fillMaxWidth()
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        "${formatTime(currentPos)} / ${formatTime(duration)}",
                        style = MaterialTheme.typography.bodySmall,
                        color = Color.Gray
                    )
                    IconButton(onClick = onPlayPauseClick) {
                        Icon(
                            if (isPlaying) Icons.Default.Pause else Icons.Default.PlayArrow,
                            contentDescription = "播放/暂停",
                            tint = GreenPrimary
                        )
                    }
                }
            }
        }
    }
}

private fun formatTime(ms: Long): String {
    val totalSeconds = (ms / 1000).toInt()
    val minutes = totalSeconds / 60
    val seconds = totalSeconds % 60
    return String.format("%02d:%02d", minutes, seconds)
}
