package com.maifeipin.webmusic.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.PlaylistDetailDto
import com.maifeipin.webmusic.core.network.TokenManager
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import com.maifeipin.webmusic.ui.theme.DarkBackground
import com.maifeipin.webmusic.ui.theme.DarkSurface
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun PlaylistDetailView(
    playlistId: Int,
    tokenManager: TokenManager,
    favoriteSongIds: Set<Int>,
    currentSongId: Int?,
    isPlaying: Boolean,
    onBack: () -> Unit,
    onPlaySongFromList: (List<MediaFileDto>, Int) -> Unit,
    onToggleFavorite: (Int) -> Unit
) {
    val scope = rememberCoroutineScope()
    var detail by remember { mutableStateOf<PlaylistDetailDto?>(null) }
    var isLoading by remember { mutableStateOf(true) }
    var statusText by remember { mutableStateOf("") }

    fun loadDetail() {
        scope.launch {
            isLoading = true
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val res = withContext(Dispatchers.IO) { api.getPlaylist(playlistId) }
                detail = res
            } catch (e: Exception) {
                statusText = "加载歌单详情失败: ${e.message}"
            } finally {
                isLoading = false
            }
        }
    }

    LaunchedEffect(playlistId) {
        loadDetail()
    }

    val songs = remember(detail) {
        detail?.songs?.mapNotNull { it.song?.toMediaFileDto() } ?: emptyList()
    }

    Column(modifier = Modifier.fillMaxSize().background(DarkBackground)) {
        // 顶栏
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onBack) {
                Icon(Icons.Default.ArrowBack, contentDescription = "返回", tint = Color.White)
            }
            Column(modifier = Modifier.weight(1f).padding(start = 4.dp)) {
                Text(
                    text = detail?.name ?: "歌单详情",
                    fontWeight = FontWeight.Bold,
                    fontSize = 18.sp,
                    color = Color.White,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    text = "${songs.size} 首歌曲",
                    fontSize = 12.sp,
                    color = Color.Gray
                )
            }
            if (songs.isNotEmpty()) {
                Button(
                    onClick = { onPlaySongFromList(songs, 0) },
                    colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                    contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
                    shape = RoundedCornerShape(20.dp)
                ) {
                    Icon(Icons.Default.PlayArrow, contentDescription = null, tint = Color.Black, modifier = Modifier.size(18.dp))
                    Spacer(modifier = Modifier.width(4.dp))
                    Text("播放全部", color = Color.Black, fontSize = 13.sp, fontWeight = FontWeight.Bold)
                }
            }
        }

        HorizontalDivider(color = Color.DarkGray)

        if (isLoading) {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = GreenPrimary)
            }
        } else if (songs.isEmpty()) {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("歌单内暂无歌曲，可在曲库中添加", color = Color.Gray, fontSize = 14.sp)
            }
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(bottom = 24.dp)
            ) {
                itemsIndexed(songs) { index, song ->
                    val isCurrent = currentSongId == song.id
                    val isFav = favoriteSongIds.contains(song.id)

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
                                    .size(36.dp)
                                    .clip(CircleShape)
                                    .background(if (isCurrent) GreenPrimary else DarkSurface),
                                contentAlignment = Alignment.Center
                            ) {
                                Icon(
                                    imageVector = if (isCurrent && isPlaying) Icons.Default.VolumeUp else Icons.Default.MusicNote,
                                    contentDescription = null,
                                    tint = if (isCurrent) Color.Black else Color.Gray,
                                    modifier = Modifier.size(18.dp)
                                )
                            }
                        },
                        trailingContent = {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                // 收藏红心按钮
                                IconButton(onClick = { onToggleFavorite(song.id) }) {
                                    Icon(
                                        imageVector = if (isFav) Icons.Filled.Favorite else Icons.Default.FavoriteBorder,
                                        contentDescription = if (isFav) "取消收藏" else "收藏",
                                        tint = if (isFav) Color(0xFFE91E63) else Color.Gray,
                                        modifier = Modifier.size(20.dp)
                                    )
                                }
                                // 从歌单移除按钮
                                IconButton(onClick = {
                                    scope.launch {
                                        try {
                                            val api = WebMusicNetwork.createApi(tokenManager)
                                            withContext(Dispatchers.IO) {
                                                api.removeSongsFromPlaylist(playlistId, song.id.toString())
                                            }
                                            loadDetail()
                                        } catch (e: Exception) {
                                            statusText = "移除失败: ${e.message}"
                                        }
                                    }
                                }) {
                                    Icon(
                                        imageVector = Icons.Default.Close,
                                        contentDescription = "从歌单移除",
                                        tint = Color.Gray,
                                        modifier = Modifier.size(18.dp)
                                    )
                                }
                            }
                        },
                        modifier = Modifier
                            .clickable { onPlaySongFromList(songs, index) }
                            .padding(horizontal = 6.dp)
                    )
                }
            }
        }
    }
}
