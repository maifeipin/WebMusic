package com.maifeipin.webmusic.ui.components

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.net.Uri
import android.util.Log
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.zIndex
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.VideoSize
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import com.maifeipin.webmusic.core.cache.MediaCacheManager
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import com.maifeipin.webmusic.ui.theme.GreenPrimary
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

    val exoPlayer = remember {
        ExoPlayer.Builder(context).build().apply {
            playWhenReady = true
        }
    }

    LaunchedEffect(song.id, localFilePath) {
        if (!localFilePath.isNullOrEmpty()) {
            val uri = com.maifeipin.webmusic.core.storage.MediaImportFilter.toPlayableUri(localFilePath)
            val mediaItem = MediaItem.fromUri(uri)
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

    // 记录一次真实视频尺寸，排查黑边/比例问题时有据可查
    DisposableEffect(exoPlayer) {
        val listener = object : Player.Listener {
            override fun onVideoSizeChanged(videoSize: VideoSize) {
                Log.d(
                    FULLSCREEN_TAG,
                    "video size=${videoSize.width}x${videoSize.height} par=${videoSize.pixelWidthHeightRatio}"
                )
            }
        }
        exoPlayer.addListener(listener)
        onDispose { exoPlayer.removeListener(listener) }
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
                    activity?.window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                } else if (isApplied()) {
                    // 只有真正应用过全屏才回退；首次组合不该动用户既有的屏幕方向
                    markApplied(false)
                    restoreOrientation(activity, originalOrientation, wasPortrait)
                    ViewCompat.getWindowInsetsController(composeView)
                        ?.show(WindowInsetsCompat.Type.systemBars())
                    activity?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                }
                Log.d(
                    FULLSCREEN_TAG,
                    "apply fullscreen=$isFullscreen applied=${isApplied()} " +
                        "orientation=${configuration.orientation} ratio=$videoRatio"
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
