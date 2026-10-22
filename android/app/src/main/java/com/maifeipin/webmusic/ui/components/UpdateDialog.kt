package com.maifeipin.webmusic.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.NewReleases
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.maifeipin.webmusic.core.update.AppVersionDto
import com.maifeipin.webmusic.core.update.UpdateController
import com.maifeipin.webmusic.ui.theme.GreenPrimary

/**
 * 应用内更新流程的唯一 UI 宿主：状态由 [UpdateController] 驱动，这里只做渲染。
 */
@Composable
fun UpdateDialogHost(controller: UpdateController) {
    val state by controller.state.collectAsState()

    when (val current = state) {
        is UpdateController.State.Idle -> Unit

        is UpdateController.State.Checking -> AlertDialog(
            onDismissRequest = { controller.dismiss() },
            title = { Text("检查更新") },
            text = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(20.dp),
                        color = GreenPrimary,
                        strokeWidth = 2.dp
                    )
                    Spacer(Modifier.width(12.dp))
                    Text("正在获取最新版本信息…", color = Color.Gray, fontSize = 14.sp)
                }
            },
            confirmButton = {
                TextButton(onClick = { controller.dismiss() }) { Text("取消") }
            }
        )

        is UpdateController.State.UpToDate -> AlertDialog(
            onDismissRequest = { controller.dismiss() },
            icon = { Icon(Icons.Default.NewReleases, contentDescription = null, tint = GreenPrimary) },
            title = { Text("已是最新版本", fontWeight = FontWeight.Bold) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("当前版本：v${current.versionName}", color = Color.Gray, fontSize = 13.sp)
                    Text("渠道：${controller.channel}", color = Color.Gray, fontSize = 13.sp)
                }
            },
            confirmButton = {
                TextButton(onClick = { controller.dismiss() }) { Text("好") }
            }
        )

        is UpdateController.State.Available -> UpdateAvailableDialog(
            info = current.info,
            channel = controller.channel,
            onDownload = { controller.download() },
            onSkip = { controller.skipThisVersion() },
            onLater = { controller.dismiss() }
        )

        is UpdateController.State.Downloading -> AlertDialog(
            onDismissRequest = { /* 下载中禁止点击外部关闭，避免误触 */ },
            icon = { Icon(Icons.Default.CloudDownload, contentDescription = null, tint = GreenPrimary) },
            title = { Text("正在下载更新", fontWeight = FontWeight.Bold) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(
                        text = "v${current.info.versionName ?: current.info.versionCode}",
                        color = Color.White,
                        fontSize = 14.sp
                    )
                    LinearProgressIndicator(
                        progress = { current.progress.fraction },
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(6.dp),
                        color = GreenPrimary,
                        trackColor = Color.DarkGray
                    )
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(
                            text = if (current.progress.totalBytes > 0L) {
                                "${formatBytes(current.progress.receivedBytes)} / ${formatBytes(current.progress.totalBytes)}"
                            } else {
                                "已下载 ${formatBytes(current.progress.receivedBytes)}"
                            },
                            color = Color.Gray,
                            fontSize = 12.sp
                        )
                        Text(
                            text = "${(current.progress.fraction * 100).toInt()}%",
                            color = GreenPrimary,
                            fontSize = 12.sp,
                            fontWeight = FontWeight.Bold
                        )
                    }
                    Text(
                        text = "已支持断点续传，中断后再次下载会从断点继续。",
                        color = Color.Gray,
                        fontSize = 11.sp
                    )
                }
            },
            confirmButton = {
                TextButton(onClick = { controller.cancelDownload() }) {
                    Text("取消下载", color = Color(0xFFFF5252))
                }
            }
        )

        is UpdateController.State.ReadyToInstall -> AlertDialog(
            onDismissRequest = { controller.dismiss() },
            icon = { Icon(Icons.Default.SystemUpdate, contentDescription = null, tint = GreenPrimary) },
            title = { Text("准备安装", fontWeight = FontWeight.Bold) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(
                        text = "v${current.info.versionName ?: current.info.versionCode} 已下载并通过完整性校验。",
                        color = Color.White,
                        fontSize = 14.sp
                    )
                    Text(
                        text = if (current.needsPermission) {
                            "需要先允许本应用安装应用，授权返回后会自动继续安装。"
                        } else {
                            "点击“立即安装”将调起系统安装界面。登录状态、离线音乐与本地数据库都会完整保留。"
                        },
                        color = if (current.needsPermission) Color(0xFFFFB74D) else Color.Gray,
                        fontSize = 12.sp
                    )
                }
            },
            confirmButton = {
                Button(
                    onClick = { controller.install() },
                    colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary)
                ) {
                    Text(
                        text = if (current.needsPermission) "去授权" else "立即安装",
                        color = Color.Black
                    )
                }
            },
            dismissButton = {
                TextButton(onClick = { controller.dismiss() }) {
                    Text("稍后")
                }
            }
        )

        is UpdateController.State.Failed -> AlertDialog(
            onDismissRequest = { controller.dismiss() },
            icon = { Icon(Icons.Default.ErrorOutline, contentDescription = null, tint = Color(0xFFFF5252)) },
            title = { Text("更新未完成", fontWeight = FontWeight.Bold) },
            text = {
                Text(current.message, color = Color.Gray, fontSize = 13.sp)
            },
            confirmButton = {
                TextButton(onClick = {
                    if (current.info != null) controller.download() else controller.checkManually()
                }) {
                    Text("重试", color = GreenPrimary)
                }
            },
            dismissButton = {
                TextButton(onClick = { controller.dismiss() }) { Text("关闭") }
            }
        )
    }
}

@Composable
private fun UpdateAvailableDialog(
    info: AppVersionDto,
    channel: String,
    onDownload: () -> Unit,
    onSkip: () -> Unit,
    onLater: () -> Unit
) {
    AlertDialog(
        onDismissRequest = { if (!info.forceUpdate) onLater() },
        icon = { Icon(Icons.Default.NewReleases, contentDescription = null, tint = GreenPrimary) },
        title = {
            Text(
                text = "发现新版本 v${info.versionName ?: info.versionCode}",
                fontWeight = FontWeight.Bold
            )
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    Text(
                        text = "渠道 $channel",
                        color = Color.Gray,
                        fontSize = 12.sp
                    )
                    if (info.sizeBytes > 0L) {
                        Text(
                            text = "安装包 ${formatBytes(info.sizeBytes)}",
                            color = Color.Gray,
                            fontSize = 12.sp
                        )
                    }
                }

                if (info.forceUpdate) {
                    Text(
                        text = "此版本为强制更新，完成后才能继续使用。",
                        color = Color(0xFFFFB74D),
                        fontSize = 12.sp
                    )
                }

                val changelog = info.changelog?.trim().orEmpty()
                if (changelog.isNotEmpty()) {
                    Text(
                        text = "更新内容",
                        color = Color.White,
                        fontSize = 13.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(max = 180.dp)
                            .verticalScroll(rememberScrollState())
                    ) {
                        Text(
                            text = changelog,
                            color = Color.Gray,
                            fontSize = 13.sp,
                            lineHeight = 20.sp
                        )
                    }
                }
            }
        },
        confirmButton = {
            Button(
                onClick = onDownload,
                colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary)
            ) {
                Text("立即更新", color = Color.Black)
            }
        },
        dismissButton = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (!info.forceUpdate) {
                    TextButton(onClick = onSkip) { Text("跳过此版本", fontSize = 12.sp) }
                }
                TextButton(onClick = onLater) { Text("稍后", fontSize = 12.sp) }
            }
        }
    )
}

private fun formatBytes(bytes: Long): String {
    if (bytes <= 0L) return "0 B"
    val kb = bytes / 1024.0
    if (kb < 1024) return "%.0f KB".format(kb)
    val mb = kb / 1024.0
    return "%.1f MB".format(mb)
}
