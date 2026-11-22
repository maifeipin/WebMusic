package com.maifeipin.webmusic.ui.components

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.util.Log
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.zIndex
import androidx.core.content.FileProvider
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.VideoSize
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.extractor.DefaultExtractorsFactory
import androidx.media3.extractor.ts.AdtsExtractor
import androidx.media3.session.MediaSession
import androidx.media3.ui.PlayerView
import kotlinx.coroutines.delay
import com.maifeipin.webmusic.core.cache.MediaCacheManager
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import org.videolan.libvlc.LibVLC
import org.videolan.libvlc.Media
import org.videolan.libvlc.MediaPlayer as VlcMediaPlayer
import org.videolan.libvlc.util.VLCVideoLayout
import java.io.File

/**
 * 沉浸式 MV 视频播放器。
 *
 * 注意：这里**不再用 Compose `Dialog` 承载**。Dialog 是独立窗口，实测（一加 9 / ColorOS 14）
 * 即使 `usePlatformDefaultWidth = false`，窗口依然被系统按 `fitTypes=STATUS_BARS|NAVIGATION_BARS`
 * 收成 2298x930（四边各让出 51~99px），于是 16:9 画面在窗口里再按适应缩放，左右各留 367px 黑边，
 * 全屏看起来和竖屏一样「黑边很大」。
 *
 * 改成 Activity 自身窗口里的一层全屏浮层后，窗口就是真实的 2400x1080（边到边），
 * 画面按视频原始比例居中显示（PlayerView 默认 RESIZE_MODE_FIT），不做裁剪或拉伸。
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
    // 顺着 ContextWrapper 链找宿主 Activity（这一层现在可能被主题包了一层）
    val activity = remember(context) { context.findHostActivity() }
    val composeView = LocalView.current
    val configuration = LocalConfiguration.current

    // 记住进入播放器前的方向，退出全屏时原样恢复，避免覆盖用户自己锁定的屏幕方向
    val originalOrientation = remember(activity) {
        activity?.requestedOrientation ?: ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
    }
    // 原值是 UNSPECIFIED 时，恢复要靠「进入前实际是横还是竖」，而不是那个值本身
    val wasPortrait = remember(activity) {
        activity?.resources?.configuration?.orientation != Configuration.ORIENTATION_LANDSCAPE
    }

    // 全屏状态挂在 Activity 作用域：极端情况下（如系统重建）会丢失组合状态，
    // 挂在 ViewModel 上可以保证「全屏 + 旋转」时不会莫名其妙退回竖屏
    val hostActivity = activity as? ComponentActivity
    val fullscreenVm = remember(hostActivity) {
        hostActivity?.let { ViewModelProvider(it).get(MvFullscreenState::class.java) }
    }
    val fallbackFullscreen = remember { mutableStateOf(false) }
    val localApplied = remember { mutableStateOf(false) }
    var isFullscreen by (fullscreenVm?.isFullscreen ?: fallbackFullscreen)

    fun isApplied(): Boolean = fullscreenVm?.applied ?: localApplied.value
    fun markApplied(applied: Boolean) {
        val vm = fullscreenVm
        if (vm != null) vm.applied = applied else localApplied.value = applied
    }

    LaunchedEffect(song.id) {
        val vm = fullscreenVm
        if (vm != null && vm.songId != song.id) {
            vm.songId = song.id
            vm.applied = false
            vm.isFullscreen.value = false
        }
        Log.d(FULLSCREEN_TAG, "bound song=${song.id} fullscreen=$isFullscreen applied=${isApplied()}")
    }

    var isBuffering by remember { mutableStateOf(true) }
    var isVideoPlaying by remember { mutableStateOf(false) }
    var playbackError by remember { mutableStateOf<PlaybackException?>(null) }
    var useNativeFallback by remember { mutableStateOf(false) }
    var nativeError by remember { mutableStateOf<String?>(null) }
    var retryTrigger by remember { mutableIntStateOf(0) }

    val exoPlayer = remember {
        // 优先硬件解码，不支持的音频/封装格式（如 AC-3/DTS/EAC-3 等）自动回退至 FFmpeg 软解扩展
        val renderersFactory = DefaultRenderersFactory(context.applicationContext).apply {
            setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_ON)
        }
        // 针对 NAS/SMB 大视频流调优缓冲窗口与回溯缓存，加快起播并减少回拖重新拉流
        val loadControl = DefaultLoadControl.Builder()
            .setBufferDurationsMs(
                /* minBufferMs = */ 15_000,
                /* maxBufferMs = */ 60_000,
                /* bufferForPlaybackMs = */ 1_500,
                /* bufferForPlaybackAfterRebufferMs = */ 3_000
            )
            .setBackBuffer(/* backBufferDurationMs = */ 10_000, /* retainBackBufferFromKeyframe = */ true)
            .build()
        val audioAttributes = AudioAttributes.Builder()
            .setUsage(C.USAGE_MEDIA)
            .setContentType(C.AUDIO_CONTENT_TYPE_MOVIE)
            .build()
        ExoPlayer.Builder(context, renderersFactory)
            .setLoadControl(loadControl)
            .setAudioAttributes(audioAttributes, /* handleAudioFocus = */ true)
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
            .apply {
                playWhenReady = true
            }
    }

    LaunchedEffect(song.id, localFilePath, retryTrigger) {
        playbackError = null
        nativeError = null
        isBuffering = true
        // .avi / .flv 等 ExoPlayer 原生不支持的容器直接走内置 LibVLC，避免先报错再切换的等待
        if (isVlcPreferredFormat(localFilePath ?: song.filePath)) {
            exoPlayer.stop()
            useNativeFallback = true
            return@LaunchedEffect
        }
        useNativeFallback = false
        val metadata = MediaMetadata.Builder()
            .setTitle(song.title ?: "未知 MV")
            .setArtist(song.artist ?: "未知艺术家")
            .setAlbumTitle(song.album ?: "")
            .build()
        if (!localFilePath.isNullOrEmpty()) {
            val uri = com.maifeipin.webmusic.core.storage.MediaImportFilter.toPlayableUri(localFilePath)
            val mediaItem = MediaItem.Builder()
                .setUri(uri)
                .setMediaId("video_${song.id}")
                .setMediaMetadata(metadata)
                .build()
            exoPlayer.setMediaItem(mediaItem)
        } else {
            val streamUrl = "${serverUrl.trimEnd('/')}/api/media/stream/${song.id}"
            val okHttpClient = WebMusicNetwork.createClientWithToken(token ?: "", readTimeoutSeconds = 60)
                .newBuilder()
                .retryOnConnectionFailure(true)
                .build()
            val cacheDataSourceFactory = MediaCacheManager.createCacheDataSourceFactory(
                context.applicationContext,
                okHttpClient
            )
            // 启用恒定码率 Seek 兜底，提升 TS / ADTS 等容器的解复用与拖动兼容性
            val extractorsFactory = DefaultExtractorsFactory()
                .setConstantBitrateSeekingEnabled(true)
                .setAdtsExtractorFlags(AdtsExtractor.FLAG_ENABLE_CONSTANT_BITRATE_SEEKING)
            val mediaSourceFactory = DefaultMediaSourceFactory(context.applicationContext, extractorsFactory)
                .setDataSourceFactory(cacheDataSourceFactory)
            val cacheKey = "video_${serverUrl.trimEnd('/')}_${song.id}_${song.contentVersion ?: song.fileHash ?: ""}"
            val mediaItem = MediaItem.Builder()
                .setUri(Uri.parse(streamUrl))
                .setMediaId("video_${song.id}")
                .setCustomCacheKey(cacheKey)
                .setMediaMetadata(metadata)
                .build()
            val mediaSource = mediaSourceFactory.createMediaSource(mediaItem)
            exoPlayer.setMediaSource(mediaSource)
        }
        exoPlayer.prepare()
    }

    // 对接系统 MediaSession，使锁屏界面与系统媒体控制面板能显示播放/暂停/进度控制
    DisposableEffect(exoPlayer) {
        val mediaSession = MediaSession.Builder(context.applicationContext, exoPlayer)
            .setId("webmusic_video_session")
            .build()
        onDispose {
            mediaSession.release()
        }
    }

    // 记录一次真实视频尺寸，并监听缓冲/播放/错误状态，避免不支持的视频静默黑屏或卡死
    DisposableEffect(exoPlayer) {
        val listener = object : Player.Listener {
            override fun onVideoSizeChanged(videoSize: VideoSize) {
                Log.d(
                    FULLSCREEN_TAG,
                    "video size=${videoSize.width}x${videoSize.height} par=${videoSize.pixelWidthHeightRatio}"
                )
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                isVideoPlaying = isPlaying
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                isBuffering = playbackState == Player.STATE_BUFFERING
                if (playbackState == Player.STATE_READY) {
                    playbackError = null
                }
            }

            override fun onPlayerError(error: PlaybackException) {
                Log.e(FULLSCREEN_TAG, "video playback error: ${error.errorCodeName}", error)
                isBuffering = false
                isVideoPlaying = false
                if (!useNativeFallback && shouldFallbackToVlc(error)) {
                    Log.w(FULLSCREEN_TAG, "Switching to LibVLC fallback for song=${song.id}")
                    exoPlayer.stop()
                    playbackError = null
                    nativeError = null
                    isBuffering = true
                    useNativeFallback = true
                } else {
                    playbackError = error
                }
            }
        }
        exoPlayer.addListener(listener)
        onDispose { exoPlayer.removeListener(listener) }
    }

    // 无论横屏全屏还是竖屏播放，只要正在播放且无报错就保持屏幕常亮
    DisposableEffect(isVideoPlaying, playbackError, nativeError, activity) {
        if (isVideoPlaying && playbackError == null && nativeError == null) {
            activity?.window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            activity?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
        onDispose { }
    }

    DisposableEffect(exoPlayer) {
        onDispose {
            exoPlayer.stop()
            exoPlayer.release()
        }
    }

    // 全屏 = 横向 + 隐藏系统栏 + 屏幕常亮。
    // 现在播放器就在 Activity 窗口里，方向与系统栏都直接作用于这个窗口。
    DisposableEffect(isFullscreen, configuration.orientation, configuration.screenWidthDp) {
        var alive = true
        val applySystemUi = {
            if (alive) {
                if (isFullscreen) {
                    markApplied(true)
                    activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    ViewCompat.getWindowInsetsController(composeView)?.apply {
                        // 用户从边缘滑动可以临时唤出系统栏，转瞬后自动再隐藏
                        systemBarsBehavior =
                            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                        hide(WindowInsetsCompat.Type.systemBars())
                    }
                } else if (isApplied()) {
                    // 只有真正应用过全屏才回退；首次组合不该动用户既有的屏幕方向
                    markApplied(false)
                    restoreOrientation(activity, originalOrientation, wasPortrait)
                    ViewCompat.getWindowInsetsController(composeView)
                        ?.show(WindowInsetsCompat.Type.systemBars())
                }
                Log.d(
                    FULLSCREEN_TAG,
                    "apply fullscreen=$isFullscreen applied=${isApplied()} " +
                        "orientation=${configuration.orientation}"
                )
            }
        }
        // 首次组合时 view 可能还没 attach，insets controller 会是 null，等一帧再试
        if (composeView.isAttachedToWindow) {
            applySystemUi()
        } else {
            composeView.post { applySystemUi() }
        }
        onDispose { alive = false }
    }

    // 兜底：播放器真的被销毁时清掉系统栏与常亮
    DisposableEffect(Unit) {
        onDispose {
            if (!isFullscreen && isApplied()) {
                markApplied(false)
                restoreOrientation(activity, originalOrientation, wasPortrait)
            }
            ViewCompat.getWindowInsetsController(composeView)
                ?.show(WindowInsetsCompat.Type.systemBars())
            activity?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            Log.d(FULLSCREEN_TAG, "dispose song=${song.id} fullscreen=$isFullscreen applied=${isApplied()}")
        }
    }

    // 返回键：全屏时先退出全屏，再按才关闭播放器（原来由 Dialog 的 onDismissRequest 兜住，
    // 现在播放器是 Activity 里的浮层，得自己接管返回）
    BackHandler(enabled = true) {
        if (isFullscreen) isFullscreen = false else onDismiss()
    }

    // 画面一律按视频原始比例显示（media3 PlayerView 默认的 RESIZE_MODE_FIT），
    // 不做裁剪或拉伸：20:9 屏幕看 16:9 视频左右留下的黑边属于正常比例留白，不额外处理。
    Box(
        modifier = Modifier
            .fillMaxSize()
            .zIndex(1f)
            .background(Color.Black)
    ) {
        // 视频渲染器视图：.avi/.flv 等直走 LibVLC；其余优先 ExoPlayer，遇到编码/容器异常自动切 LibVLC
        if (useNativeFallback) {
            VlcVideoPlayerView(
                song = song,
                serverUrl = serverUrl,
                token = token,
                localFilePath = localFilePath,
                retryTrigger = retryTrigger,
                onBufferingChanged = { isBuffering = it },
                onPlayingChanged = { isVideoPlaying = it },
                onError = { msg ->
                    isBuffering = false
                    isVideoPlaying = false
                    nativeError = msg
                },
                modifier = Modifier.fillMaxSize()
            )
        } else {
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
        }

        val activeErrorMessage = nativeError ?: playbackError?.let { formatPlaybackErrorMessage(it) }
        if (isBuffering && activeErrorMessage == null) {
            CircularProgressIndicator(
                color = GreenPrimary,
                modifier = Modifier.align(Alignment.Center)
            )
        }

        if (activeErrorMessage != null) {
            Column(
                modifier = Modifier
                    .align(Alignment.Center)
                    .padding(horizontal = 32.dp)
                    .background(Color.Black.copy(alpha = 0.85f), RoundedCornerShape(16.dp))
                    .padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Icon(
                    imageVector = Icons.Default.ErrorOutline,
                    contentDescription = null,
                    tint = Color(0xFFFF6B6B),
                    modifier = Modifier.size(44.dp)
                )
                Text(
                    text = "视频播放失败",
                    color = Color.White,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.Bold
                )
                Text(
                    text = activeErrorMessage,
                    color = Color.LightGray,
                    fontSize = 13.sp,
                    textAlign = TextAlign.Center
                )
                playbackError?.let { error ->
                    Text(
                        text = "错误代码：${error.errorCodeName}",
                        color = Color.Gray,
                        fontSize = 11.sp,
                        textAlign = TextAlign.Center
                    )
                    val causeDetail = remember(error) { formatPlaybackErrorCause(error) }
                    if (causeDetail.isNotEmpty()) {
                        Text(
                            text = causeDetail,
                            color = Color.Gray,
                            fontSize = 11.sp,
                            maxLines = 3,
                            overflow = TextOverflow.Ellipsis,
                            textAlign = TextAlign.Center
                        )
                    }
                }
                Row(
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    modifier = Modifier.padding(top = 4.dp)
                ) {
                    OutlinedButton(
                        onClick = {
                            playbackError = null
                            nativeError = null
                            isBuffering = true
                            retryTrigger++
                        },
                        colors = ButtonDefaults.outlinedButtonColors(contentColor = Color.White)
                    ) {
                        Icon(
                            imageVector = Icons.Default.Refresh,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                        )
                        Spacer(modifier = Modifier.width(6.dp))
                        Text("重试")
                    }
                    Button(
                        onClick = {
                            exoPlayer.pause()
                            openInExternalPlayer(
                                context = context,
                                song = song,
                                serverUrl = serverUrl,
                                token = token,
                                localFilePath = localFilePath
                            )
                        },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = GreenPrimary,
                            contentColor = Color.Black
                        )
                    ) {
                        Icon(
                            imageVector = Icons.Default.OpenInNew,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                        )
                        Spacer(modifier = Modifier.width(6.dp))
                        Text("外部播放器打开")
                    }
                }
            }
        }

        if (!isFullscreen) {
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

                if (useNativeFallback) {
                    Surface(
                        color = Color.White.copy(alpha = 0.16f),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.padding(end = 4.dp)
                    ) {
                        Text(
                            text = "VLC 内核",
                            color = Color.White,
                            fontSize = 11.sp,
                            fontWeight = FontWeight.Medium,
                            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp)
                        )
                    }
                }

                if (!localFilePath.isNullOrEmpty() && File(localFilePath).exists()) {
                    Surface(
                        color = GreenPrimary.copy(alpha = 0.2f),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.padding(end = 4.dp)
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

                IconButton(
                    onClick = {
                        exoPlayer.pause()
                        openInExternalPlayer(
                            context = context,
                            song = song,
                            serverUrl = serverUrl,
                            token = token,
                            localFilePath = localFilePath
                        )
                    }
                ) {
                    Icon(
                        imageVector = Icons.Default.OpenInNew,
                        contentDescription = "外部播放器打开",
                        tint = Color.White
                    )
                }

                IconButton(onClick = { isFullscreen = true }) {
                    Icon(
                        imageVector = Icons.Default.Fullscreen,
                        contentDescription = "全屏",
                        tint = Color.White
                    )
                }
            }
        } else {
            // 全屏时收起标题栏，只留一个常驻的退出入口：
            // media3 控制器 3.5 秒后会自动隐藏，只靠它找出口很不顺手
            IconButton(
                onClick = { isFullscreen = false },
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .displayCutoutPadding()
                    .padding(8.dp)
                    .background(Color.Black.copy(alpha = 0.45f), CircleShape)
            ) {
                Icon(
                    imageVector = Icons.Default.FullscreenExit,
                    contentDescription = "退出全屏",
                    tint = Color.White
                )
            }
        }
    }
}

private const val FULLSCREEN_TAG = "MvFullscreen"

/**
 * MV 播放器的全屏状态，挂在 Activity 作用域上。
 *
 * 播放器已经是 Activity 里的浮层，正常旋转不会丢状态；但 Activity 重建、进程恢复之类的情形
 * 仍可能让 `remember` 归零，那会让「刚进全屏就自己退出来」再次出现。ViewModel 跟随 Activity，
 * 换曲时才重置。
 */
class MvFullscreenState : ViewModel() {
    val isFullscreen = mutableStateOf(false)

    /** 是否已经把全屏效果应用到系统，用来区分「退出全屏」和「首次组合」 */
    var applied: Boolean = false

    /** 上一次绑定的曲目，换曲时才重置全屏 */
    var songId: Int = Int.MIN_VALUE
}

/** 顺着 ContextWrapper 链向上找到宿主 Activity（Compose 的 context 只是层包装）。 */
private fun Context.findHostActivity(): Activity? {
    var current: Context? = this
    while (current is ContextWrapper) {
        if (current is Activity) return current
        current = current.baseContext
    }
    return null
}

/**
 * 恢复进入播放器前的屏幕方向。
 *
 * 若原值是一个明确方向，直接写回即可。麻烦的是 `SCREEN_ORIENTATION_UNSPECIFIED`：
 * 实测一加 / ColorOS 14（Android 14）上，把请求值从强制横屏改回 UNSPECIFIED **不会**
 * 触发方向重新评估 —— dumpsys 里 `requestedOrientation` 已经是 UNSPECIFIED，
 * 屏幕却一直停在横屏。换成 `SENSOR` 也一样，因为传感器静止时系统不会给出新建议。
 *
 * 所以这里先写一个**明确**方向，让系统立刻转回 [wasPortrait] 对应的方向；
 * 等布局稳定后再恢复 UNSPECIFIED，把自动旋转的选择权交还用户
 * （此时方向已经对了，UNSPECIFIED 不会再把它掰回去）。
 */
private fun restoreOrientation(activity: Activity?, original: Int, wasPortrait: Boolean) {
    activity ?: return
    if (original != ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED) {
        activity.requestedOrientation = original
        return
    }
    val forced = if (wasPortrait) {
        ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
    } else {
        ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
    }
    activity.requestedOrientation = forced
    activity.window?.decorView?.postDelayed({
        // 期间用户可能又点了全屏，只有值仍是我们设的那个才交还控制权
        if (activity.requestedOrientation == forced) {
            activity.requestedOrientation = original
        }
    }, ORIENTATION_SETTLE_MS)
}

private const val ORIENTATION_SETTLE_MS = 400L

/** 将 Media3 PlaybackException 映射为简洁可读的中文错误提示。 */
private fun formatPlaybackErrorMessage(error: PlaybackException): String {
    return when (error.errorCode) {
        PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
        PlaybackException.ERROR_CODE_DECODER_QUERY_FAILED,
        PlaybackException.ERROR_CODE_DECODING_FAILED,
        PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
        PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
        PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED,
        PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED ->
            "当前视频封装或编码格式不受内置播放器支持，可尝试使用外部播放器（如 VLC / MX Player）打开。"

        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT,
        PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS ->
            "视频流请求失败或网络超时，请检查网络连接后重试。"

        PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND ->
            "视频文件不存在或暂无访问权限。"

        else -> error.localizedMessage ?: "播放遇到未知异常，可尝试重试或使用外部播放器打开。"
    }
}

/** 提取底层异常原因（如 SocketTimeoutException / HttpDataSourceException），方便无 ADB 时直接排查。 */
private fun formatPlaybackErrorCause(error: PlaybackException): String {
    val root = generateSequence<Throwable>(error) { it.cause }.lastOrNull()
    if (root == null || root === error) return ""
    val name = root.javaClass.simpleName
    val msg = root.message?.takeIf { it.isNotBlank() }
    return if (msg != null) "$name: $msg" else name
}

/** 根据文件路径后缀推断视频 MIME 类型，用于拉起外部播放器。 */
private fun resolveVideoMimeType(filePath: String?): String {
    val ext = filePath?.substringAfterLast('.', "")?.lowercase() ?: ""
    return when (ext) {
        "mp4", "m4v" -> "video/mp4"
        "mkv" -> "video/x-matroska"
        "webm" -> "video/webm"
        "mov" -> "video/quicktime"
        "avi" -> "video/x-msvideo"
        "flv" -> "video/x-flv"
        "ts" -> "video/mp2t"
        else -> "video/*"
    }
}

/** 构造本地 FileProvider URI 或带鉴权参数的远端流地址，交由系统/第三方播放器打开。 */
private fun openInExternalPlayer(
    context: Context,
    song: MediaFileDto,
    serverUrl: String,
    token: String?,
    localFilePath: String?
) {
    val mimeType = resolveVideoMimeType(localFilePath ?: song.filePath)
    val uri = if (!localFilePath.isNullOrEmpty()) {
        val trimmed = localFilePath.trim()
        if (trimmed.startsWith("content://")) {
            Uri.parse(trimmed)
        } else {
            val file = File(trimmed.removePrefix("file://"))
            try {
                FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
            } catch (ex: IllegalArgumentException) {
                Log.w(FULLSCREEN_TAG, "FileProvider failed for $trimmed, fallback to stream", ex)
                null
            }
        }
    } else {
        null
    } ?: run {
        val base = "${serverUrl.trimEnd('/')}/api/media/stream/${song.id}"
        val url = if (!token.isNullOrBlank()) "$base?access_token=${Uri.encode(token)}" else base
        Uri.parse(url)
    }

    val intent = Intent(Intent.ACTION_VIEW).apply {
        setDataAndType(uri, mimeType)
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    }
    try {
        context.startActivity(Intent.createChooser(intent, "选择视频播放器"))
    } catch (_: ActivityNotFoundException) {
        Toast.makeText(context, "未找到可用的外部视频播放器", Toast.LENGTH_SHORT).show()
    } catch (ex: Exception) {
        Log.e(FULLSCREEN_TAG, "Failed to launch external player", ex)
        Toast.makeText(context, "无法打开外部播放器：${ex.message ?: "未知错误"}", Toast.LENGTH_SHORT).show()
    }
}

/** 判断文件扩展名是否属于 ExoPlayer 原生不支持、应直接交由 LibVLC 播放的容器格式。 */
private fun isVlcPreferredFormat(filePath: String?): Boolean {
    val ext = filePath?.substringAfterLast('.', "")?.lowercase() ?: ""
    return ext in setOf("avi", "flv", "wmv", "rmvb", "rm", "vob", "mpg", "mpeg", "divx", "asf")
}

/** 判断 ExoPlayer 异常是否属于解码/封装/渲染兼容性问题，适合自动切换至 LibVLC 兜底。 */
private fun shouldFallbackToVlc(error: PlaybackException): Boolean {
    return when (error.errorCode) {
        PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
        PlaybackException.ERROR_CODE_DECODER_QUERY_FAILED,
        PlaybackException.ERROR_CODE_DECODING_FAILED,
        PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
        PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
        PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED,
        PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED,
        PlaybackException.ERROR_CODE_UNSPECIFIED -> true
        else -> false
    }
}

/**
 * 基于 LibVLC 的全能视频兜底播放器：
 * 内置完整 FFmpeg 解复用与软/硬解能力，通吃 .avi / .mkv / .ts / .flv 等冷门容器与编码。
 */
@Composable
private fun VlcVideoPlayerView(
    song: MediaFileDto,
    serverUrl: String,
    token: String?,
    localFilePath: String?,
    retryTrigger: Int,
    onBufferingChanged: (Boolean) -> Unit,
    onPlayingChanged: (Boolean) -> Unit,
    onError: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    var isPlaying by remember { mutableStateOf(false) }
    var showControls by remember { mutableStateOf(true) }
    var durationMs by remember { mutableLongStateOf(0L) }
    var positionMs by remember { mutableLongStateOf(0L) }
    var isSeeking by remember { mutableStateOf(false) }

    val libVlc = remember {
        LibVLC(
            context.applicationContext,
            arrayListOf(
                "--no-drop-late-frames",
                "--no-skip-frames",
                "--network-caching=1500"
            )
        )
    }
    val vlcPlayer = remember(libVlc) { VlcMediaPlayer(libVlc) }

    LaunchedEffect(showControls, isPlaying) {
        if (showControls && isPlaying) {
            delay(3500)
            showControls = false
        }
    }

    // 绑定事件监听并加载媒体（本地文件 / content:// / 远端鉴权流）
    DisposableEffect(song.id, localFilePath, retryTrigger, vlcPlayer) {
        var pfd: ParcelFileDescriptor? = null
        onBufferingChanged(true)
        durationMs = 0L
        positionMs = 0L
        isPlaying = false

        vlcPlayer.setEventListener { event ->
            when (event.type) {
                VlcMediaPlayer.Event.Opening -> onBufferingChanged(true)
                VlcMediaPlayer.Event.Buffering -> {
                    onBufferingChanged(event.buffering < 100f)
                }
                VlcMediaPlayer.Event.Playing -> {
                    isPlaying = true
                    onPlayingChanged(true)
                    onBufferingChanged(false)
                    if (vlcPlayer.length > 0) {
                        durationMs = vlcPlayer.length
                    }
                }
                VlcMediaPlayer.Event.Paused,
                VlcMediaPlayer.Event.Stopped -> {
                    isPlaying = false
                    onPlayingChanged(false)
                }
                VlcMediaPlayer.Event.EndReached -> {
                    isPlaying = false
                    onPlayingChanged(false)
                    showControls = true
                }
                VlcMediaPlayer.Event.TimeChanged -> {
                    if (!isSeeking) {
                        positionMs = event.timeChanged.coerceAtLeast(0L)
                    }
                }
                VlcMediaPlayer.Event.LengthChanged -> {
                    durationMs = event.lengthChanged.coerceAtLeast(0L)
                }
                VlcMediaPlayer.Event.EncounteredError -> {
                    Log.e(FULLSCREEN_TAG, "LibVLC playback encountered error for song=${song.id}")
                    isPlaying = false
                    onPlayingChanged(false)
                    onBufferingChanged(false)
                    onError("VLC 内核播放失败，请检查网络或尝试外部播放器打开。")
                }
            }
        }

        try {
            val media = if (!localFilePath.isNullOrEmpty()) {
                val trimmed = localFilePath.trim()
                if (trimmed.startsWith("content://")) {
                    pfd = context.contentResolver.openFileDescriptor(Uri.parse(trimmed), "r")
                    val fd = pfd?.fileDescriptor
                        ?: throw IllegalStateException("无法打开本地视频描述符")
                    Media(libVlc, fd)
                } else {
                    Media(libVlc, trimmed.removePrefix("file://"))
                }
            } else {
                val base = "${serverUrl.trimEnd('/')}/api/media/stream/${song.id}"
                val url = if (!token.isNullOrBlank()) "$base?access_token=${Uri.encode(token)}" else base
                Media(libVlc, Uri.parse(url))
            }
            media.setHWDecoderEnabled(true, false)
            vlcPlayer.media = media
            media.release()
            vlcPlayer.play()
        } catch (ex: Exception) {
            Log.e(FULLSCREEN_TAG, "LibVLC media init failed", ex)
            onBufferingChanged(false)
            onError("VLC 内核初始化失败：${ex.message ?: "未知错误"}")
        }

        onDispose {
            runCatching { vlcPlayer.setEventListener(null) }
            runCatching { vlcPlayer.stop() }
            runCatching { pfd?.close() }
        }
    }

    DisposableEffect(vlcPlayer, libVlc) {
        onDispose {
            runCatching { vlcPlayer.detachViews() }
            runCatching { vlcPlayer.release() }
            runCatching { libVlc.release() }
        }
    }

    Box(
        modifier = modifier
            .background(Color.Black)
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null
            ) { showControls = !showControls },
        contentAlignment = Alignment.Center
    ) {
        AndroidView(
            factory = { ctx ->
                VLCVideoLayout(ctx).also { layout ->
                    vlcPlayer.attachViews(layout, null, false, false)
                }
            },
            modifier = Modifier.fillMaxSize()
        )

        if (showControls) {
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .size(64.dp)
                    .background(Color.Black.copy(alpha = 0.55f), CircleShape)
            ) {
                IconButton(
                    onClick = {
                        if (vlcPlayer.isPlaying) {
                            vlcPlayer.pause()
                        } else {
                            vlcPlayer.play()
                        }
                    },
                    modifier = Modifier.fillMaxSize()
                ) {
                    Icon(
                        imageVector = if (isPlaying) Icons.Default.Pause else Icons.Default.PlayArrow,
                        contentDescription = if (isPlaying) "暂停" else "播放",
                        tint = Color.White,
                        modifier = Modifier.size(36.dp)
                    )
                }
            }

            if (durationMs > 0L) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .align(Alignment.BottomCenter)
                        .background(Color.Black.copy(alpha = 0.65f))
                        .navigationBarsPadding()
                        .padding(horizontal = 16.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = formatMediaTime(positionMs),
                        color = Color.White,
                        fontSize = 12.sp
                    )
                    Slider(
                        value = positionMs.toFloat().coerceIn(0f, durationMs.toFloat()),
                        onValueChange = { value ->
                            isSeeking = true
                            positionMs = value.toLong()
                        },
                        onValueChangeFinished = {
                            vlcPlayer.time = positionMs
                            isSeeking = false
                        },
                        valueRange = 0f..durationMs.toFloat(),
                        colors = SliderDefaults.colors(
                            thumbColor = GreenPrimary,
                            activeTrackColor = GreenPrimary
                        ),
                        modifier = Modifier
                            .weight(1f)
                            .padding(horizontal = 8.dp)
                    )
                    Text(
                        text = formatMediaTime(durationMs),
                        color = Color.White,
                        fontSize = 12.sp
                    )
                }
            }
        }
    }
}

private fun formatMediaTime(ms: Long): String {
    val totalSeconds = (ms / 1000L).coerceAtLeast(0L)
    val minutes = totalSeconds / 60L
    val seconds = totalSeconds % 60L
    return "%02d:%02d".format(minutes, seconds)
}
