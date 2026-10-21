package com.maifeipin.webmusic.ui

import android.content.ComponentName
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import com.maifeipin.webmusic.core.lyrics.LyricLine
import com.maifeipin.webmusic.core.lyrics.LyricsParser
import com.maifeipin.webmusic.core.network.*
import com.maifeipin.webmusic.core.player.PlaybackMode
import com.maifeipin.webmusic.core.player.PlaybackQueueManager
import com.maifeipin.webmusic.service.PlaybackService
import com.maifeipin.webmusic.ui.components.FullPlayerSheet
import com.maifeipin.webmusic.ui.components.MiniPlayerBar
import com.maifeipin.webmusic.ui.theme.DarkBackground
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
    private val queueManager = PlaybackQueueManager()

    // 播放与进度状态
    private val isPlayingState = mutableStateOf(false)
    private val currentSongState = mutableStateOf<MediaFileDto?>(null)
    private val progressState = mutableStateOf(0f)
    private val durationMsState = mutableStateOf(0L)
    private val currentPositionMsState = mutableStateOf(0L)
    private val playbackModeState = mutableStateOf(PlaybackMode.SEQUENCE)
    private val lyricsState = mutableStateOf<List<LyricLine>>(emptyList())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        tokenManager = TokenManager(this)

        initMediaController()

        setContent {
            WebMusicTheme {
                MainAppScreen(
                    tokenManager = tokenManager,
                    isPlaying = isPlayingState.value,
                    currentSong = currentSongState.value,
                    progress = progressState.value,
                    durationMs = durationMsState.value,
                    currentPosMs = currentPositionMsState.value,
                    playbackMode = playbackModeState.value,
                    lyrics = lyricsState.value,
                    onPlayPauseClick = { togglePlayPause() },
                    onPreviousClick = { playPrevious() },
                    onNextClick = { playNext() },
                    onSeekTo = { posMs -> seekTo(posMs) },
                    onToggleMode = { togglePlaybackMode() },
                    onPlaySongFromList = { songList, index ->
                        queueManager.setQueue(songList, index)
                        playCurrentQueueItem()
                    }
                )
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

                override fun onPlaybackStateChanged(playbackState: Int) {
                    if (playbackState == Player.STATE_ENDED) {
                        // 播放完毕自动连播下一首
                        playNext()
                    }
                }
            })
        }, MoreExecutors.directExecutor())
    }

    private fun togglePlayPause() {
        mediaController?.let {
            if (it.isPlaying) it.pause() else it.play()
        }
    }

    private fun playNext() {
        val nextSong = queueManager.next() ?: return
        currentSongState.value = nextSong
        playCurrentQueueItem()
    }

    private fun playPrevious() {
        val prevSong = queueManager.previous() ?: return
        currentSongState.value = prevSong
        playCurrentQueueItem()
    }

    private fun togglePlaybackMode() {
        playbackModeState.value = queueManager.toggleMode()
    }

    private fun seekTo(posMs: Long) {
        mediaController?.seekTo(posMs)
    }

    private fun playCurrentQueueItem() {
        val song = queueManager.getCurrentItem() ?: return
        currentSongState.value = song

        val streamUrl = WebMusicNetwork.buildStreamUrl(tokenManager.serverUrl, song.id)
        val title = song.title ?: "未知曲目 #${song.id}"
        val artist = song.artist ?: "未知艺术家"
        val album = song.album ?: "未知专辑"

        val metadata = MediaMetadata.Builder()
            .setTitle(title)
            .setArtist(artist)
            .setAlbumTitle(album)
            .build()

        val mediaItem = MediaItem.Builder()
            .setUri(streamUrl)
            .setMediaId(song.id.toString())
            .setMediaMetadata(metadata)
            .build()

        mediaController?.run {
            setMediaItem(mediaItem)
            prepare()
            play()
        }

        // 异步获取歌词
        fetchLyrics(song.id)
    }

    private fun fetchLyrics(mediaId: Int) {
        lifecycleScope.launch {
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val resp = withContext(Dispatchers.IO) { api.getLyrics(mediaId) }
                lyricsState.value = LyricsParser.parse(resp.content)
            } catch (e: Exception) {
                lyricsState.value = emptyList()
            }
        }
    }

    private fun startProgressTracker() {
        lifecycleScope.launch {
            while (true) {
                mediaController?.let { controller ->
                    if (controller.isPlaying) {
                        val dur = controller.duration.coerceAtLeast(1L)
                        val pos = controller.currentPosition.coerceAtLeast(0L)
                        durationMsState.value = dur
                        currentPositionMsState.value = pos
                        progressState.value = (pos.toFloat() / dur.toFloat()).coerceIn(0f, 1f)
                    }
                }
                delay(300)
            }
        }
    }

    override fun onDestroy() {
        controllerFuture?.let { MediaController.releaseFuture(it) }
        super.onDestroy()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainAppScreen(
    tokenManager: TokenManager,
    isPlaying: Boolean,
    currentSong: MediaFileDto?,
    progress: Float,
    durationMs: Long,
    currentPosMs: Long,
    playbackMode: PlaybackMode,
    lyrics: List<LyricLine>,
    onPlayPauseClick: () -> Unit,
    onPreviousClick: () -> Unit,
    onNextClick: () -> Unit,
    onSeekTo: (Long) -> Unit,
    onToggleMode: () -> Unit,
    onPlaySongFromList: (List<MediaFileDto>, Int) -> Unit
) {
    val scope = rememberCoroutineScope()
    val mediaList = remember { mutableStateListOf<MediaFileDto>() }
    var currentPage by remember { mutableIntStateOf(1) }
    var totalSongs by remember { mutableIntStateOf(0) }
    var isLoading by remember { mutableStateOf(false) }
    var searchQuery by remember { mutableStateOf("") }
    var showFullPlayer by remember { mutableStateOf(false) }
    var showLoginDialog by remember { mutableStateOf(tokenManager.token == null) }

    fun loadMedia(page: Int, search: String? = null, append: Boolean = false) {
        if (tokenManager.token == null) return
        scope.launch {
            isLoading = true
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val res = withContext(Dispatchers.IO) {
                    api.getMediaFiles(page = page, pageSize = 40, search = search?.ifEmpty { null })
                }
                totalSongs = res.total
                if (!append) {
                    mediaList.clear()
                }
                mediaList.addAll(res.files)
                currentPage = page
            } catch (e: Exception) {
                // 忽略或展示错误
            } finally {
                isLoading = false
            }
        }
    }

    // 初始化拉取首批曲目
    LaunchedEffect(tokenManager.token) {
        if (tokenManager.token != null && mediaList.isEmpty()) {
            loadMedia(1)
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Text("WebMusic", fontWeight = FontWeight.Bold, color = GreenPrimary)
                },
                actions = {
                    IconButton(onClick = { showLoginDialog = true }) {
                        Icon(
                            imageVector = Icons.Default.AccountCircle,
                            contentDescription = "账号设置",
                            tint = if (tokenManager.token != null) GreenPrimary else Color.LightGray
                        )
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = DarkBackground)
            )
        },
        bottomBar = {
            if (currentSong != null) {
                MiniPlayerBar(
                    currentSong = currentSong,
                    isPlaying = isPlaying,
                    progress = progress,
                    onPlayPauseClick = onPlayPauseClick,
                    onNextClick = onNextClick,
                    onExpandFullPlayer = { showFullPlayer = true }
                )
            }
        },
        containerColor = DarkBackground
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            // 搜索框
            OutlinedTextField(
                value = searchQuery,
                onValueChange = {
                    searchQuery = it
                    loadMedia(1, it, append = false)
                },
                placeholder = { Text("搜索全库 $totalSongs 首音乐、歌手、专辑...") },
                leadingIcon = { Icon(Icons.Default.Search, contentDescription = "搜索") },
                trailingIcon = {
                    if (searchQuery.isNotEmpty()) {
                        IconButton(onClick = {
                            searchQuery = ""
                            loadMedia(1, null, append = false)
                        }) {
                            Icon(Icons.Default.Close, contentDescription = "清空")
                        }
                    }
                },
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp),
                shape = RoundedCornerShape(24.dp),
                colors = OutlinedTextFieldDefaults.colors(
                    focusedBorderColor = GreenPrimary,
                    unfocusedBorderColor = Color.DarkGray,
                    focusedContainerColor = DarkSurface,
                    unfocusedContainerColor = DarkSurface
                ),
                singleLine = true
            )

            // 歌曲列表与分页
            val listState = rememberLazyListState()

            // 监听滚动到底部自动加载下一页
            val shouldLoadMore = remember {
                derivedStateOf {
                    val lastVisibleIndex = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
                    lastVisibleIndex >= mediaList.size - 5 && !isLoading && mediaList.size < totalSongs
                }
            }

            LaunchedEffect(shouldLoadMore.value) {
                if (shouldLoadMore.value) {
                    loadMedia(currentPage + 1, searchQuery, append = true)
                }
            }

            Box(modifier = Modifier.fillMaxSize()) {
                LazyColumn(
                    state = listState,
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(bottom = 16.dp)
                ) {
                    itemsIndexed(mediaList) { index, song ->
                        val isCurrent = currentSong?.id == song.id
                        ListItem(
                            headlineContent = {
                                Text(
                                    text = song.title ?: "未知标题",
                                    color = if (isCurrent) GreenPrimary else Color.White,
                                    fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.Normal,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                            },
                            supportingContent = {
                                Text(
                                    text = "${song.artist ?: "未知艺术家"} • ${song.album ?: "未知专辑"}",
                                    color = Color.Gray,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                            },
                            leadingContent = {
                                Box(
                                    modifier = Modifier
                                        .size(40.dp)
                                        .clip(CircleShape)
                                        .background(if (isCurrent) GreenPrimary else DarkSurface),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Icon(
                                        imageVector = if (isCurrent && isPlaying) Icons.Default.VolumeUp else Icons.Default.MusicNote,
                                        contentDescription = null,
                                        tint = if (isCurrent) Color.Black else Color.Gray,
                                        modifier = Modifier.size(20.dp)
                                    )
                                }
                            },
                            modifier = Modifier
                                .clickable { onPlaySongFromList(mediaList, index) }
                                .padding(horizontal = 8.dp)
                        )
                    }

                    if (isLoading) {
                        item {
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(16.dp),
                                contentAlignment = Alignment.Center
                            ) {
                                CircularProgressIndicator(color = GreenPrimary, modifier = Modifier.size(28.dp))
                            }
                        }
                    }
                }
            }
        }
    }

    // 全屏播放器模态底栏
    if (showFullPlayer && currentSong != null) {
        FullPlayerSheet(
            currentSong = currentSong,
            isPlaying = isPlaying,
            progress = progress,
            durationMs = durationMs,
            currentPosMs = currentPosMs,
            playbackMode = playbackMode,
            lyrics = lyrics,
            onPlayPauseClick = onPlayPauseClick,
            onPreviousClick = onPreviousClick,
            onNextClick = onNextClick,
            onSeekTo = onSeekTo,
            onToggleMode = onToggleMode,
            onDismiss = { showFullPlayer = false }
        )
    }

    // 登录与设置对话框 (支持密码掩码)
    if (showLoginDialog) {
        LoginSettingDialog(
            tokenManager = tokenManager,
            onDismiss = { showLoginDialog = false },
            onLoginSuccess = {
                showLoginDialog = false
                loadMedia(1)
            }
        )
    }
}

@Composable
fun LoginSettingDialog(
    tokenManager: TokenManager,
    onDismiss: () -> Unit,
    onLoginSuccess: () -> Unit
) {
    var serverUrl by remember { mutableStateOf(tokenManager.serverUrl) }
    var username by remember { mutableStateOf(tokenManager.username ?: "") }
    var password by remember { mutableStateOf("") }
    var passwordVisible by remember { mutableStateOf(false) }
    var statusText by remember { mutableStateOf("") }
    var isLoggingIn by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("WebMusic 账号登录", fontWeight = FontWeight.Bold) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = serverUrl,
                    onValueChange = { serverUrl = it },
                    label = { Text("服务器地址") },
                    modifier = Modifier.fillMaxWidth()
                )
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    label = { Text("账号") },
                    modifier = Modifier.fillMaxWidth()
                )
                // 密码掩码类型输入框 + 显隐切换
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = { Text("密码") },
                    visualTransformation = if (passwordVisible) VisualTransformation.None else PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    trailingIcon = {
                        IconButton(onClick = { passwordVisible = !passwordVisible }) {
                            Icon(
                                imageVector = if (passwordVisible) Icons.Default.Visibility else Icons.Default.VisibilityOff,
                                contentDescription = if (passwordVisible) "隐藏密码" else "显示密码"
                            )
                        }
                    },
                    modifier = Modifier.fillMaxWidth()
                )

                if (statusText.isNotEmpty()) {
                    Text(statusText, color = GreenPrimary, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    scope.launch {
                        isLoggingIn = true
                        statusText = "正在登录..."
                        try {
                            tokenManager.serverUrl = serverUrl
                            val api = WebMusicNetwork.createApi(tokenManager)
                            val res = withContext(Dispatchers.IO) {
                                api.login(LoginRequest(username, password))
                            }
                            tokenManager.token = res.token
                            tokenManager.username = username
                            statusText = "登录成功！"
                            delay(500)
                            onLoginSuccess()
                        } catch (e: Exception) {
                            statusText = "登录失败: ${e.message}"
                        } finally {
                            isLoggingIn = false
                        }
                    }
                },
                colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                enabled = !isLoggingIn && username.isNotEmpty() && password.isNotEmpty()
            ) {
                Text("登录", color = Color.Black)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("关闭")
            }
        },
        containerColor = DarkSurface
    )
}
