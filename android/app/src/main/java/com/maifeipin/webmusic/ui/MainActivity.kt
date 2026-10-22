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
import com.maifeipin.webmusic.ui.components.AddToPlaylistDialog
import com.maifeipin.webmusic.ui.components.FullPlayerSheet
import com.maifeipin.webmusic.ui.components.MiniPlayerBar
import com.maifeipin.webmusic.ui.components.PlaylistDetailView
import java.util.concurrent.CancellationException
import com.maifeipin.webmusic.ui.theme.DarkBackground
import com.maifeipin.webmusic.ui.theme.DarkSurface
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import com.maifeipin.webmusic.ui.theme.WebMusicTheme
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import com.maifeipin.webmusic.core.storage.DownloadedSong
import com.maifeipin.webmusic.core.storage.OfflineDownloadManager
import com.maifeipin.webmusic.core.storage.toMediaFileDto
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
                    onClearSession = { clearPlaybackSession() },
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
            // 优先检查本地离线数据库是否已固化该曲目的 LRC 歌词
            val offlineSong = withContext(Dispatchers.IO) {
                OfflineDownloadManager.getDownloadedSong(this@MainActivity, tokenManager.serverUrl, tokenManager.username, targetMediaId)
            }
            if (offlineSong?.lyrics != null) {
                if (isActive && currentMediaItemState.value?.mediaId?.toIntOrNull() == targetMediaId) {
                    lyricsState.value = LyricsParser.parse(offlineSong.lyrics)
                }
                return@launch
            }

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

    private fun clearPlaybackSession() {
        lyricsJob?.cancel()
        mediaController?.let { controller ->
            controller.stop()
            controller.clearMediaItems()
        }
        isPlayingState.value = false
        currentMediaItemState.value = null
        progressState.value = 0f
        durationMsState.value = 0L
        currentPositionMsState.value = 0L
        lyricsState.value = emptyList()
    }

    override fun onDestroy() {
        OfflineDownloadManager.cancelAllDownloads()
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
    onClearSession: () -> Unit,
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
    var offlineRefreshTrigger by remember { mutableIntStateOf(0) }

    // 收藏状态 (乐观更新 + 异步持久化)
    var favoriteSongIds by remember { mutableStateOf<Set<Int>>(emptySet()) }
    val favoriteList = remember { mutableStateListOf<FavoriteItem>() }
    var isFavoritesLoading by remember { mutableStateOf(false) }

    // 自建歌单状态
    val playlists = remember { mutableStateListOf<PlaylistSummaryDto>() }
    var isPlaylistsLoading by remember { mutableStateOf(false) }
    var activePlaylistId by remember { mutableStateOf<Int?>(null) }
    var showCreatePlaylistDialog by remember { mutableStateOf(false) }
    var newPlaylistNameInput by remember { mutableStateOf("") }

    // 播放历史状态
    val historyList = remember { mutableStateListOf<HistoryItem>() }
    var isHistoryLoading by remember { mutableStateOf(false) }

    // 目标待添加到歌单的歌曲
    var songToAddToPlaylist by remember { mutableStateOf<MediaFileDto?>(null) }

    fun toggleFavorite(songId: Int) {
        if (tokenManager.token == null) return
        val wasFav = favoriteSongIds.contains(songId)
        favoriteSongIds = if (wasFav) favoriteSongIds - songId else favoriteSongIds + songId
        scope.launch {
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                withContext(Dispatchers.IO) {
                    api.toggleFavorite(songId)
                }
            } catch (e: Exception) {
                // 失败回滚
                favoriteSongIds = if (wasFav) favoriteSongIds + songId else favoriteSongIds - songId
            }
        }
    }

    fun loadFavoriteIds() {
        if (tokenManager.token == null) return
        scope.launch {
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val ids = withContext(Dispatchers.IO) { api.getFavoriteIds() }
                favoriteSongIds = ids.toSet()
            } catch (e: Exception) {
            }
        }
    }

    fun loadFavoritesList() {
        if (tokenManager.token == null) return
        scope.launch {
            isFavoritesLoading = true
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val res = withContext(Dispatchers.IO) { api.getFavorites(page = 1, pageSize = 100) }
                favoriteList.clear()
                favoriteList.addAll(res.items)
                favoriteSongIds = res.items.map { it.mediaFileId }.toSet()
            } catch (e: Exception) {
            } finally {
                isFavoritesLoading = false
            }
        }
    }

    fun loadPlaylists() {
        if (tokenManager.token == null) return
        scope.launch {
            isPlaylistsLoading = true
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val res = withContext(Dispatchers.IO) { api.getPlaylists() }
                playlists.clear()
                playlists.addAll(res)
            } catch (e: Exception) {
            } finally {
                isPlaylistsLoading = false
            }
        }
    }

    fun loadHistoryList() {
        if (tokenManager.token == null) return
        scope.launch {
            isHistoryLoading = true
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val res = withContext(Dispatchers.IO) { api.getHistory(page = 1, pageSize = 100) }
                historyList.clear()
                historyList.addAll(res.items)
            } catch (e: Exception) {
            } finally {
                isHistoryLoading = false
            }
        }
    }

    // 播放满 3 秒自动记录历史
    LaunchedEffect(currentMediaItem?.mediaId, isPlaying) {
        if (isPlaying && currentMediaItem != null && tokenManager.token != null) {
            val songId = currentMediaItem.mediaId.toIntOrNull()
            if (songId != null) {
                delay(3000)
                if (isActive && isPlaying && currentMediaItem.mediaId.toIntOrNull() == songId) {
                    try {
                        val api = WebMusicNetwork.createApi(tokenManager)
                        withContext(Dispatchers.IO) {
                            api.recordHistory(songId)
                        }
                    } catch (e: Exception) {
                    }
                }
            }
        }
    }

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
        if (tokenManager.token != null) {
            loadFavoriteIds()
            if (mediaList.isEmpty()) {
                queryVersion++
                executeQuery(1, null, append = false, queryVersion)
            }
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
        val context = LocalContext.current
        var selectedTab by remember { mutableIntStateOf(0) } // 0: 全部曲库, 1: 我的收藏, 2: 自建歌单, 3: 播放历史, 4: 离线音乐
        var offlineSongs by remember { mutableStateOf<List<DownloadedSong>>(emptyList()) }

        LaunchedEffect(selectedTab, offlineRefreshTrigger, tokenManager.token) {
            if (tokenManager.token != null) {
                when (selectedTab) {
                    0 -> {
                        if (favoriteSongIds.isEmpty()) loadFavoriteIds()
                    }
                    1 -> loadFavoritesList()
                    2 -> loadPlaylists()
                    3 -> loadHistoryList()
                    4 -> {
                        withContext(Dispatchers.IO) {
                            offlineSongs = OfflineDownloadManager.getAllDownloaded(context, tokenManager.serverUrl, tokenManager.username)
                        }
                    }
                }
            }
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            // 五大核心业务栏切换 (支持水平滑动，防止小屏截断)
            ScrollableTabRow(
                selectedTabIndex = selectedTab,
                containerColor = DarkBackground,
                contentColor = GreenPrimary,
                edgePadding = 12.dp,
                divider = { HorizontalDivider(color = Color.DarkGray) }
            ) {
                Tab(
                    selected = selectedTab == 0,
                    onClick = { selectedTab = 0 },
                    text = {
                        Text(
                            text = "全部曲库 (${totalSongs})",
                            fontWeight = if (selectedTab == 0) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 0) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 1,
                    onClick = {
                        selectedTab = 1
                        loadFavoritesList()
                    },
                    text = {
                        Text(
                            text = "我的收藏 (${favoriteSongIds.size})",
                            fontWeight = if (selectedTab == 1) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 1) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 2,
                    onClick = {
                        selectedTab = 2
                        loadPlaylists()
                    },
                    text = {
                        Text(
                            text = "自建歌单 (${playlists.size})",
                            fontWeight = if (selectedTab == 2) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 2) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 3,
                    onClick = {
                        selectedTab = 3
                        loadHistoryList()
                    },
                    text = {
                        Text(
                            text = "播放历史",
                            fontWeight = if (selectedTab == 3) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 3) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 4,
                    onClick = {
                        selectedTab = 4
                        offlineRefreshTrigger++
                    },
                    text = {
                        Text(
                            text = "离线音乐 (${offlineSongs.size})",
                            fontWeight = if (selectedTab == 4) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 4) GreenPrimary else Color.Gray
                        )
                    }
                )
            }

            // 搜索框 (仅在全部曲库与离线音乐展示)
            if (selectedTab == 0 || selectedTab == 4) {
                OutlinedTextField(
                    value = searchQuery,
                    onValueChange = { newQuery ->
                        searchQuery = newQuery
                        if (selectedTab == 0) {
                            isDebouncingSearch = true
                            activeFetchJob?.cancel()
                            val version = ++queryVersion
                            scope.launch {
                                delay(400)
                                if (isActive && version == queryVersion) {
                                    executeQuery(1, newQuery, append = false, version)
                                }
                            }
                        }
                    },
                    placeholder = {
                        Text(
                            if (selectedTab == 0) "搜索全库 $totalSongs 首音乐、歌手、专辑..."
                            else "搜索 ${offlineSongs.size} 首本地离线音乐..."
                        )
                    },
                    leadingIcon = { Icon(Icons.Default.Search, contentDescription = "搜索") },
                    trailingIcon = {
                        if (searchQuery.isNotEmpty()) {
                            IconButton(onClick = {
                                searchQuery = ""
                                if (selectedTab == 0) {
                                    isDebouncingSearch = true
                                    activeFetchJob?.cancel()
                                    val version = ++queryVersion
                                    executeQuery(1, null, append = false, version)
                                }
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
            }

            when (selectedTab) {
                0 -> {
                    // 全部曲库
                    val listState = rememberLazyListState()
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
                                val isFav = favoriteSongIds.contains(song.id)
                                val isDownloaded = remember(song.id, offlineRefreshTrigger, tokenManager.token) {
                                    OfflineDownloadManager.isDownloaded(context, tokenManager.serverUrl, tokenManager.username, song.id)
                                }
                                val downloadProgress = OfflineDownloadManager.getDownloadProgress(tokenManager.serverUrl, tokenManager.username, song.id)

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
                                    trailingContent = {
                                        Row(verticalAlignment = Alignment.CenterVertically) {
                                            IconButton(
                                                onClick = { toggleFavorite(song.id) },
                                                modifier = Modifier.size(36.dp)
                                            ) {
                                                Icon(
                                                    imageVector = if (isFav) Icons.Default.Favorite else Icons.Default.FavoriteBorder,
                                                    contentDescription = if (isFav) "取消收藏" else "收藏",
                                                    tint = if (isFav) Color(0xFFE91E63) else Color.Gray,
                                                    modifier = Modifier.size(20.dp)
                                                )
                                            }
                                            IconButton(
                                                onClick = { songToAddToPlaylist = song },
                                                modifier = Modifier.size(36.dp)
                                            ) {
                                                Icon(
                                                    imageVector = Icons.Default.PlaylistAdd,
                                                    contentDescription = "添加到歌单",
                                                    tint = Color.Gray,
                                                    modifier = Modifier.size(22.dp)
                                                )
                                            }
                                            if (downloadProgress != null) {
                                                CircularProgressIndicator(
                                                    progress = { downloadProgress },
                                                    modifier = Modifier.size(22.dp),
                                                    color = GreenPrimary,
                                                    strokeWidth = 2.dp
                                                )
                                            } else if (isDownloaded) {
                                                Icon(
                                                    imageVector = Icons.Default.DownloadDone,
                                                    contentDescription = "已离线",
                                                    tint = GreenPrimary,
                                                    modifier = Modifier.size(22.dp)
                                                )
                                            } else {
                                                IconButton(
                                                    onClick = {
                                                        scope.launch {
                                                            try {
                                                                OfflineDownloadManager.downloadSong(context, tokenManager, song)
                                                            } catch (e: CancellationException) {
                                                            } finally {
                                                                offlineRefreshTrigger++
                                                            }
                                                        }
                                                    },
                                                    modifier = Modifier.size(36.dp)
                                                ) {
                                                    Icon(
                                                        imageVector = Icons.Default.Download,
                                                        contentDescription = "下载到本地",
                                                        tint = Color.Gray,
                                                        modifier = Modifier.size(22.dp)
                                                    )
                                                }
                                            }
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
                1 -> {
                    val favSongDtos = favoriteList.mapNotNull { it.mediaFile?.toMediaFileDto() }

                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = "共 ${favSongDtos.size} 首收藏歌曲",
                            color = Color.LightGray,
                            fontSize = 14.sp
                        )
                        Button(
                            onClick = {
                                if (favSongDtos.isNotEmpty()) {
                                    onPlaySongFromList(favSongDtos, 0)
                                }
                            },
                            enabled = favSongDtos.isNotEmpty(),
                            colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                            shape = RoundedCornerShape(20.dp),
                            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)
                        ) {
                            Icon(
                                imageVector = Icons.Default.PlayArrow,
                                contentDescription = null,
                                tint = Color.Black,
                                modifier = Modifier.size(18.dp)
                            )
                            Spacer(modifier = Modifier.width(4.dp))
                            Text("播放全部", color = Color.Black, fontWeight = FontWeight.Bold, fontSize = 13.sp)
                        }
                    }

                    if (isFavoritesLoading && favoriteList.isEmpty()) {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            CircularProgressIndicator(color = GreenPrimary)
                        }
                    } else if (favSongDtos.isEmpty()) {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            Text(
                                text = "暂无收藏歌曲\n在曲库或播放器中点击心形即可收藏",
                                color = Color.Gray,
                                textAlign = TextAlign.Center,
                                fontSize = 14.sp
                            )
                        }
                    } else {
                        LazyColumn(
                            modifier = Modifier.fillMaxSize(),
                            contentPadding = PaddingValues(bottom = 16.dp)
                        ) {
                            itemsIndexed(favSongDtos) { index, song ->
                                val isCurrent = currentSongDto?.id == song.id
                                val isDownloaded = remember(song.id, offlineRefreshTrigger, tokenManager.token) {
                                    OfflineDownloadManager.isDownloaded(context, tokenManager.serverUrl, tokenManager.username, song.id)
                                }
                                val downloadProgress = OfflineDownloadManager.getDownloadProgress(tokenManager.serverUrl, tokenManager.username, song.id)

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
                                    trailingContent = {
                                        Row(verticalAlignment = Alignment.CenterVertically) {
                                            IconButton(
                                                onClick = {
                                                    toggleFavorite(song.id)
                                                    favoriteList.removeAll { it.mediaFileId == song.id }
                                                },
                                                modifier = Modifier.size(36.dp)
                                            ) {
                                                Icon(
                                                    imageVector = Icons.Default.Favorite,
                                                    contentDescription = "取消收藏",
                                                    tint = Color(0xFFE91E63),
                                                    modifier = Modifier.size(20.dp)
                                                )
                                            }
                                            IconButton(
                                                onClick = { songToAddToPlaylist = song },
                                                modifier = Modifier.size(36.dp)
                                            ) {
                                                Icon(
                                                    imageVector = Icons.Default.PlaylistAdd,
                                                    contentDescription = "添加到歌单",
                                                    tint = Color.Gray,
                                                    modifier = Modifier.size(22.dp)
                                                )
                                            }
                                            if (downloadProgress != null) {
                                                CircularProgressIndicator(
                                                    progress = { downloadProgress },
                                                    modifier = Modifier.size(22.dp),
                                                    color = GreenPrimary,
                                                    strokeWidth = 2.dp
                                                )
                                            } else if (isDownloaded) {
                                                Icon(
                                                    imageVector = Icons.Default.DownloadDone,
                                                    contentDescription = "已离线",
                                                    tint = GreenPrimary,
                                                    modifier = Modifier.size(22.dp)
                                                )
                                            } else {
                                                IconButton(
                                                    onClick = {
                                                        scope.launch {
                                                            try {
                                                                OfflineDownloadManager.downloadSong(context, tokenManager, song)
                                                            } catch (e: CancellationException) {
                                                            } finally {
                                                                offlineRefreshTrigger++
                                                            }
                                                        }
                                                    },
                                                    modifier = Modifier.size(36.dp)
                                                ) {
                                                    Icon(
                                                        imageVector = Icons.Default.Download,
                                                        contentDescription = "下载到本地",
                                                        tint = Color.Gray,
                                                        modifier = Modifier.size(22.dp)
                                                    )
                                                }
                                            }
                                        }
                                    },
                                    modifier = Modifier
                                        .clickable { onPlaySongFromList(favSongDtos, index) }
                                        .padding(horizontal = 8.dp)
                                )
                            }
                        }
                    }
                }
                2 -> {
                    // 自建歌单
                    if (activePlaylistId != null) {
                        PlaylistDetailView(
                            playlistId = activePlaylistId!!,
                            tokenManager = tokenManager,
                            favoriteSongIds = favoriteSongIds,
                            currentSongId = currentSongDto?.id,
                            isPlaying = isPlaying,
                            onBack = {
                                activePlaylistId = null
                                loadPlaylists()
                            },
                            onPlaySongFromList = onPlaySongFromList,
                            onToggleFavorite = { toggleFavorite(it) }
                        )
                    } else {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 16.dp, vertical = 8.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                text = "我的歌单 (${playlists.size})",
                                color = Color.LightGray,
                                fontSize = 14.sp
                            )
                            Button(
                                onClick = { showCreatePlaylistDialog = true },
                                colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                                shape = RoundedCornerShape(20.dp),
                                contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)
                            ) {
                                Icon(
                                    imageVector = Icons.Default.Add,
                                    contentDescription = null,
                                    tint = Color.Black,
                                    modifier = Modifier.size(18.dp)
                                )
                                Spacer(modifier = Modifier.width(4.dp))
                                Text("新建歌单", color = Color.Black, fontWeight = FontWeight.Bold, fontSize = 13.sp)
                            }
                        }

                        if (isPlaylistsLoading && playlists.isEmpty()) {
                            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                                CircularProgressIndicator(color = GreenPrimary)
                            }
                        } else if (playlists.isEmpty()) {
                            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                                Text(
                                    text = "暂无自建歌单\n点击右上角新建歌单",
                                    color = Color.Gray,
                                    textAlign = TextAlign.Center,
                                    fontSize = 14.sp
                                )
                            }
                        } else {
                            LazyColumn(
                                modifier = Modifier.fillMaxSize(),
                                contentPadding = PaddingValues(bottom = 16.dp)
                            ) {
                                itemsIndexed(playlists) { _, playlist ->
                                    ListItem(
                                        headlineContent = {
                                            Text(
                                                text = playlist.name,
                                                color = Color.White,
                                                fontWeight = FontWeight.SemiBold,
                                                maxLines = 1,
                                                overflow = TextOverflow.Ellipsis
                                            )
                                        },
                                        supportingContent = {
                                            Text(
                                                text = "${playlist.count} 首歌曲",
                                                color = Color.Gray,
                                                fontSize = 13.sp
                                            )
                                        },
                                        leadingContent = {
                                            Box(
                                                modifier = Modifier
                                                    .size(44.dp)
                                                    .clip(RoundedCornerShape(8.dp))
                                                    .background(DarkSurface),
                                                contentAlignment = Alignment.Center
                                            ) {
                                                Icon(
                                                    imageVector = Icons.Default.QueueMusic,
                                                    contentDescription = null,
                                                    tint = GreenPrimary,
                                                    modifier = Modifier.size(24.dp)
                                                )
                                            }
                                        },
                                        trailingContent = {
                                            IconButton(
                                                onClick = {
                                                    scope.launch {
                                                        try {
                                                            val api = WebMusicNetwork.createApi(tokenManager)
                                                            withContext(Dispatchers.IO) {
                                                                api.deletePlaylist(playlist.id)
                                                            }
                                                            loadPlaylists()
                                                        } catch (e: Exception) {
                                                        }
                                                    }
                                                },
                                                modifier = Modifier.size(36.dp)
                                            ) {
                                                Icon(
                                                    imageVector = Icons.Default.DeleteOutline,
                                                    contentDescription = "删除歌单",
                                                    tint = Color(0xFFFF5252),
                                                    modifier = Modifier.size(20.dp)
                                                )
                                            }
                                        },
                                        modifier = Modifier
                                            .clickable { activePlaylistId = playlist.id }
                                            .padding(horizontal = 8.dp)
                                    )
                                }
                            }
                        }
                    }
                }
                3 -> {
                    val historySongDtos = historyList.mapNotNull { it.mediaFile?.toMediaFileDto() }

                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = "最近播放 (${historySongDtos.size})",
                            color = Color.LightGray,
                            fontSize = 14.sp
                        )
                        Button(
                            onClick = {
                                if (historySongDtos.isNotEmpty()) {
                                    onPlaySongFromList(historySongDtos, 0)
                                }
                            },
                            enabled = historySongDtos.isNotEmpty(),
                            colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                            shape = RoundedCornerShape(20.dp),
                            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)
                        ) {
                            Icon(
                                imageVector = Icons.Default.PlayArrow,
                                contentDescription = null,
                                tint = Color.Black,
                                modifier = Modifier.size(18.dp)
                            )
                            Spacer(modifier = Modifier.width(4.dp))
                            Text("播放全部", color = Color.Black, fontWeight = FontWeight.Bold, fontSize = 13.sp)
                        }
                    }

                    if (isHistoryLoading && historyList.isEmpty()) {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            CircularProgressIndicator(color = GreenPrimary)
                        }
                    } else if (historySongDtos.isEmpty()) {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            Text(
                                text = "暂无播放历史\n播放音乐满 3 秒将自动记录",
                                color = Color.Gray,
                                textAlign = TextAlign.Center,
                                fontSize = 14.sp
                            )
                        }
                    } else {
                        LazyColumn(
                            modifier = Modifier.fillMaxSize(),
                            contentPadding = PaddingValues(bottom = 16.dp)
                        ) {
                            itemsIndexed(historySongDtos) { index, song ->
                                val isCurrent = currentSongDto?.id == song.id
                                val isFav = favoriteSongIds.contains(song.id)
                                val isDownloaded = remember(song.id, offlineRefreshTrigger, tokenManager.token) {
                                    OfflineDownloadManager.isDownloaded(context, tokenManager.serverUrl, tokenManager.username, song.id)
                                }
                                val downloadProgress = OfflineDownloadManager.getDownloadProgress(tokenManager.serverUrl, tokenManager.username, song.id)

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
                                    trailingContent = {
                                        Row(verticalAlignment = Alignment.CenterVertically) {
                                            IconButton(
                                                onClick = { toggleFavorite(song.id) },
                                                modifier = Modifier.size(36.dp)
                                            ) {
                                                Icon(
                                                    imageVector = if (isFav) Icons.Default.Favorite else Icons.Default.FavoriteBorder,
                                                    contentDescription = if (isFav) "取消收藏" else "收藏",
                                                    tint = if (isFav) Color(0xFFE91E63) else Color.Gray,
                                                    modifier = Modifier.size(20.dp)
                                                )
                                            }
                                            IconButton(
                                                onClick = { songToAddToPlaylist = song },
                                                modifier = Modifier.size(36.dp)
                                            ) {
                                                Icon(
                                                    imageVector = Icons.Default.PlaylistAdd,
                                                    contentDescription = "添加到歌单",
                                                    tint = Color.Gray,
                                                    modifier = Modifier.size(22.dp)
                                                )
                                            }
                                            if (downloadProgress != null) {
                                                CircularProgressIndicator(
                                                    progress = { downloadProgress },
                                                    modifier = Modifier.size(22.dp),
                                                    color = GreenPrimary,
                                                    strokeWidth = 2.dp
                                                )
                                            } else if (isDownloaded) {
                                                Icon(
                                                    imageVector = Icons.Default.DownloadDone,
                                                    contentDescription = "已离线",
                                                    tint = GreenPrimary,
                                                    modifier = Modifier.size(22.dp)
                                                )
                                            } else {
                                                IconButton(
                                                    onClick = {
                                                        scope.launch {
                                                            try {
                                                                OfflineDownloadManager.downloadSong(context, tokenManager, song)
                                                            } catch (e: CancellationException) {
                                                            } finally {
                                                                offlineRefreshTrigger++
                                                            }
                                                        }
                                                    },
                                                    modifier = Modifier.size(36.dp)
                                                ) {
                                                    Icon(
                                                        imageVector = Icons.Default.Download,
                                                        contentDescription = "下载到本地",
                                                        tint = Color.Gray,
                                                        modifier = Modifier.size(22.dp)
                                                    )
                                                }
                                            }
                                        }
                                    },
                                    modifier = Modifier
                                        .clickable { onPlaySongFromList(historySongDtos, index) }
                                        .padding(horizontal = 8.dp)
                                )
                            }
                        }
                    }
                }
                4 -> {
                    // 离线音乐列表展示
                    val filteredOffline = remember(offlineSongs, searchQuery) {
                        if (searchQuery.isEmpty()) offlineSongs
                        else offlineSongs.filter {
                            it.title.contains(searchQuery, ignoreCase = true) ||
                            it.artist.contains(searchQuery, ignoreCase = true) ||
                            it.album.contains(searchQuery, ignoreCase = true)
                        }
                    }

                    if (filteredOffline.isEmpty()) {
                        Box(
                            modifier = Modifier.fillMaxSize(),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(
                                text = if (searchQuery.isNotEmpty()) "未找到匹配的离线歌曲" else "暂无离线音乐\n在全库中点击下载按钮即可离线收听",
                                color = Color.Gray,
                                textAlign = TextAlign.Center,
                                fontSize = 14.sp
                            )
                        }
                    } else {
                        val offlineDtoList = remember(filteredOffline) {
                            filteredOffline.map { it.toMediaFileDto() }
                        }
                        LazyColumn(
                            modifier = Modifier.fillMaxSize(),
                            contentPadding = PaddingValues(bottom = 16.dp)
                        ) {
                            itemsIndexed(filteredOffline) { index, song ->
                                val isCurrent = currentSongDto?.id == song.id
                                val sizeMb = song.sizeBytes / (1024f * 1024f)

                                ListItem(
                                    headlineContent = {
                                        Text(
                                            text = song.title,
                                            color = if (isCurrent) GreenPrimary else Color.White,
                                            fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.Normal,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis
                                        )
                                    },
                                    supportingContent = {
                                        Text(
                                            text = "${song.artist} • ${song.album} • ${"%.1f".format(sizeMb)} MB",
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
                                    trailingContent = {
                                        IconButton(onClick = {
                                            OfflineDownloadManager.deleteDownload(context, tokenManager.serverUrl, tokenManager.username, song.id)
                                            offlineRefreshTrigger++
                                        }) {
                                            Icon(
                                                imageVector = Icons.Default.DeleteOutline,
                                                contentDescription = "删除离线文件",
                                                tint = Color(0xFFFF5252),
                                                modifier = Modifier.size(22.dp)
                                            )
                                        }
                                    },
                                    modifier = Modifier
                                        .clickable { onPlaySongFromList(offlineDtoList, index) }
                                        .padding(horizontal = 8.dp)
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    // 新建歌单对话框
    if (showCreatePlaylistDialog) {
        AlertDialog(
            onDismissRequest = {
                showCreatePlaylistDialog = false
                newPlaylistNameInput = ""
            },
            title = { Text("新建歌单") },
            text = {
                OutlinedTextField(
                    value = newPlaylistNameInput,
                    onValueChange = { newPlaylistNameInput = it },
                    label = { Text("歌单名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
            },
            confirmButton = {
                Button(
                    onClick = {
                        val name = newPlaylistNameInput.trim()
                        if (name.isNotEmpty()) {
                            scope.launch {
                                try {
                                    val api = WebMusicNetwork.createApi(tokenManager)
                                    withContext(Dispatchers.IO) {
                                        api.createPlaylist(CreatePlaylistRequest(name = name))
                                    }
                                    loadPlaylists()
                                } catch (e: Exception) {
                                }
                                showCreatePlaylistDialog = false
                                newPlaylistNameInput = ""
                            }
                        }
                    },
                    enabled = newPlaylistNameInput.isNotBlank()
                ) {
                    Text("创建")
                }
            },
            dismissButton = {
                TextButton(onClick = {
                    showCreatePlaylistDialog = false
                    newPlaylistNameInput = ""
                }) {
                    Text("取消")
                }
            }
        )
    }

    // 添加到歌单对话框
    if (songToAddToPlaylist != null) {
        AddToPlaylistDialog(
            songId = songToAddToPlaylist!!.id,
            songTitle = songToAddToPlaylist!!.title ?: "未知歌曲",
            tokenManager = tokenManager,
            onDismiss = { songToAddToPlaylist = null },
            onSuccess = { _ ->
                songToAddToPlaylist = null
                loadPlaylists()
            }
        )
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
            isFavorite = currentSongDto.id != 0 && favoriteSongIds.contains(currentSongDto.id),
            onToggleFavorite = { toggleFavorite(currentSongDto.id) },
            onAddToPlaylist = { songToAddToPlaylist = currentSongDto },
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
                onClearSession()
                mediaList.clear()
                currentPage = 1
                totalSongs = 0
                searchQuery = ""
                confirmedQuery = ""
                isDebouncingSearch = false
                offlineRefreshTrigger++
                loadFavoriteIds()
                val v = ++queryVersion
                executeQuery(1, null, append = false, v)
            },
            onLogout = {
                // 1. 取消在途列表请求并递增版本，防止旧响应在退出后回填
                activeFetchJob?.cancel()
                activeFetchJob = null
                queryVersion++

                // 2. 取消所有在途下载任务，阻断旧会话网络与写入
                OfflineDownloadManager.cancelAllDownloads()

                // 3. 清理登录凭据
                tokenManager.clear()

                // 4. 清理播放会话（停止 ExoPlayer、清空 MediaItems、重置播放状态）
                onClearSession()

                // 5. 清空曲库与状态
                mediaList.clear()
                currentPage = 1
                totalSongs = 0
                searchQuery = ""
                confirmedQuery = ""
                isDebouncingSearch = false
                offlineRefreshTrigger++
                favoriteSongIds = emptySet()
                favoriteList.clear()
                playlists.clear()
                historyList.clear()
                activePlaylistId = null
                songToAddToPlaylist = null
            },
            onStopPlaybackForCacheClear = {
                onClearSession()
            }
        )
    }
}

@Composable
fun LoginSettingDialog(
    tokenManager: TokenManager,
    onDismiss: () -> Unit,
    onLoginSuccess: () -> Unit,
    onLogout: () -> Unit,
    onStopPlaybackForCacheClear: () -> Unit
) {
    var serverUrl by remember { mutableStateOf(tokenManager.serverUrl) }
    var username by remember { mutableStateOf(tokenManager.username ?: "") }
    var password by remember { mutableStateOf("") }
    var passwordVisible by remember { mutableStateOf(false) }
    var statusText by remember { mutableStateOf("") }
    var isLoggingIn by remember { mutableStateOf(false) }
    var isClearingCache by remember { mutableStateOf(false) }
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
                        text = "本地缓存快照: ${"%.1f".format(cacheSizeBytes / (1024f * 1024f))} MB",
                        color = Color.Gray,
                        fontSize = 13.sp
                    )
                    TextButton(
                        enabled = !isClearingCache,
                        onClick = {
                            scope.launch {
                                isClearingCache = true
                                onStopPlaybackForCacheClear()
                                withContext(Dispatchers.IO) {
                                    com.maifeipin.webmusic.core.cache.MediaCacheManager.clearAllCache(context)
                                }
                                cacheSizeBytes = withContext(Dispatchers.IO) {
                                    com.maifeipin.webmusic.core.cache.MediaCacheManager.getCacheSizeBytes(context)
                                }
                                statusText = "已停止播放并清空本地缓存！"
                                isClearingCache = false
                            }
                        }
                    ) {
                        if (isClearingCache) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(16.dp),
                                color = Color(0xFFFF5252),
                                strokeWidth = 2.dp
                            )
                        } else {
                            Text("清空缓存", color = Color(0xFFFF5252), fontSize = 13.sp)
                        }
                    }
                }

                if (tokenManager.token != null) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.End
                    ) {
                        TextButton(onClick = {
                            onLogout()
                            statusText = "已退出当前登录并清理播放会话"
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
