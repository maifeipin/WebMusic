package com.maifeipin.webmusic.ui.components

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.net.Uri
import android.view.WindowManager
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
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
 * 沉浸式 MV 视频播放器对话框
 * - 支持流媒体与离线本地视频播放
 * - 结合 MediaCacheManager 实现边播边存分片与 HTTP 206 Range 高效拖拽
 * - 顶部标题栏提供「全屏」入口：切到横屏 + 隐藏状态栏/导航栏 + 收起标题栏，
 *   退出全屏后恢复进入前的屏幕方向与系统栏
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
    // Dialog 里的 LocalContext 是 ContextThemeWrapper，需要顺着 baseContext 找到宿主 Activity
    val activity = remember(context) { context.findHostActivity() }
    val composeView = LocalView.current

    // 记住进入播放器前的方向，退出全屏时原样恢复，避免覆盖用户自己锁定的屏幕方向
    val originalOrientation = remember(activity) {
        activity?.requestedOrientation ?: ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
    }
    // 原值是 UNSPECIFIED 时，恢复要靠「进入前实际是横还是竖」，而不是那个值本身
    val wasPortrait = remember(activity) {
        activity?.resources?.configuration?.orientation != Configuration.ORIENTATION_LANDSCAPE
    }
    var isFullscreen by remember { mutableStateOf(false) }

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

    DisposableEffect(exoPlayer) {
        onDispose {
            exoPlayer.stop()
            exoPlayer.release()
        }
    }

    // 全屏 = 横向 + 隐藏系统栏 + 屏幕常亮。
    // 方向只对 Activity 生效，系统栏作用于当前窗口（这个 Dialog 自己的 window）。
    DisposableEffect(isFullscreen) {
        var alive = true
        val applySystemUi = {
            if (alive) {
                if (isFullscreen) {
                    activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    ViewCompat.getWindowInsetsController(composeView)?.apply {
                        // 用户从边缘滑动可以临时唤出系统栏，转瞬后自动再隐藏
                        systemBarsBehavior =
                            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                        hide(WindowInsetsCompat.Type.systemBars())
                    }
                    activity?.window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                } else {
                    restoreOrientation(activity, originalOrientation, wasPortrait)
                    ViewCompat.getWindowInsetsController(composeView)
                        ?.show(WindowInsetsCompat.Type.systemBars())
                    activity?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                }
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

    // 兜底：无论以什么方式离开播放器（返回键、点击关闭、Activity 销毁），
    // 都恢复屏幕方向与系统栏，否则会残留在横屏 + 无状态栏的界面上
    DisposableEffect(Unit) {
        onDispose {
            restoreOrientation(activity, originalOrientation, wasPortrait)
            ViewCompat.getWindowInsetsController(composeView)
                ?.show(WindowInsetsCompat.Type.systemBars())
            // Dialog 的 window 这时可能已经销毁，同时对 Activity 的窗口恢复一次更保险
            activity?.window?.decorView?.let {
                ViewCompat.getWindowInsetsController(it)
                    ?.show(WindowInsetsCompat.Type.systemBars())
            }
            activity?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    Dialog(
        onDismissRequest = {
            // 全屏时返回键先退出全屏，而不是直接关掉播放器（再按一次才关闭）
            if (isFullscreen) isFullscreen = false else onDismiss()
        },
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
}

/** 顺着 ContextWrapper 链向上找到宿主 Activity（Dialog 的 context 只是层包装）。 */
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
