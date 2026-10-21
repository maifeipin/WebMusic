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
import com.maifeipin.webmusic.service.PlaybackService
import com.maifeipin.webmusic.ui.components.FullPlayerSheet
import com.maifeipin.webmusic.ui.components.MiniPlayerBar
import com.maifeipin.webmusic.ui.theme.DarkBackground
import com.maifeipin.webmusic.ui.theme.DarkSurface
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import com.maifeipin.webmusic.ui.theme.WebMusicTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {

    private lateinit var tokenManager: TokenManager
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var mediaController: MediaController? = null

    // 播放与进度状态 (底层 MediaController 状态驱动，解耦 Activity 生命周期)
    private val isPlayingState = mutableStateOf(false)
    private val currentMediaItemState = mutableStateOf<MediaItem?>(null)
    private val progressState = mutableStateOf(0f)
    private val durationMsState = mutableStateOf(0L)
    private val currentPositionMsState = mutableStateOf(0L)
    private val playbackModeState = mutableStateOf(PlaybackMode.SEQUENCE)
    private val lyricsState = mutableStateOf<List<LyricLine>>(emptyList())

    // 歌词防竞态异步 Job
    private var lyricsJob: Job? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        tokenManager = TokenManager(this)

        initMediaController()

        setContent {
            WebMusicTheme {
                MainAppScreen(
                    tokenManager = tokenManager,
                    isPlaying = isPlayingState.value,
                    currentMediaItem = currentMediaItemState.value,
                    progress = progressState.value,
                    durationMs = durationMsState.value,
                    currentPosMs = currentPositionMsState.value,
                    playbackMode = playbackModeState.value,
                    lyrics = lyricsState.value,
                    onPlayPauseClick = { togglePlayPause() },
                    onPreviousClick = { mediaController?.seekToPreviousMediaItem() },
                    onNextClick = { mediaController?.seekToNextMediaItem() },
                    onSeekTo = { posMs -> mediaController?.seekTo(posMs) },
                    onToggleMode = { togglePlaybackMode() },
                    onPlaySongFromList = { songList, index ->
                        playQueue(songList, index)
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
            val controller = controllerFuture?.get()
            mediaController = controller
            controller?.addListener(object : Player.Listener {
                override fun onIsPlayingChanged(isPlaying: Boolean) {
                    isPlayingState.value = isPlaying
                }

                override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                    currentMediaItemState.value = mediaItem
                    val songId = mediaItem?.mediaId?.toIntOrNull()
                    if (songId != null) {
                        fetchLyricsSafely(songId)
                    } else {
                        lyricsState.value = emptyList()
                    }
                }

                override fun onRepeatModeChanged(repeatMode: Int) {
                    syncPlaybackMode(controller)
                }

                override fun onShuffleModeEnabledChanged(shuffleModeEnabled: Boolean) {
                    syncPlaybackMode(controller)
                }
            })

            // 初始化与 Activity 重建状态恢复
            controller?.let { c ->
                isPlayingState.value = c.isPlaying
                currentMediaItemState.value = c.currentMediaItem

                // 1. 底层默认列表循环：若未设置模式则设为 REPEAT_MODE_ALL，与 UI 默认契约对齐
                if (c.repeatMode == Player.REPEAT_MODE_OFF && !c.shuffleModeEnabled) {
                    c.repeatMode = Player.REPEAT_MODE_ALL
                }
                syncPlaybackMode(c)

                // 2. Activity 重建后主动恢复当前正在播放歌曲的歌词
                c.currentMediaItem?.mediaId?.toIntOrNull()?.let { songId ->
                    fetchLyricsSafely(songId)
                }
            }
        }, MoreExecutors.directExecutor())
    }

    private fun syncPlaybackMode(controller: MediaController) {
        playbackModeState.value = when {
            controller.shuffleModeEnabled -> PlaybackMode.SHUFFLE
            controller.repeatMode == Player.REPEAT_MODE_ONE -> PlaybackMode.REPEAT_ONE
            else -> PlaybackMode.SEQUENCE
        }
    }

    private fun togglePlayPause() {
        mediaController?.let {
            if (it.isPlaying) it.pause() else it.play()
        }
    }

    private fun togglePlaybackMode() {
        val controller = mediaController ?: return
        val nextMode = when (playbackModeState.value) {
            PlaybackMode.SEQUENCE -> PlaybackMode.REPEAT_ONE
            PlaybackMode.REPEAT_ONE -> PlaybackMode.SHUFFLE
            PlaybackMode.SHUFFLE -> PlaybackMode.SEQUENCE
        }
        playbackModeState.value = nextMode

        when (nextMode) {
            PlaybackMode.SEQUENCE -> {
                controller.repeatMode = Player.REPEAT_MODE_ALL
                controller.shuffleModeEnabled = false
            }
            PlaybackMode.REPEAT_ONE -> {
                controller.repeatMode = Player.REPEAT_MODE_ONE
                controller.shuffleModeEnabled = false
            }
            PlaybackMode.SHUFFLE -> {
                controller.repeatMode = Player.REPEAT_MODE_ALL
                controller.shuffleModeEnabled = true
            }
        }
    }

    /**
     * 将整个播放队列交付底层 ExoPlayer 统一管理
     * 解决 Activity 销毁断连、锁屏切歌无感知问题
     * 注入无碰撞 SHA-256 隔离缓存键 (ServerUrl + Username + MediaId + FileHash/SizeBytes)
     */
    private fun playQueue(songList: List<MediaFileDto>, startIndex: Int) {
        val controller = mediaController ?: return
        val serverUrl = tokenManager.serverUrl
        val username = tokenManager.username

        val mediaItems = songList.map { song ->
            val streamUrl = WebMusicNetwork.buildStreamUrl(serverUrl, song.id)
            val metadata = MediaMetadata.Builder()
                .setTitle(song.title ?: "未知曲目 #${song.id}")
                .setArtist(song.artist ?: "未知艺术家")
                .setAlbumTitle(song.album ?: "未知专辑")
                .build()

            val safeCacheKey = WebMusicNetwork.generateSafeCacheKey(serverUrl, username, song)

            MediaItem.Builder()
                .setUri(streamUrl)
                .setMediaId(song.id.toString())
                .setCustomCacheKey(safeCacheKey)
                .setMediaMetadata(metadata)
                .build()
        }

        // 注入默认模式：保证首次播放默认享受列表循环
        if (controller.repeatMode == Player.REPEAT_MODE_OFF && !controller.shuffleModeEnabled) {
            controller.repeatMode = Player.REPEAT_MODE_ALL
        }

        controller.setMediaItems(mediaItems, startIndex, 0L)
        controller.prepare()
        controller.play()
    }

    /**
     * 安全获取歌词：取消旧请求，并严格校验响应 ID 是否匹配当前正在播放的曲目
     */
    private fun fetchLyricsSafely(targetMediaId: Int) {
        lyricsJob?.cancel()
        lyricsState.value = emptyList() // 切歌先清空，防止旧歌词闪烁

        lyricsJob = lifecycleScope.launch {
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val resp = withContext(Dispatchers.IO) { api.getLyrics(targetMediaId) }
                if (isActive && currentMediaItemState.value?.mediaId?.toIntOrNull() == targetMediaId) {
                    lyricsState.value = LyricsParser.parse(resp.content)
                }
            } catch (e: Exception) {
                if (isActive) {
                    lyricsState.value = emptyList()
                }
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
    currentMediaItem: MediaItem?,
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
    var confirmedQuery by remember { mutableStateOf("") }
    var isDebouncingSearch by remember { mutableStateOf(false) }
    var queryVersion by remember { mutableLongStateOf(0L) }

    var showFullPlayer by remember { mutableStateOf(false) }
    var showLoginDialog by remember { mutableStateOf(tokenManager.token == null) }

    // 活跃请求 Job，保证单一请求链路
    var activeFetchJob by remember { mutableStateOf<Job?>(null) }

    fun executeQuery(page: Int, search: String?, append: Boolean, targetVersion: Long) {
        if (tokenManager.token == null) return
        activeFetchJob?.cancel()
        activeFetchJob = scope.launch {
            isLoading = true
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val res = withContext(Dispatchers.IO) {
                    api.getMediaFiles(page = page, pageSize = 40, search = search?.ifEmpty { null })
                }
                // 严格校验请求版本：过时的搜索响应或不同关键词的旧页响应直接作废
                if (isActive && targetVersion == queryVersion) {
                    totalSongs = res.total
                    if (!append) {
                        mediaList.clear()
                    }
                    mediaList.addAll(res.files)
                    currentPage = page
                    confirmedQuery = search ?: ""
                }
            } catch (e: Exception) {
                // 网络异常捕获
            } finally {
                if (isActive && targetVersion == queryVersion) {
                    isLoading = false
                    isDebouncingSearch = false
                }
            }
        }
    }

    // 初始化拉取
    LaunchedEffect(tokenManager.token) {
        if (tokenManager.token != null && mediaList.isEmpty()) {
            queryVersion++
            executeQuery(1, null, append = false, queryVersion)
        }
    }

    val currentSongDto = remember(currentMediaItem) {
        if (currentMediaItem == null) null
        else MediaFileDto(
            id = currentMediaItem.mediaId.toIntOrNull() ?: 0,
            filePath = null,
            title = currentMediaItem.mediaMetadata.title?.toString(),
            artist = currentMediaItem.mediaMetadata.artist?.toString(),
            album = currentMediaItem.mediaMetadata.albumTitle?.toString(),
            genre = null,
            year = null,
            duration = null,
            sizeBytes = null,
            fileHash = null
        )
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
            if (currentSongDto != null) {
                MiniPlayerBar(
                    currentSong = currentSongDto,
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
            // 搜索框：版本隔离与防抖机制，防抖期间锁定底部分页抢占
            OutlinedTextField(
                value = searchQuery,
                onValueChange = { newQuery ->
                    searchQuery = newQuery
                    isDebouncingSearch = true // 标记处于防抖中，防止旧列表底部抢先触发分页
                    activeFetchJob?.cancel()
                    val version = ++queryVersion
                    scope.launch {
                        delay(400) // 400ms 防抖
                        if (isActive && version == queryVersion) {
                            executeQuery(1, newQuery, append = false, version)
                        }
                    }
                },
                placeholder = { Text("搜索全库 $totalSongs 首音乐、歌手、专辑...") },
                leadingIcon = { Icon(Icons.Default.Search, contentDescription = "搜索") },
                trailingIcon = {
                    if (searchQuery.isNotEmpty()) {
                        IconButton(onClick = {
                            searchQuery = ""
                            isDebouncingSearch = true
                            activeFetchJob?.cancel()
                            val version = ++queryVersion
                            executeQuery(1, null, append = false, version)
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

            // 仅当非防抖、非加载中且属于已确认查询时，才允许触发下一页加载
            val shouldLoadMore = remember {
                derivedStateOf {
                    val lastVisibleIndex = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
                    lastVisibleIndex >= mediaList.size - 5 && !isLoading && !isDebouncingSearch && mediaList.size < totalSongs
                }
            }

            LaunchedEffect(shouldLoadMore.value) {
                if (shouldLoadMore.value && !isDebouncingSearch) {
                    val version = queryVersion
                    executeQuery(currentPage + 1, confirmedQuery, append = true, version)
                }
            }

            Box(modifier = Modifier.fillMaxSize()) {
                LazyColumn(
                    state = listState,
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(bottom = 16.dp)
                ) {
                    itemsIndexed(mediaList) { index, song ->
                        val isCurrent = currentSongDto?.id == song.id
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

    // 全屏沉浸播放器
    if (showFullPlayer && currentSongDto != null) {
        FullPlayerSheet(
            currentSong = currentSongDto,
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

    // 登录与设置对话框 (密码掩码 + 显隐切换)
    if (showLoginDialog) {
        LoginSettingDialog(
            tokenManager = tokenManager,
            onDismiss = { showLoginDialog = false },
            onLoginSuccess = {
                showLoginDialog = false
                val v = ++queryVersion
                executeQuery(1, null, append = false, v)
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

                val context = androidx.compose.ui.platform.LocalContext.current
                var cacheSizeBytes by remember {
                    mutableLongStateOf(com.maifeipin.webmusic.core.cache.MediaCacheManager.getCacheSizeBytes(context))
                }

                HorizontalDivider(color = Color.DarkGray, modifier = Modifier.padding(vertical = 4.dp))

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "本地缓存: ${"%.1f".format(cacheSizeBytes / (1024f * 1024f))} MB",
                        color = Color.Gray,
                        fontSize = 13.sp
                    )
                    TextButton(onClick = {
                        com.maifeipin.webmusic.core.cache.MediaCacheManager.clearAllCache(context)
                        cacheSizeBytes = com.maifeipin.webmusic.core.cache.MediaCacheManager.getCacheSizeBytes(context)
                        statusText = "已清空本地流媒体缓存！"
                    }) {
                        Text("清空缓存", color = Color(0xFFFF5252), fontSize = 13.sp)
                    }
                }

                if (tokenManager.token != null) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.End
                    ) {
                        TextButton(onClick = {
                            tokenManager.clear()
                            statusText = "已退出当前登录"
                            onLoginSuccess()
                        }) {
                            Text("退出登录", color = Color.Gray, fontSize = 13.sp)
                        }
                    }
                }

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
