package com.maifeipin.webmusic.ui.components

import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DownloadDone
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.maifeipin.webmusic.ui.theme.GreenPrimary

/**
 * 曲目行尾的下载状态位。
 *
 * 三态：
 * - 下载中：进度环。[progress] 为 0 时用**不确定进度**的转圈，因为 0% 的确定进度环在深色
 *   背景上几乎是一条看不见的细线，用户会以为点击没生效（服务端读 SMB 首字节可能要好几秒）。
 * - 已离线：绿色 ✓。
 * - 未下载：下载图标按钮，点击回调 [onStartDownload]。
 */
@Composable
fun SongDownloadAction(
    isDownloaded: Boolean,
    progress: Float?,
    onStartDownload: () -> Unit,
    modifier: Modifier = Modifier
) {
    when {
        progress != null && progress < 0.02f -> {
            // 已经开始但还没有第一个字节：给转圈，明确「请求已发出」
            CircularProgressIndicator(
                modifier = modifier.size(22.dp),
                color = GreenPrimary,
                strokeWidth = 2.dp
            )
        }

        progress != null -> {
            CircularProgressIndicator(
                progress = { progress.coerceIn(0f, 1f) },
                modifier = modifier.size(22.dp),
                color = GreenPrimary,
                strokeWidth = 2.dp
            )
        }

        isDownloaded -> {
            Icon(
                imageVector = Icons.Default.DownloadDone,
                contentDescription = "已离线",
                tint = GreenPrimary,
                modifier = modifier.size(22.dp)
            )
        }

        else -> {
            IconButton(onClick = onStartDownload, modifier = modifier.size(36.dp)) {
                Icon(
                    imageVector = Icons.Default.Download,
                    contentDescription = "下载到本地",
                    tint = Color.Gray,
                    modifier = Modifier.size(22.dp)
                )
            }
        }
    }
}
