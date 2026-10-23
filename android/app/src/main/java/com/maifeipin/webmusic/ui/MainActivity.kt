package com.maifeipin.webmusic.ui

import android.content.ComponentName
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import android.os.Build
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import com.maifeipin.webmusic.core.storage.LocalMediaImporter
import com.maifeipin.webmusic.core.storage.MediaImportFilter
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
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
import com.maifeipin.webmusic.core.player.PlaybackTimeline
import com.maifeipin.webmusic.service.PlaybackService
import com.maifeipin.webmusic.ui.components.AddToPlaylistDialog
import com.maifeipin.webmusic.ui.components.FullPlayerSheet
import com.maifeipin.webmusic.ui.components.LyricsManagerSheet
import com.maifeipin.webmusic.ui.components.MiniPlayerBar
import com.maifeipin.webmusic.ui.components.PlaylistDetailView
import com.maifeipin.webmusic.ui.components.SongDownloadAction
import com.maifeipin.webmusic.ui.components.VideoPlayerDialog
import java.util.concurrent.CancellationException
import com.maifeipin.webmusic.ui.theme.DarkBackground
import com.maifeipin.webmusic.ui.theme.DarkSurface
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import com.maifeipin.webmusic.ui.theme.WebMusicTheme
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import com.maifeipin.webmusic.core.storage.DownloadedSong
import com.maifeipin.webmusic.core.storage.AppSettings
import com.maifeipin.webmusic.core.storage.AutoOfflineCache
import com.maifeipin.webmusic.core.storage.LocalSongDatabase
import com.maifeipin.webmusic.core.storage.OfflineDownloadManager
import com.maifeipin.webmusic.core.storage.friendlyDownloadError
import com.maifeipin.webmusic.core.storage.toMediaFileDto
import java.io.File
import com.maifeipin.webmusic.core.update.AppUpdateManager
import com.maifeipin.webmusic.core.update.UpdateController
import com.maifeipin.webmusic.ui.components.UpdateDialogHost
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {

    private lateinit var tokenManager: TokenManager
    private lateinit var updateController: UpdateController
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var mediaController: MediaController? = null

    // 播放与进度状态 (底层 MediaController 状态驱动，解耦 Activity 生命周期)
    private val isPlayingState = mutableStateOf(false)
    private val currentMediaItemState = mutableStateOf<MediaItem?>(null)
    private val progressState = mutableStateOf(0f)
    private val durationMsState = mutableStateOf(0L)
    private val seekEnabledState = mutableStateOf(false)
    private val currentPositionMsState = mutableStateOf(0L)
    private val playbackModeState = mutableStateOf(PlaybackMode.SEQUENCE)
    private val lyricsState = mutableStateOf<List<LyricLine>>(emptyList())

    // 歌词防竞态异步 Job
    private var lyricsJob: Job? = null

    // 播放队列注入任务（快速连续点击时只保留最后一次）
    private var playQueueJob: Job? = null

    private var onTreeUriSelected: ((Uri) -> Unit)? = null
    private var onMultipleDocumentsSelected: ((List<Uri>) -> Unit)? = null

    private val openDocumentTreeLauncher = registerForActivityResult(
        ActivityResultContracts.OpenDocumentTree()
    ) { uri ->
        if (uri != null) {
            try {
                contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                )
            } catch (_: Exception) {}
            onTreeUriSelected?.invoke(uri)
        }
    }

    private val openMultipleDocumentsLauncher = registerForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments()
    ) { uris ->
        if (!uris.isNullOrEmpty()) {
            for (uri in uris) {
                try {
                    contentResolver.takePersistableUriPermission(
                        uri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                } catch (_: Exception) {}
            }
            onMultipleDocumentsSelected?.invoke(uris)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT)
        )
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            window.attributes.layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        tokenManager = TokenManager(this)
        updateController = UpdateController(this, tokenManager, lifecycleScope) {
            PlaybackService.saveForUpdate()
        }

        initMediaController()

        setContent {
            WebMusicTheme {
                MainAppScreen(
                    tokenManager = tokenManager,
                    updateController = updateController,
                    isPlaying = isPlayingState.value,
                    currentMediaItem = currentMediaItemState.value,
                    progress = progressState.value,
                    durationMs = durationMsState.value,
                    currentPosMs = currentPositionMsState.value,
                    seekEnabled = seekEnabledState.value,
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
                    },
                    onLyricsUpdated = { mediaId, content ->
                        // 歌词在管理面板里被改写后：立刻刷新当前播放器展示，并同步离线库副本
                        if (currentMediaItemState.value?.mediaId?.toIntOrNull() == mediaId) {
                            lyricsState.value = LyricsParser.parse(content)
                        }
                        lifecycleScope.launch(Dispatchers.IO) {
                            runCatching {
                                LocalSongDatabase.getInstance(this@MainActivity).updateLyrics(
                                    LocalSongDatabase.computeServerHash(tokenManager.serverUrl),
                                    LocalSongDatabase.computeUserHash(tokenManager.username),
                                    mediaId,
                                    content
                                )
                            }
                        }
                    },
                    onSelectFolderToImport = { callback ->
                        onTreeUriSelected = callback
                        openDocumentTreeLauncher.launch(null)
                    },
                    onSelectFilesToImport = { callback ->
                        onMultipleDocumentsSelected = callback
                        openMultipleDocumentsLauncher.launch(arrayOf("audio/*", "video/*", "*/*"))
                    }
                )
            }
        }

        startProgressTracker()

        // 启动即静默检查一次更新（失败不打扰用户，登录前也能工作）
        updateController.autoCheckOnce()
    }

    override fun onResume() {
        super.onResume()
        // 从"安装未知应用"授权页返回后自动续接安装
        updateController.onResumed()
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
                    if (isPlaying) PlaybackTimeline.onFirstAudio()
                }

                override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                    currentMediaItemState.value = mediaItem
                    PlaybackTimeline.onTransition(mediaItem?.mediaId)
                    // 切歌瞬间即时清零：新曲在 prepare 完成前拿不到时长，
                    // 若沿用上一曲的时长/位置，进度条与 seek 计算都会基于错误基准。
                    resetProgressState()
                    val songId = mediaItem?.mediaId?.toIntOrNull()
                    if (songId != null) {
                        fetchLyricsSafely(songId)
                        maybeAutoCache(mediaItem, songId)
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
     *
     * 本地优先：播放队列在入队前先批量查本地离线库，命中且文件仍存在时直接用 file:// 播放，
     * 绕开服务端首包偶发的 SMB 冷读超时（实测单次 5~6 秒，叠加播放器退避会放大到十几秒）。
     */
    private fun playQueue(songList: List<MediaFileDto>, startIndex: Int) {
        val controller = mediaController ?: return
        if (songList.isEmpty()) return
        val currentSong = songList.getOrNull(startIndex)
        val audioList = songList.filter { !it.isVideo() }
        if (audioList.isEmpty()) return
        val safeStartIndex = if (currentSong != null) {
            val idx = audioList.indexOfFirst { it.id == currentSong.id }
            if (idx >= 0) idx else startIndex.coerceIn(0, audioList.lastIndex)
        } else {
            startIndex.coerceIn(0, audioList.lastIndex)
        }
        PlaybackTimeline.onClick(audioList.size)
        val serverUrl = tokenManager.serverUrl
        val username = tokenManager.username

        // 快速连续点不同歌单时，只保留最后一次队列注入
        playQueueJob?.cancel()
        playQueueJob = lifecycleScope.launch {
            val localPaths = withContext(Dispatchers.IO) {
                runCatching {
                    LocalSongDatabase.getInstance(this@MainActivity).getLocalPathsWithImports(
                        LocalSongDatabase.computeServerHash(serverUrl),
                        LocalSongDatabase.computeUserHash(username),
                        audioList.map { it.id }
                    )
                }.getOrDefault(emptyMap())
            }
            // 库里有记录但文件已被清理或不可达时回退网络流
            val verifiedLocalPaths = withContext(Dispatchers.IO) {
                if (localPaths.isEmpty()) {
                    emptyMap<Int, String>()
                } else {
                    localPaths.filterValues { path ->
                        if (path.startsWith("content://")) {
                            runCatching {
                                contentResolver.openInputStream(Uri.parse(path))?.use { true } ?: false
                            }.getOrDefault(false)
                        } else {
                            runCatching { File(path).exists() }.getOrDefault(false)
                        }
                    }
                }
            }

            val mediaItems = audioList.map { song ->
                buildQueueMediaItem(serverUrl, username, song, verifiedLocalPaths[song.id])
            }
            PlaybackTimeline.onQueueBuilt()

            // 注入默认模式：保证首次播放默认享受列表循环
            if (controller.repeatMode == Player.REPEAT_MODE_OFF && !controller.shuffleModeEnabled) {
                controller.repeatMode = Player.REPEAT_MODE_ALL
            }

            controller.setMediaItems(mediaItems, safeStartIndex, 0L)
            controller.prepare()
            controller.play()
            PlaybackTimeline.onPrepared()
        }
    }

    private fun buildQueueMediaItem(
        serverUrl: String,
        username: String?,
        song: MediaFileDto,
        localPath: String?
    ): MediaItem {
        val metadata = MediaMetadata.Builder()
            .setTitle(song.title ?: "未知曲目 #${song.id}")
            .setArtist(song.artist ?: "未知艺术家")
            .setAlbumTitle(song.album ?: "未知专辑")
            .build()

        val uri = if (localPath != null) {
            MediaImportFilter.toPlayableUri(localPath)
        } else {
            Uri.parse(WebMusicNetwork.buildStreamUrl(serverUrl, song.id))
        }

        return MediaItem.Builder()
            .setUri(uri)
            .setMediaId(song.id.toString())
            .apply {
                if (localPath == null) {
                    val safeCacheKey = WebMusicNetwork.generateSafeCacheKey(serverUrl, username, song)
                    setCustomCacheKey(safeCacheKey)
                }
            }
            .setMediaMetadata(metadata)
            .build()
    }

    /**
     * 切歌后把当前曲目追加进本地离线库（听过即离线）。
     * 延迟与并发控制都在 [AutoOfflineCache] 内部，此处只负责提供当前曲目快照。
     */
    private fun maybeAutoCache(mediaItem: MediaItem, songId: Int) {
        if (songId <= 0) return // 本机导入曲目无需且不能自动离线
        val uri = mediaItem.localConfiguration?.uri
        if (uri != null && (uri.scheme == "file" || uri.scheme == "content")) return // 已经在播本地文件，无需再缓存

        val metadata = mediaItem.mediaMetadata
        val song = MediaFileDto(
            id = songId,
            filePath = null,
            title = metadata.title?.toString(),
            artist = metadata.artist?.toString(),
            album = metadata.albumTitle?.toString(),
            genre = null,
            year = null,
            duration = null,
            sizeBytes = null,
            fileHash = null,
            contentVersion = null
        )
        AutoOfflineCache.onTrackStarted(this, tokenManager, song) {
            currentMediaItemState.value?.mediaId?.toIntOrNull() == songId
        }
    }


    /**
     * 安全获取歌词：取消旧请求，并严格校验响应 ID 是否匹配当前正在播放的曲目
     */
    private fun fetchLyricsSafely(targetMediaId: Int) {
        lyricsJob?.cancel()
        lyricsState.value = emptyList() // 切歌先清空，防止旧歌词闪烁

        lyricsJob = lifecycleScope.launch {
            if (targetMediaId <= 0) {
                // 本机导入曲目在服务器上不存在，短路网络请求，避免 404 噪音
                lyricsState.value = emptyList()
                return@launch
            }
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
                    // 暂停态也要同步一次：升级后恢复的队列默认暂停，
                    // 若只在 isPlaying 时刷新，界面会一直停在 00:00 / 00:00，
                    // 而播放器实际已定位到恢复的进度（如 01:54）。
                    val dur = controller.duration
                    if (dur > 0L) {
                        val pos = controller.currentPosition.coerceIn(0L, dur)
                        durationMsState.value = dur
                        currentPositionMsState.value = pos
                        progressState.value = (pos.toFloat() / dur.toFloat()).coerceIn(0f, 1f)
                        seekEnabledState.value = controller.isCurrentMediaItemSeekable
                    } else {
                        // 时长未知（新曲未 prepare 完 / 不可 seek 的流）：
                        // 清零并禁用拖动，绝不沿用上一曲的时长与位置。
                        resetProgressState()
                    }
                }
                delay(300)
            }
        }
    }

    /** 进度三态统一清零：切歌、清空会话、时长未知时共用，避免各处漏改导致残留旧值。 */
    private fun resetProgressState() {
        progressState.value = 0f
        durationMsState.value = 0L
        currentPositionMsState.value = 0L
        seekEnabledState.value = false
    }

    private fun clearPlaybackSession() {
        getSharedPreferences("upgrade_playback", MODE_PRIVATE).edit().clear().commit()
        lyricsJob?.cancel()
        mediaController?.let { controller ->
            controller.stop()
            controller.clearMediaItems()
        }
        isPlayingState.value = false
        currentMediaItemState.value = null
        resetProgressState()
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
    updateController: UpdateController,
    isPlaying: Boolean,
    currentMediaItem: MediaItem?,
    progress: Float,
    durationMs: Long,
    currentPosMs: Long,
    seekEnabled: Boolean,
    playbackMode: PlaybackMode,
    lyrics: List<LyricLine>,
    onPlayPauseClick: () -> Unit,
    onPreviousClick: () -> Unit,
    onNextClick: () -> Unit,
    onSeekTo: (Long) -> Unit,
    onToggleMode: () -> Unit,
    onClearSession: () -> Unit,
    onPlaySongFromList: (List<MediaFileDto>, Int) -> Unit,
    onLyricsUpdated: (Int, String) -> Unit,
    onSelectFolderToImport: ((Uri) -> Unit) -> Unit = {},
    onSelectFilesToImport: ((List<Uri>) -> Unit) -> Unit = {}
) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val mediaList = remember { mutableStateListOf<MediaFileDto>() }
    var currentPage by remember { mutableIntStateOf(1) }
    var totalSongs by remember { mutableIntStateOf(0) }
    var isLoading by remember { mutableStateOf(false) }

    // 本机导入状态
    var importJob by remember { mutableStateOf<Job?>(null) }
    var importProgress by remember { mutableStateOf<LocalMediaImporter.ImportProgress?>(null) }
    var isImporting by remember { mutableStateOf(false) }
    var showImportMenu by remember { mutableStateOf(false) }
    var songPendingDelete by remember { mutableStateOf<DownloadedSong?>(null) }

    var searchQuery by remember { mutableStateOf("") }
    var confirmedQuery by remember { mutableStateOf("") }
    var isDebouncingSearch by remember { mutableStateOf(false) }
    var queryVersion by remember { mutableLongStateOf(0L) }

    // MV 视频列表与状态
    val mvList = remember { mutableStateListOf<MediaFileDto>() }
    var currentMvPage by remember { mutableIntStateOf(1) }
    var totalMvs by remember { mutableIntStateOf(0) }
    var isMvLoading by remember { mutableStateOf(false) }
    var mvSearchQuery by remember { mutableStateOf("") }
    var confirmedMvQuery by remember { mutableStateOf("") }
    var isDebouncingMvSearch by remember { mutableStateOf(false) }
    var mvQueryVersion by remember { mutableLongStateOf(0L) }
    var activeMvFetchJob by remember { mutableStateOf<Job?>(null) }

    // 当前全屏播放的视频与本地路径
    var activeVideoSong by remember { mutableStateOf<MediaFileDto?>(null) }
    var activeVideoLocalPath by remember { mutableStateOf<String?>(null) }

    var showFullPlayer by remember { mutableStateOf(false) }
    // 歌词管理面板的目标曲目（网易云匹配 / AI 生成）
    var lyricsManagerSong by remember { mutableStateOf<MediaFileDto?>(null) }
    var showLoginDialog by remember { mutableStateOf(tokenManager.token == null) }

    // 活跃请求 Job，保证单一请求链路
    var activeFetchJob by remember { mutableStateOf<Job?>(null) }
    var offlineRefreshTrigger by remember { mutableIntStateOf(0) }

    // 下载进度快照：0% 的确定进度环在深色背景上等于空白，必须靠状态驱动重组才有反馈
    val downloadStates by OfflineDownloadManager.downloadStates.collectAsState()

    /**
     * 手动下载单曲：显式给出开始 / 成功 / 失败的提示。
     * 服务端读 SMB 偶发超时（5xx），[OfflineDownloadManager.downloadSong] 内部已做退避重试，
     * 走到这里失败就是真的失败了，绝不能再静默吞掉。
     */
    fun startDownload(song: MediaFileDto) {
        val label = song.title ?: "该曲目"
        scope.launch {
            Toast.makeText(context, "开始下载：$label", Toast.LENGTH_SHORT).show()
            try {
                val result = OfflineDownloadManager.downloadSong(context, tokenManager, song)
                result.fold(
                    onSuccess = {
                        Toast.makeText(context, "已离线：$label", Toast.LENGTH_SHORT).show()
                    },
                    onFailure = { e ->
                        Toast.makeText(
                            context,
                            "$label 下载失败：${friendlyDownloadError(e)}",
                            Toast.LENGTH_LONG
                        ).show()
                    }
                )
            } catch (e: CancellationException) {
                // 退出登录 / 切账号导致的取消，不打扰用户
            } finally {
                offlineRefreshTrigger++
            }
        }
    }

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
            if (songId != null && songId > 0) {
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

    fun executeMvQuery(page: Int, search: String?, append: Boolean, targetVersion: Long) {
        if (tokenManager.token == null) return
        activeMvFetchJob?.cancel()
        activeMvFetchJob = scope.launch {
            isMvLoading = true
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val res = withContext(Dispatchers.IO) {
                    api.getMediaFiles(page = page, pageSize = 40, search = search?.ifEmpty { null }, mediaType = "video")
                }
                if (isActive && targetVersion == mvQueryVersion) {
                    totalMvs = res.total
                    if (!append) {
                        mvList.clear()
                    }
                    mvList.addAll(res.files)
                    currentMvPage = page
                    confirmedMvQuery = search ?: ""
                }
            } catch (e: Exception) {
            } finally {
                if (isActive && targetVersion == mvQueryVersion) {
                    isMvLoading = false
                    isDebouncingMvSearch = false
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
            if (mvList.isEmpty()) {
                mvQueryVersion++
                executeMvQuery(1, null, append = false, mvQueryVersion)
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
        var selectedTab by remember { mutableIntStateOf(0) } // 0: 全部曲库, 1: MV视频, 2: 我的收藏, 3: 自建歌单, 4: 播放历史, 5: 离线音乐
        var offlineSongs by remember { mutableStateOf<List<DownloadedSong>>(emptyList()) }

        // 自动缓存有新曲目落盘后，刷新离线列表计数与内容
        val autoCachedIds by AutoOfflineCache.cachedSongIds.collectAsState()
        LaunchedEffect(autoCachedIds.size) {
            if (autoCachedIds.isNotEmpty()) offlineRefreshTrigger++
        }

        LaunchedEffect(selectedTab, offlineRefreshTrigger, tokenManager.token) {
            when (selectedTab) {
                0 -> {
                    if (tokenManager.token != null && favoriteSongIds.isEmpty()) loadFavoriteIds()
                }
                1 -> {
                    if (tokenManager.token != null && mvList.isEmpty()) {
                        mvQueryVersion++
                        executeMvQuery(1, null, append = false, mvQueryVersion)
                    }
                }
                2 -> if (tokenManager.token != null) loadFavoritesList()
                3 -> if (tokenManager.token != null) loadPlaylists()
                4 -> if (tokenManager.token != null) loadHistoryList()
                5 -> {
                    withContext(Dispatchers.IO) {
                        offlineSongs = OfflineDownloadManager.getAllDownloaded(context, tokenManager.serverUrl, tokenManager.username)
                    }
                }
            }
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            // 六大核心业务栏切换 (支持水平滑动，防止小屏截断)
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
                        if (mvList.isEmpty()) {
                            mvQueryVersion++
                            executeMvQuery(1, null, append = false, mvQueryVersion)
                        }
                    },
                    text = {
                        Text(
                            text = "MV视频 (${totalMvs})",
                            fontWeight = if (selectedTab == 1) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 1) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 2,
                    onClick = {
                        selectedTab = 2
                        loadFavoritesList()
                    },
                    text = {
                        Text(
                            text = "我的收藏 (${favoriteSongIds.size})",
                            fontWeight = if (selectedTab == 2) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 2) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 3,
                    onClick = {
                        selectedTab = 3
                        loadPlaylists()
                    },
                    text = {
                        Text(
                            text = "自建歌单 (${playlists.size})",
                            fontWeight = if (selectedTab == 3) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 3) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 4,
                    onClick = {
                        selectedTab = 4
                        loadHistoryList()
                    },
                    text = {
                        Text(
                            text = "播放历史",
                            fontWeight = if (selectedTab == 4) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 4) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = selectedTab == 5,
                    onClick = {
                        selectedTab = 5
                        offlineRefreshTrigger++
                    },
                    text = {
                        Text(
                            text = "离线音乐 (${offlineSongs.size})",
                            fontWeight = if (selectedTab == 5) FontWeight.Bold else FontWeight.Normal,
                            color = if (selectedTab == 5) GreenPrimary else Color.Gray
                        )
                    }
                )
            }

            // 搜索框 (在全部曲库、MV视频与离线音乐展示)
            if (selectedTab == 0 || selectedTab == 1 || selectedTab == 5) {
                val currentQueryText = when (selectedTab) {
                    0 -> searchQuery
                    1 -> mvSearchQuery
                    else -> searchQuery
                }
                OutlinedTextField(
                    value = currentQueryText,
                    onValueChange = { newQuery ->
                        when (selectedTab) {
                            0 -> {
                                searchQuery = newQuery
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
                            1 -> {
                                mvSearchQuery = newQuery
                                isDebouncingMvSearch = true
                                activeMvFetchJob?.cancel()
                                val version = ++mvQueryVersion
                                scope.launch {
                                    delay(400)
                                    if (isActive && version == mvQueryVersion) {
                                        executeMvQuery(1, newQuery, append = false, version)
                                    }
                                }
                            }
                            5 -> {
                                searchQuery = newQuery
                            }
                        }
                    },
                    placeholder = {
                        Text(
                            when (selectedTab) {
                                0 -> "搜索全库 $totalSongs 首音乐、歌手、专辑..."
                                1 -> "搜索 $totalMvs 部 MV 视频、歌手..."
                                else -> "搜索 ${offlineSongs.size} 首本地离线音乐..."
                            }
                        )
                    },
                    leadingIcon = { Icon(Icons.Default.Search, contentDescription = "搜索") },
                    trailingIcon = {
                        if (currentQueryText.isNotEmpty()) {
                            IconButton(onClick = {
                                when (selectedTab) {
                                    0 -> {
                                        searchQuery = ""
                                        isDebouncingSearch = true
                                        activeFetchJob?.cancel()
                                        val version = ++queryVersion
                                        executeQuery(1, null, append = false, version)
                                    }
                                    1 -> {
                                        mvSearchQuery = ""
                                        isDebouncingMvSearch = true
                                        activeMvFetchJob?.cancel()
                                        val version = ++mvQueryVersion
                                        executeMvQuery(1, null, append = false, version)
                                    }
                                    5 -> {
                                        searchQuery = ""
                                    }
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
                                // 任务键算一次即可；downloadStates 是 StateFlow，进度变化能可靠触发重组
                                val downloadTaskKey = remember(song.id, tokenManager.serverUrl, tokenManager.username) {
                                    OfflineDownloadManager.taskKeyFor(tokenManager.serverUrl, tokenManager.username, song.id)
                                }
                                val downloadProgress = downloadStates[downloadTaskKey]

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
                                            SongDownloadAction(
                                                isDownloaded = isDownloaded,
                                                progress = downloadProgress,
                                                onStartDownload = { startDownload(song) }
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
                1 -> {
                    // MV视频列表
                    val mvListState = rememberLazyListState()
                    val shouldLoadMoreMv = remember {
                        derivedStateOf {
                            val lastVisibleIndex = mvListState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
                            lastVisibleIndex >= mvList.size - 5 && !isMvLoading && !isDebouncingMvSearch && mvList.size < totalMvs
                        }
                    }

                    LaunchedEffect(shouldLoadMoreMv.value) {
                        if (shouldLoadMoreMv.value && !isDebouncingMvSearch) {
                            val version = mvQueryVersion
                            executeMvQuery(currentMvPage + 1, confirmedMvQuery, append = true, version)
                        }
                    }

                    if (isMvLoading && mvList.isEmpty()) {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            CircularProgressIndicator(color = GreenPrimary)
                        }
                    } else if (mvList.isEmpty()) {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            Text(
                                text = if (confirmedMvQuery.isNotEmpty()) "未找到匹配的 MV 视频" else "暂无 MV 视频\n服务端扫描到视频文件后将在此展示",
                                color = Color.Gray,
                                textAlign = TextAlign.Center,
                                fontSize = 14.sp
                            )
                        }
                    } else {
                        Box(modifier = Modifier.fillMaxSize()) {
                            LazyColumn(
                                state = mvListState,
                                modifier = Modifier.fillMaxSize(),
                                contentPadding = PaddingValues(bottom = 16.dp)
                            ) {
                                itemsIndexed(mvList) { index, mv ->
                                    val isFav = favoriteSongIds.contains(mv.id)
                                    val isDownloaded = remember(mv.id, offlineRefreshTrigger, tokenManager.token) {
                                        OfflineDownloadManager.isDownloaded(context, tokenManager.serverUrl, tokenManager.username, mv.id)
                                    }
                                    val downloadTaskKey = remember(mv.id, tokenManager.serverUrl, tokenManager.username) {
                                        OfflineDownloadManager.taskKeyFor(tokenManager.serverUrl, tokenManager.username, mv.id)
                                    }
                                    val downloadProgress = downloadStates[downloadTaskKey]

                                    ListItem(
                                        headlineContent = {
                                            Text(
                                                text = mv.title ?: "未知 MV",
                                                color = Color.White,
                                                fontWeight = FontWeight.Medium,
                                                maxLines = 1,
                                                overflow = TextOverflow.Ellipsis
                                            )
                                        },
                                        supportingContent = {
                                            Text(
                                                text = "${mv.artist ?: "未知艺术家"}${if (!mv.duration.isNullOrEmpty()) " • ${mv.duration}" else ""}",
                                                color = Color.Gray,
                                                maxLines = 1,
                                                overflow = TextOverflow.Ellipsis
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
                                                    imageVector = Icons.Default.PlayCircleOutline,
                                                    contentDescription = "播放 MV",
                                                    tint = GreenPrimary,
                                                    modifier = Modifier.size(26.dp)
                                                )
                                            }
                                        },
                                        trailingContent = {
                                            Row(verticalAlignment = Alignment.CenterVertically) {
                                                IconButton(
                                                    onClick = { toggleFavorite(mv.id) },
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
                                                    onClick = { songToAddToPlaylist = mv },
                                                    modifier = Modifier.size(36.dp)
                                                ) {
                                                    Icon(
                                                        imageVector = Icons.Default.PlaylistAdd,
                                                        contentDescription = "添加到歌单",
                                                        tint = Color.Gray,
                                                        modifier = Modifier.size(22.dp)
                                                    )
                                                }
                                                SongDownloadAction(
                                                    isDownloaded = isDownloaded,
                                                    progress = downloadProgress,
                                                    onStartDownload = { startDownload(mv) }
                                                )
                                            }
                                        },
                                        modifier = Modifier
                                            .clickable {
                                                if (isPlaying) {
                                                    onPlayPauseClick()
                                                }
                                                val downloaded = OfflineDownloadManager.getDownloadedSong(context, tokenManager.serverUrl, tokenManager.username, mv.id)
                                                val localPath = if (downloaded != null && File(downloaded.localFilePath).exists()) downloaded.localFilePath else null
                                                activeVideoSong = mv
                                                activeVideoLocalPath = localPath
                                            }
                                            .padding(horizontal = 8.dp)
                                    )
                                }

                                if (isMvLoading) {
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
                2 -> {
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
                                val playable = favSongDtos.filter { !it.isVideo() }
                                if (playable.isNotEmpty()) {
                                    onPlaySongFromList(playable, 0)
                                }
                            },
                            enabled = favSongDtos.any { !it.isVideo() },
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
                                // 任务键算一次即可；downloadStates 是 StateFlow，进度变化能可靠触发重组
                                val downloadTaskKey = remember(song.id, tokenManager.serverUrl, tokenManager.username) {
                                    OfflineDownloadManager.taskKeyFor(tokenManager.serverUrl, tokenManager.username, song.id)
                                }
                                val downloadProgress = downloadStates[downloadTaskKey]

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
                                                imageVector = if (song.isVideo()) Icons.Default.PlayCircleOutline else if (isCurrent && isPlaying) Icons.Default.VolumeUp else Icons.Default.MusicNote,
                                                contentDescription = null,
                                                tint = if (isCurrent) Color.Black else if (song.isVideo()) GreenPrimary else Color.Gray,
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
                                            SongDownloadAction(
                                                isDownloaded = isDownloaded,
                                                progress = downloadProgress,
                                                onStartDownload = { startDownload(song) }
                                            )
                                        }
                                    },
                                    modifier = Modifier
                                        .clickable {
                                            if (song.isVideo()) {
                                                if (isPlaying) {
                                                    onPlayPauseClick()
                                                }
                                                val downloaded = OfflineDownloadManager.getDownloadedSong(context, tokenManager.serverUrl, tokenManager.username, song.id)
                                                val localPath = if (downloaded != null && File(downloaded.localFilePath).exists()) downloaded.localFilePath else null
                                                activeVideoSong = song
                                                activeVideoLocalPath = localPath
                                            } else {
                                                val audioList = favSongDtos.filter { !it.isVideo() }
                                                val audioIndex = audioList.indexOfFirst { it.id == song.id }
                                                if (audioIndex >= 0) {
                                                    onPlaySongFromList(audioList, audioIndex)
                                                }
                                            }
                                        }
                                        .padding(horizontal = 8.dp)
                                )
                            }
                        }
                    }
                }
                3 -> {
                    // 自建歌单
                    if (activePlaylistId != null) {
                        PlaylistDetailView(
                            playlistId = activePlaylistId!!,
                            tokenManager = tokenManager,
                            favoriteSongIds = favoriteSongIds,
                            currentSongId = currentSongDto?.id,
                            isPlaying = isPlaying,
                            offlineRefreshTrigger = offlineRefreshTrigger,
                            onBack = {
                                activePlaylistId = null
                                loadPlaylists()
                            },
                            onPlaySongFromList = { songs, index ->
                                val song = songs.getOrNull(index)
                                if (song != null && song.isVideo()) {
                                    if (isPlaying) {
                                        onPlayPauseClick()
                                    }
                                    val downloaded = OfflineDownloadManager.getDownloadedSong(context, tokenManager.serverUrl, tokenManager.username, song.id)
                                    val localPath = if (downloaded != null && File(downloaded.localFilePath).exists()) downloaded.localFilePath else null
                                    activeVideoSong = song
                                    activeVideoLocalPath = localPath
                                } else {
                                    val audioList = songs.filter { !it.isVideo() }
                                    val audioIndex = audioList.indexOfFirst { it.id == song?.id }
                                    if (audioIndex >= 0) {
                                        onPlaySongFromList(audioList, audioIndex)
                                    }
                                }
                            },
                            onToggleFavorite = { toggleFavorite(it) },
                            onStartDownload = { startDownload(it) }
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
                4 -> {
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
                                val playable = historySongDtos.filter { !it.isVideo() }
                                if (playable.isNotEmpty()) {
                                    onPlaySongFromList(playable, 0)
                                }
                            },
                            enabled = historySongDtos.any { !it.isVideo() },
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
                                // 任务键算一次即可；downloadStates 是 StateFlow，进度变化能可靠触发重组
                                val downloadTaskKey = remember(song.id, tokenManager.serverUrl, tokenManager.username) {
                                    OfflineDownloadManager.taskKeyFor(tokenManager.serverUrl, tokenManager.username, song.id)
                                }
                                val downloadProgress = downloadStates[downloadTaskKey]

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
                                                imageVector = if (song.isVideo()) Icons.Default.PlayCircleOutline else if (isCurrent && isPlaying) Icons.Default.VolumeUp else Icons.Default.MusicNote,
                                                contentDescription = null,
                                                tint = if (isCurrent) Color.Black else if (song.isVideo()) GreenPrimary else Color.Gray,
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
                                            SongDownloadAction(
                                                isDownloaded = isDownloaded,
                                                progress = downloadProgress,
                                                onStartDownload = { startDownload(song) }
                                            )
                                        }
                                    },
                                    modifier = Modifier
                                        .clickable {
                                            if (song.isVideo()) {
                                                if (isPlaying) {
                                                    onPlayPauseClick()
                                                }
                                                val downloaded = OfflineDownloadManager.getDownloadedSong(context, tokenManager.serverUrl, tokenManager.username, song.id)
                                                val localPath = if (downloaded != null && File(downloaded.localFilePath).exists()) downloaded.localFilePath else null
                                                activeVideoSong = song
                                                activeVideoLocalPath = localPath
                                            } else {
                                                val audioList = historySongDtos.filter { !it.isVideo() }
                                                val audioIndex = audioList.indexOfFirst { it.id == song.id }
                                                if (audioIndex >= 0) {
                                                    onPlaySongFromList(audioList, audioIndex)
                                                }
                                            }
                                        }
                                        .padding(horizontal = 8.dp)
                                )
                            }
                        }
                    }
                }
                5 -> {
                    // 离线音乐列表展示
                    val filteredOffline = remember(offlineSongs, searchQuery) {
                        if (searchQuery.isEmpty()) offlineSongs
                        else offlineSongs.filter {
                            it.title.contains(searchQuery, ignoreCase = true) ||
                            it.artist.contains(searchQuery, ignoreCase = true) ||
                            it.album.contains(searchQuery, ignoreCase = true)
                        }
                    }

                    // 离线库操作栏：标题数量 + 导入本机音乐按钮
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = "离线音乐库 (${filteredOffline.size})",
                            color = Color.White,
                            fontSize = 15.sp,
                            fontWeight = FontWeight.Bold
                        )
                        OutlinedButton(
                            onClick = { showImportMenu = true },
                            enabled = !isImporting,
                            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 6.dp),
                            colors = ButtonDefaults.outlinedButtonColors(
                                contentColor = GreenPrimary
                            ),
                            border = androidx.compose.foundation.BorderStroke(1.dp, GreenPrimary.copy(alpha = 0.6f)),
                            shape = RoundedCornerShape(8.dp)
                        ) {
                            Icon(
                                imageVector = Icons.Default.Add,
                                contentDescription = null,
                                modifier = Modifier.size(16.dp)
                            )
                            Spacer(modifier = Modifier.width(4.dp))
                            Text("导入本机音乐", fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                        }
                    }

                    // 导入进度卡片
                    if (isImporting) {
                        Card(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 16.dp, vertical = 4.dp),
                            colors = CardDefaults.cardColors(containerColor = DarkSurface),
                            shape = RoundedCornerShape(8.dp)
                        ) {
                            Column(modifier = Modifier.padding(12.dp)) {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(
                                        text = "正在导入本机媒体...",
                                        color = GreenPrimary,
                                        fontSize = 13.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    TextButton(
                                        onClick = {
                                            importJob?.cancel()
                                        },
                                        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 2.dp)
                                    ) {
                                        Text("取消", color = Color(0xFFFF5252), fontSize = 12.sp)
                                    }
                                }
                                val total = importProgress?.totalFound ?: 0
                                val processed = importProgress?.processed ?: 0
                                if (total > 0) {
                                    LinearProgressIndicator(
                                        progress = { processed.toFloat() / total.toFloat() },
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(vertical = 6.dp),
                                        color = GreenPrimary,
                                        trackColor = Color.DarkGray
                                    )
                                } else {
                                    LinearProgressIndicator(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(vertical = 6.dp),
                                        color = GreenPrimary,
                                        trackColor = Color.DarkGray
                                    )
                                }
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween
                                ) {
                                    Text(
                                        text = importProgress?.currentTitle ?: "正在扫描文件...",
                                        color = Color.LightGray,
                                        fontSize = 11.sp,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                        modifier = Modifier.weight(1f)
                                    )
                                    if (total > 0) {
                                        Spacer(modifier = Modifier.width(8.dp))
                                        Text(
                                            text = "$processed / $total",
                                            color = Color.Gray,
                                            fontSize = 11.sp
                                        )
                                    }
                                }
                            }
                        }
                    }

                    if (filteredOffline.isEmpty()) {
                        Box(
                            modifier = Modifier.fillMaxSize(),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(
                                text = if (searchQuery.isNotEmpty()) "未找到匹配的离线歌曲" else "暂无离线音乐\n可点击上方「导入本机音乐」或在全库中下载",
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
                            itemsIndexed(
                                items = filteredOffline,
                                key = { _, song -> "${song.serverHash}_${song.userHash}_${song.id}" }
                            ) { index, song ->
                                val isCurrent = currentSongDto?.id == song.id
                                val sizeMb = song.sizeBytes / (1024f * 1024f)
                                val isImported = song.source == DownloadedSong.SOURCE_IMPORT

                                ListItem(
                                    headlineContent = {
                                        Row(verticalAlignment = Alignment.CenterVertically) {
                                            Text(
                                                text = song.title,
                                                color = if (isCurrent) GreenPrimary else Color.White,
                                                fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.Normal,
                                                maxLines = 1,
                                                overflow = TextOverflow.Ellipsis,
                                                modifier = Modifier.weight(1f, fill = false)
                                            )
                                            if (isImported) {
                                                Spacer(modifier = Modifier.width(6.dp))
                                                Surface(
                                                    color = GreenPrimary.copy(alpha = 0.15f),
                                                    shape = RoundedCornerShape(4.dp),
                                                    border = androidx.compose.foundation.BorderStroke(0.5.dp, GreenPrimary.copy(alpha = 0.5f))
                                                ) {
                                                    Text(
                                                        text = "本机",
                                                        color = GreenPrimary,
                                                        fontSize = 10.sp,
                                                        fontWeight = FontWeight.Medium,
                                                        modifier = Modifier.padding(horizontal = 4.dp, vertical = 1.dp)
                                                    )
                                                }
                                            }
                                        }
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
                                                imageVector = if (song.isVideo()) Icons.Default.PlayCircleOutline else if (isCurrent && isPlaying) Icons.Default.VolumeUp else Icons.Default.MusicNote,
                                                contentDescription = null,
                                                tint = if (isCurrent) Color.Black else if (song.isVideo()) GreenPrimary else Color.Gray,
                                                modifier = Modifier.size(20.dp)
                                            )
                                        }
                                    },
                                    trailingContent = {
                                        IconButton(onClick = {
                                            songPendingDelete = song
                                        }) {
                                            Icon(
                                                imageVector = Icons.Default.DeleteOutline,
                                                contentDescription = if (isImported) "移除本机音乐" else "删除离线文件",
                                                tint = Color(0xFFFF5252),
                                                modifier = Modifier.size(22.dp)
                                            )
                                        }
                                    },
                                    modifier = Modifier
                                        .clickable {
                                            if (song.isVideo()) {
                                                if (isPlaying) {
                                                    onPlayPauseClick()
                                                }
                                                activeVideoSong = song.toMediaFileDto()
                                                activeVideoLocalPath = song.localFilePath
                                            } else {
                                                val audioList = offlineDtoList.filter { !it.isVideo() }
                                                val audioIndex = audioList.indexOfFirst { it.id == song.id }
                                                if (audioIndex >= 0) {
                                                    onPlaySongFromList(audioList, audioIndex)
                                                }
                                            }
                                        }
                                        .padding(horizontal = 8.dp)
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    // 本机导入方式选择弹窗
    if (showImportMenu) {
        AlertDialog(
            onDismissRequest = { showImportMenu = false },
            title = { Text("导入本机媒体", fontWeight = FontWeight.Bold) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(
                        text = "从本地存储导入音乐或视频到离线音乐库（原地引用，不额外占用磁盘空间）：",
                        fontSize = 13.sp,
                        color = Color.LightGray
                    )
                    Spacer(modifier = Modifier.height(4.dp))
                    OutlinedButton(
                        onClick = {
                            showImportMenu = false
                            onSelectFolderToImport { treeUri ->
                                isImporting = true
                                importProgress = null
                                importJob = scope.launch {
                                    try {
                                        val importer = LocalMediaImporter(context)
                                        val result = importer.importDirectoryTree(treeUri) { p ->
                                            importProgress = p
                                        }
                                        Toast.makeText(
                                            context,
                                            MediaImportFilter.formatImportSummary(
                                                result.added,
                                                result.skippedDuplicate,
                                                result.unsupported,
                                                result.failed
                                            ),
                                            Toast.LENGTH_LONG
                                        ).show()
                                        offlineRefreshTrigger++
                                    } catch (e: Exception) {
                                        if (e is CancellationException) {
                                            Toast.makeText(context, "已取消导入", Toast.LENGTH_SHORT).show()
                                        } else {
                                            Toast.makeText(context, "导入失败: ${e.message}", Toast.LENGTH_LONG).show()
                                        }
                                    } finally {
                                        isImporting = false
                                        importProgress = null
                                        importJob = null
                                    }
                                }
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Icon(Icons.Default.FolderOpen, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(modifier = Modifier.width(8.dp))
                        Text("选择整个文件夹（推荐递归扫描）")
                    }
                    OutlinedButton(
                        onClick = {
                            showImportMenu = false
                            onSelectFilesToImport { uris ->
                                isImporting = true
                                importProgress = null
                                importJob = scope.launch {
                                    try {
                                        val importer = LocalMediaImporter(context)
                                        val result = importer.importDocumentUris(uris) { p ->
                                            importProgress = p
                                        }
                                        Toast.makeText(
                                            context,
                                            MediaImportFilter.formatImportSummary(
                                                result.added,
                                                result.skippedDuplicate,
                                                result.unsupported,
                                                result.failed
                                            ),
                                            Toast.LENGTH_LONG
                                        ).show()
                                        offlineRefreshTrigger++
                                    } catch (e: Exception) {
                                        if (e is CancellationException) {
                                            Toast.makeText(context, "已取消导入", Toast.LENGTH_SHORT).show()
                                        } else {
                                            Toast.makeText(context, "导入失败: ${e.message}", Toast.LENGTH_LONG).show()
                                        }
                                    } finally {
                                        isImporting = false
                                        importProgress = null
                                        importJob = null
                                    }
                                }
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Icon(Icons.Default.FileOpen, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(modifier = Modifier.width(8.dp))
                        Text("多选媒体文件")
                    }
                }
            },
            confirmButton = {},
            dismissButton = {
                TextButton(onClick = { showImportMenu = false }) {
                    Text("取消")
                }
            }
        )
    }

    // 删除/移除离线曲目确认弹窗
    if (songPendingDelete != null) {
        val song = songPendingDelete!!
        val isImported = song.source == DownloadedSong.SOURCE_IMPORT
        AlertDialog(
            onDismissRequest = { songPendingDelete = null },
            title = {
                Text(if (isImported) "移除本机音乐" else "删除离线文件")
            },
            text = {
                Text(
                    if (isImported) {
                        "确定将《${song.title}》从离线音乐库中移除？\n\n提示：此操作仅删除应用内索引，绝不会删除您手机上的原始文件。"
                    } else {
                        "确定删除《${song.title}》的本地离线缓存吗？"
                    }
                )
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        scope.launch(Dispatchers.IO) {
                            OfflineDownloadManager.deleteDownload(
                                context,
                                tokenManager.serverUrl,
                                tokenManager.username,
                                song.id
                            )
                            withContext(Dispatchers.Main) {
                                offlineRefreshTrigger++
                                songPendingDelete = null
                            }
                        }
                    }
                ) {
                    Text(if (isImported) "移除" else "删除", color = Color(0xFFFF5252))
                }
            },
            dismissButton = {
                TextButton(onClick = { songPendingDelete = null }) {
                    Text("取消")
                }
            }
        )
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
            seekEnabled = seekEnabled,
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
            onManageLyrics = { lyricsManagerSong = currentSongDto },
            onDismiss = { showFullPlayer = false }
        )
    }

    // 歌词管理：网易云搜索匹配 / AI 生成（对齐 Web 版 LyricsPanel 能力）
    val lyricsManagerTarget = lyricsManagerSong
    if (lyricsManagerTarget != null) {
        LyricsManagerSheet(
            song = lyricsManagerTarget,
            tokenManager = tokenManager,
            knownDurationMs = durationMs,
            onDismiss = { lyricsManagerSong = null },
            onSaved = { content -> onLyricsUpdated(lyricsManagerTarget.id, content) }
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
                val mvV = ++mvQueryVersion
                executeMvQuery(1, null, append = false, mvV)
            },
            onLogout = {
                // 1. 取消在途列表请求并递增版本，防止旧响应在退出后回填
                activeFetchJob?.cancel()
                activeFetchJob = null
                queryVersion++

                activeMvFetchJob?.cancel()
                activeMvFetchJob = null
                mvQueryVersion++

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

                mvList.clear()
                currentMvPage = 1
                totalMvs = 0
                mvSearchQuery = ""
                confirmedMvQuery = ""
                isDebouncingMvSearch = false
                activeVideoSong = null
                activeVideoLocalPath = null

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
            },
            onCheckUpdate = {
                // 关闭设置弹窗，避免两层 Dialog 叠加
                showLoginDialog = false
                updateController.checkManually()
            }
        )
    }

    // 全屏 MV 视频播放器
    if (activeVideoSong != null) {
        VideoPlayerDialog(
            song = activeVideoSong!!,
            serverUrl = tokenManager.serverUrl,
            token = tokenManager.token,
            localFilePath = activeVideoLocalPath,
            onDismiss = {
                activeVideoSong = null
                activeVideoLocalPath = null
            }
        )
    }

    // 应用内更新流程（检查 / 下载 / 安装 状态机驱动）
    UpdateDialogHost(controller = updateController)
}

@Composable
fun LoginSettingDialog(
    tokenManager: TokenManager,
    onDismiss: () -> Unit,
    onLoginSuccess: () -> Unit,
    onLogout: () -> Unit,
    onStopPlaybackForCacheClear: () -> Unit,
    onCheckUpdate: () -> Unit
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val settings = remember { AppSettings.getInstance(context) }

    var serverUrl by remember { mutableStateOf(tokenManager.serverUrl) }
    var username by remember { mutableStateOf(tokenManager.username ?: "") }
    var password by remember { mutableStateOf("") }
    var passwordVisible by remember { mutableStateOf(false) }
    var statusText by remember { mutableStateOf("") }
    var isLoggingIn by remember { mutableStateOf(false) }
    var isClearingCache by remember { mutableStateOf(false) }

    var isLoggedIn by remember { mutableStateOf(tokenManager.token != null) }

    var autoCacheEnabled by remember { mutableStateOf(settings.autoCacheEnabled) }
    var autoCacheWifiOnly by remember { mutableStateOf(settings.autoCacheWifiOnly) }

    var cacheSizeBytes by remember {
        mutableLongStateOf(com.maifeipin.webmusic.core.cache.MediaCacheManager.getCacheSizeBytes(context))
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 20.dp, vertical = 24.dp)
                .wrapContentHeight(),
            shape = RoundedCornerShape(20.dp),
            color = DarkSurface,
            tonalElevation = 6.dp
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(20.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                // 1. 顶部 Header
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(
                            modifier = Modifier
                                .size(36.dp)
                                .clip(CircleShape)
                                .background(GreenPrimary.copy(alpha = 0.15f)),
                            contentAlignment = Alignment.Center
                        ) {
                            Icon(
                                imageVector = if (isLoggedIn) Icons.Default.AccountCircle else Icons.Default.MusicNote,
                                contentDescription = null,
                                tint = GreenPrimary,
                                modifier = Modifier.size(22.dp)
                            )
                        }
                        Spacer(modifier = Modifier.width(10.dp))
                        Column {
                            Text(
                                text = if (isLoggedIn) "账号与全局设置" else "WebMusic 账号登录",
                                color = Color.White,
                                fontWeight = FontWeight.Bold,
                                fontSize = 17.sp
                            )
                            Text(
                                text = if (isLoggedIn) "管理连接与本地偏好" else "连接到私有流媒体服务",
                                color = Color.Gray,
                                fontSize = 11.sp
                            )
                        }
                    }
                    IconButton(
                        onClick = onDismiss,
                        modifier = Modifier.size(32.dp)
                    ) {
                        Icon(
                            imageVector = Icons.Default.Close,
                            contentDescription = "关闭",
                            tint = Color.Gray
                        )
                    }
                }

                HorizontalDivider(color = Color(0xFF2E2E2E))

                if (isLoggedIn) {
                    // ========================= 已登录视图 =========================
                    // 2.1 账号身份卡片
                    Surface(
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(12.dp),
                        color = DarkBackground,
                        border = BorderStroke(1.dp, Color(0xFF2E2E2E))
                    ) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(14.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier.weight(1f)
                            ) {
                                Box(
                                    modifier = Modifier
                                        .size(40.dp)
                                        .clip(CircleShape)
                                        .background(GreenPrimary.copy(alpha = 0.2f)),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Icon(
                                        imageVector = Icons.Default.Person,
                                        contentDescription = null,
                                        tint = GreenPrimary,
                                        modifier = Modifier.size(24.dp)
                                    )
                                }
                                Spacer(modifier = Modifier.width(10.dp))
                                Column {
                                    Text(
                                        text = tokenManager.username ?: "已登录用户",
                                        color = Color.White,
                                        fontWeight = FontWeight.Bold,
                                        fontSize = 15.sp
                                    )
                                    Spacer(modifier = Modifier.height(2.dp))
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Box(
                                            modifier = Modifier
                                                .size(6.dp)
                                                .clip(CircleShape)
                                                .background(Color(0xFF4CAF50))
                                        )
                                        Spacer(modifier = Modifier.width(4.dp))
                                        Text(
                                            text = tokenManager.serverUrl.removePrefix("https://").removePrefix("http://"),
                                            color = Color.Gray,
                                            fontSize = 11.sp,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis
                                        )
                                    }
                                }
                            }
                            OutlinedButton(
                                onClick = {
                                    onLogout()
                                    isLoggedIn = false
                                    statusText = "已退出当前登录"
                                },
                                shape = RoundedCornerShape(8.dp),
                                colors = ButtonDefaults.outlinedButtonColors(
                                    contentColor = Color(0xFFFF5252)
                                ),
                                border = BorderStroke(1.dp, Color(0xFFFF5252).copy(alpha = 0.5f)),
                                contentPadding = PaddingValues(horizontal = 12.dp, vertical = 6.dp)
                            ) {
                                Text("退出", fontSize = 12.sp)
                            }
                        }
                    }

                    // 2.2 全局离线与缓存设置卡片
                    Surface(
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(12.dp),
                        color = DarkBackground,
                        border = BorderStroke(1.dp, Color(0xFF2E2E2E))
                    ) {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(10.dp)
                        ) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(
                                    imageVector = Icons.Default.CloudDownload,
                                    contentDescription = null,
                                    tint = GreenPrimary,
                                    modifier = Modifier.size(18.dp)
                                )
                                Spacer(modifier = Modifier.width(6.dp))
                                Text(
                                    text = "全局自动离线",
                                    color = Color.White,
                                    fontWeight = FontWeight.Bold,
                                    fontSize = 14.sp
                                )
                            }

                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "播放即自动离线",
                                        color = Color.White,
                                        fontSize = 13.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Text(
                                        text = if (autoCacheEnabled) "播过的音频自动存到本地，下次免流秒播"
                                               else "已关闭：播放不向本地存储写入歌曲",
                                        color = Color.Gray,
                                        fontSize = 11.sp
                                    )
                                }
                                Switch(
                                    checked = autoCacheEnabled,
                                    onCheckedChange = {
                                        autoCacheEnabled = it
                                        settings.autoCacheEnabled = it
                                    },
                                    colors = SwitchDefaults.colors(
                                        checkedThumbColor = Color.Black,
                                        checkedTrackColor = GreenPrimary
                                    )
                                )
                            }

                            if (autoCacheEnabled) {
                                HorizontalDivider(color = Color(0xFF262626))
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Column(modifier = Modifier.weight(1f)) {
                                        Text(
                                            text = "仅在 Wi-Fi 下自动离线",
                                            color = Color.White,
                                            fontSize = 13.sp,
                                            fontWeight = FontWeight.Medium
                                        )
                                        Text(
                                            text = "移动蜂窝网络下不自动下载缓存音频",
                                            color = Color.Gray,
                                            fontSize = 11.sp
                                        )
                                    }
                                    Switch(
                                        checked = autoCacheWifiOnly,
                                        onCheckedChange = {
                                            autoCacheWifiOnly = it
                                            settings.autoCacheWifiOnly = it
                                        },
                                        colors = SwitchDefaults.colors(
                                            checkedThumbColor = Color.Black,
                                            checkedTrackColor = GreenPrimary
                                        )
                                    )
                                }
                            }

                            Text(
                                text = "💡 仅对音频生效；MV 视频绝不自动落盘，避免撑满手机存储空间。",
                                color = Color(0xFF81C784),
                                fontSize = 11.sp
                            )
                        }
                    }

                    // 2.3 存储分片与临时缓存
                    Surface(
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(12.dp),
                        color = DarkBackground,
                        border = BorderStroke(1.dp, Color(0xFF2E2E2E))
                    ) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(14.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier.weight(1f)
                            ) {
                                Icon(
                                    imageVector = Icons.Default.Storage,
                                    contentDescription = null,
                                    tint = Color.Gray,
                                    modifier = Modifier.size(18.dp)
                                )
                                Spacer(modifier = Modifier.width(8.dp))
                                Column {
                                    Text(
                                        text = "播放器临时分片缓存",
                                        color = Color.White,
                                        fontSize = 13.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Text(
                                        text = "临时占用: ${"%.1f".format(cacheSizeBytes / (1024f * 1024f))} MB (LRU自动滚动)",
                                        color = Color.Gray,
                                        fontSize = 11.sp
                                    )
                                }
                            }
                            OutlinedButton(
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
                                        statusText = "已停止播放并清空临时缓存"
                                        isClearingCache = false
                                    }
                                },
                                shape = RoundedCornerShape(8.dp),
                                colors = ButtonDefaults.outlinedButtonColors(contentColor = Color(0xFFFF5252)),
                                border = BorderStroke(1.dp, Color(0xFFFF5252).copy(alpha = 0.5f)),
                                contentPadding = PaddingValues(horizontal = 10.dp, vertical = 6.dp)
                            ) {
                                if (isClearingCache) {
                                    CircularProgressIndicator(
                                        modifier = Modifier.size(14.dp),
                                        color = Color(0xFFFF5252),
                                        strokeWidth = 2.dp
                                    )
                                } else {
                                    Text("清空缓存", fontSize = 12.sp)
                                }
                            }
                        }
                    }

                    // 2.4 版本信息与更新检查
                    Surface(
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(12.dp),
                        color = DarkBackground,
                        border = BorderStroke(1.dp, Color(0xFF2E2E2E))
                    ) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(14.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier.weight(1f)
                            ) {
                                Icon(
                                    imageVector = Icons.Default.Info,
                                    contentDescription = null,
                                    tint = Color.Gray,
                                    modifier = Modifier.size(18.dp)
                                )
                                Spacer(modifier = Modifier.width(8.dp))
                                Column {
                                    Text(
                                        text = "WebMusic for Android",
                                        color = Color.White,
                                        fontSize = 13.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Text(
                                        text = "版本 v${AppUpdateManager.currentVersionName} (${AppUpdateManager.currentVersionCode})",
                                        color = Color.Gray,
                                        fontSize = 11.sp
                                    )
                                }
                            }
                            Button(
                                onClick = onCheckUpdate,
                                shape = RoundedCornerShape(8.dp),
                                colors = ButtonDefaults.buttonColors(
                                    containerColor = GreenPrimary.copy(alpha = 0.15f),
                                    contentColor = GreenPrimary
                                ),
                                contentPadding = PaddingValues(horizontal = 12.dp, vertical = 6.dp)
                            ) {
                                Text("检查更新", fontSize = 12.sp, fontWeight = FontWeight.Bold)
                            }
                        }
                    }

                    if (statusText.isNotEmpty()) {
                        Text(
                            text = statusText,
                            color = GreenPrimary,
                            fontSize = 12.sp,
                            modifier = Modifier.padding(horizontal = 4.dp)
                        )
                    }

                    // 完成关闭按钮
                    Button(
                        onClick = onDismiss,
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(44.dp),
                        shape = RoundedCornerShape(10.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = GreenPrimary,
                            contentColor = Color.Black
                        )
                    ) {
                        Text("完成", fontWeight = FontWeight.Bold, fontSize = 14.sp)
                    }

                } else {
                    // ========================= 未登录视图 =========================
                    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        OutlinedTextField(
                            value = serverUrl,
                            onValueChange = { serverUrl = it },
                            label = { Text("服务器地址") },
                            leadingIcon = {
                                Icon(Icons.Default.CloudQueue, contentDescription = null, tint = GreenPrimary)
                            },
                            singleLine = true,
                            shape = RoundedCornerShape(10.dp),
                            modifier = Modifier.fillMaxWidth()
                        )
                        OutlinedTextField(
                            value = username,
                            onValueChange = { username = it },
                            label = { Text("账号") },
                            leadingIcon = {
                                Icon(Icons.Default.Person, contentDescription = null, tint = GreenPrimary)
                            },
                            singleLine = true,
                            shape = RoundedCornerShape(10.dp),
                            modifier = Modifier.fillMaxWidth()
                        )
                        OutlinedTextField(
                            value = password,
                            onValueChange = { password = it },
                            label = { Text("密码") },
                            leadingIcon = {
                                Icon(Icons.Default.Lock, contentDescription = null, tint = GreenPrimary)
                            },
                            singleLine = true,
                            shape = RoundedCornerShape(10.dp),
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
                            Text(statusText, color = GreenPrimary, fontSize = 12.sp)
                        }

                        Button(
                            onClick = {
                                scope.launch {
                                    isLoggingIn = true
                                    statusText = "正在连接登录..."
                                    try {
                                        tokenManager.serverUrl = serverUrl
                                        val api = WebMusicNetwork.createApi(tokenManager)
                                        val res = withContext(Dispatchers.IO) {
                                            api.login(LoginRequest(username, password))
                                        }
                                        tokenManager.token = res.token
                                        tokenManager.username = username
                                        isLoggedIn = true
                                        statusText = "登录成功！"
                                        delay(400)
                                        onLoginSuccess()
                                    } catch (e: Exception) {
                                        statusText = "登录失败: ${e.message}"
                                    } finally {
                                        isLoggingIn = false
                                    }
                                }
                            },
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(48.dp),
                            shape = RoundedCornerShape(10.dp),
                            colors = ButtonDefaults.buttonColors(
                                containerColor = GreenPrimary,
                                contentColor = Color.Black
                            ),
                            enabled = !isLoggingIn && username.isNotEmpty() && password.isNotEmpty()
                        ) {
                            if (isLoggingIn) {
                                CircularProgressIndicator(
                                    modifier = Modifier.size(20.dp),
                                    color = Color.Black,
                                    strokeWidth = 2.dp
                                )
                            } else {
                                Text("立即登录", fontWeight = FontWeight.Bold, fontSize = 15.sp)
                            }
                        }

                        // 版本与检查更新
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 4.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                text = "v${AppUpdateManager.currentVersionName} (${AppUpdateManager.currentVersionCode})",
                                color = Color.Gray,
                                fontSize = 12.sp
                            )
                            TextButton(onClick = onCheckUpdate) {
                                Text("检查更新", color = GreenPrimary, fontSize = 12.sp)
                            }
                        }
                    }
                }
            }
        }
    }
}
