package com.maifeipin.webmusic.ui.components

import androidx.compose.animation.core.*
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.maifeipin.webmusic.core.lyrics.LyricLine
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.player.PlaybackMode
import com.maifeipin.webmusic.ui.theme.DarkBackground
import com.maifeipin.webmusic.ui.theme.GreenPrimary

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FullPlayerSheet(
    currentSong: MediaFileDto?,
    isPlaying: Boolean,
    progress: Float,
    durationMs: Long,
    currentPosMs: Long,
    seekEnabled: Boolean = true,
    playbackMode: PlaybackMode,
    lyrics: List<LyricLine>,
    isFavorite: Boolean = false,
    onToggleFavorite: () -> Unit = {},
    onAddToPlaylist: () -> Unit = {},
    onPlayPauseClick: () -> Unit,
    onPreviousClick: () -> Unit,
    onNextClick: () -> Unit,
    onSeekTo: (Long) -> Unit,
    onToggleMode: () -> Unit,
    onDismiss: () -> Unit
) {
    if (currentSong == null) return

    var showLyrics by remember { mutableStateOf(false) }
    var showEqualizerDialog by remember { mutableStateOf(false) }

    val infiniteTransition = rememberInfiniteTransition(label = "vinyl_disc")
    val rotation by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 15000, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "rotation"
    )

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        containerColor = DarkBackground,
        dragHandle = {
            BottomSheetDefaults.DragHandle(color = Color.Gray)
        },
        modifier = Modifier.fillMaxHeight(0.95f)
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 24.dp)
                .padding(bottom = 32.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            // 顶部收起、均衡器与副标题
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = onDismiss) {
                    Icon(Icons.Default.KeyboardArrowDown, contentDescription = "收起", tint = Color.White)
                }
                Text(
                    text = if (showLyrics) "歌词模式" else "播放中",
                    style = MaterialTheme.typography.titleSmall,
                    color = Color.Gray
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = { showEqualizerDialog = true }) {
                        Icon(
                            imageVector = Icons.Default.GraphicEq,
                            contentDescription = "音频均衡器",
                            tint = Color.White
                        )
                    }
                    IconButton(onClick = { showLyrics = !showLyrics }) {
                        Icon(
                            imageVector = if (showLyrics) Icons.Default.MusicNote else Icons.Default.FormatQuote,
                            contentDescription = "切换歌词/唱片",
                            tint = if (showLyrics) GreenPrimary else Color.White
                        )
                    }
                }
            }

            Spacer(modifier = Modifier.height(16.dp))

            // 中间核心：唱片转盘 或 歌词
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                contentAlignment = Alignment.Center
            ) {
                if (showLyrics) {
                    LyricsView(
                        lyrics = lyrics,
                        currentPositionMs = currentPosMs,
                        onSeekTo = onSeekTo
                    )
                } else {
                    // 黑胶大黑胶唱盘
                    Box(
                        modifier = Modifier
                            .size(280.dp)
                            .shadow(16.dp, CircleShape)
                            .clip(CircleShape)
                            .background(
                                Brush.radialGradient(
                                    colors = listOf(Color(0xFF222222), Color(0xFF111111), Color.Black)
                                )
                            )
                            .rotate(if (isPlaying) rotation else 0f)
                            .clickable { showLyrics = true },
                        contentAlignment = Alignment.Center
                    ) {
                        // 唱片纹理与中心轴
                        Box(
                            modifier = Modifier
                                .size(90.dp)
                                .clip(CircleShape)
                                .background(GreenPrimary),
                            contentAlignment = Alignment.Center
                        ) {
                            Box(
                                modifier = Modifier
                                    .size(24.dp)
                                    .clip(CircleShape)
                                    .background(DarkBackground)
                            )
                        }
                    }
                }
            }

            Spacer(modifier = Modifier.height(24.dp))

            // 歌曲信息与操作栏 (收藏 & 加入歌单)
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = currentSong.title ?: "未知曲目",
                        fontWeight = FontWeight.Bold,
                        fontSize = 20.sp,
                        color = Color.White,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(modifier = Modifier.height(2.dp))
                    Text(
                        text = "${currentSong.artist ?: "未知艺术家"} • ${currentSong.album ?: "未知专辑"}",
                        fontSize = 13.sp,
                        color = Color.LightGray,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = onToggleFavorite) {
                        Icon(
                            imageVector = if (isFavorite) Icons.Filled.Favorite else Icons.Default.FavoriteBorder,
                            contentDescription = if (isFavorite) "取消收藏" else "加入收藏",
                            tint = if (isFavorite) Color(0xFFE91E63) else Color.LightGray,
                            modifier = Modifier.size(26.dp)
                        )
                    }
                    IconButton(onClick = onAddToPlaylist) {
                        Icon(
                            imageVector = Icons.Default.PlaylistAdd,
                            contentDescription = "添加到歌单",
                            tint = Color.LightGray,
                            modifier = Modifier.size(26.dp)
                        )
                    }
                }
            }

            Spacer(modifier = Modifier.height(20.dp))

            // 进度条与时间（时长未知或当前曲目不可 seek 时禁用拖动）
            Slider(
                value = progress,
                onValueChange = { newProg ->
                    val target = (newProg * durationMs).toLong()
                    onSeekTo(target)
                },
                modifier = Modifier.fillMaxWidth(),
                enabled = seekEnabled && durationMs > 0L,
                colors = SliderDefaults.colors(
                    thumbColor = GreenPrimary,
                    activeTrackColor = GreenPrimary,
                    inactiveTrackColor = Color.DarkGray
                )
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Text(formatTime(currentPosMs), style = MaterialTheme.typography.bodySmall, color = Color.Gray)
                Text(formatTime(durationMs), style = MaterialTheme.typography.bodySmall, color = Color.Gray)
            }

            Spacer(modifier = Modifier.height(16.dp))

            // 控制按钮行 (循环模式 / 上一曲 / 播放暂停 / 下一曲 / 歌单)
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.CenterVertically
            ) {
                // 循环模式切换
                IconButton(onClick = onToggleMode) {
                    Icon(
                        imageVector = when (playbackMode) {
                            PlaybackMode.SEQUENCE -> Icons.Default.Repeat
                            PlaybackMode.REPEAT_ONE -> Icons.Default.RepeatOne
                            PlaybackMode.SHUFFLE -> Icons.Default.Shuffle
                        },
                        contentDescription = "播放模式",
                        tint = if (playbackMode != PlaybackMode.SEQUENCE) GreenPrimary else Color.White
                    )
                }

                // 上一曲
                IconButton(onClick = onPreviousClick) {
                    Icon(
                        imageVector = Icons.Default.SkipPrevious,
                        contentDescription = "上一首",
                        tint = Color.White,
                        modifier = Modifier.size(36.dp)
                    )
                }

                // 大号主播放暂停按钮
                FloatingActionButton(
                    onClick = onPlayPauseClick,
                    containerColor = GreenPrimary,
                    shape = CircleShape,
                    modifier = Modifier.size(68.dp)
                ) {
                    Icon(
                        imageVector = if (isPlaying) Icons.Default.Pause else Icons.Default.PlayArrow,
                        contentDescription = "播放/暂停",
                        tint = Color.Black,
                        modifier = Modifier.size(36.dp)
                    )
                }

                // 下一曲
                IconButton(onClick = onNextClick) {
                    Icon(
                        imageVector = Icons.Default.SkipNext,
                        contentDescription = "下一首",
                        tint = Color.White,
                        modifier = Modifier.size(36.dp)
                    )
                }

                // 歌词快捷切入
                IconButton(onClick = { showLyrics = !showLyrics }) {
                    Icon(
                        imageVector = Icons.Default.MenuBook,
                        contentDescription = "歌词",
                        tint = if (showLyrics) GreenPrimary else Color.White
                    )
                }
            }
        }
    }

    if (showEqualizerDialog) {
        com.maifeipin.webmusic.ui.EqualizerDialog(
            onDismiss = { showEqualizerDialog = false }
        )
    }
}

private fun formatTime(ms: Long): String {
    val totalSeconds = (ms / 1000).toInt()
    val minutes = totalSeconds / 60
    val seconds = totalSeconds % 60
    return String.format("%02d:%02d", minutes, seconds)
}
