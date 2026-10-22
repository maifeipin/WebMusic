package com.maifeipin.webmusic.ui.components

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.QueueMusic
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.maifeipin.webmusic.core.network.CreatePlaylistRequest
import com.maifeipin.webmusic.core.network.PlaylistSummaryDto
import com.maifeipin.webmusic.core.network.TokenManager
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import com.maifeipin.webmusic.ui.theme.DarkBackground
import com.maifeipin.webmusic.ui.theme.DarkSurface
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun AddToPlaylistDialog(
    songId: Int,
    songTitle: String,
    tokenManager: TokenManager,
    onDismiss: () -> Unit,
    onSuccess: (playlistName: String) -> Unit
) {
    val scope = rememberCoroutineScope()
    var playlists by remember { mutableStateOf<List<PlaylistSummaryDto>>(emptyList()) }
    var isLoading by remember { mutableStateOf(true) }
    var showCreateNew by remember { mutableStateOf(false) }
    var newPlaylistName by remember { mutableStateOf("") }
    var statusText by remember { mutableStateOf("") }
    var isSubmitting by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        try {
            val api = WebMusicNetwork.createApi(tokenManager)
            val res = withContext(Dispatchers.IO) { api.getPlaylists() }
            playlists = res
        } catch (e: Exception) {
            statusText = "获取歌单失败: ${e.message}"
        } finally {
            isLoading = false
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Column {
                Text("添加到歌单", fontWeight = FontWeight.Bold, color = Color.White)
                Text(
                    text = "目标歌曲: $songTitle",
                    fontSize = 12.sp,
                    color = Color.Gray,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        },
        text = {
            Column(modifier = Modifier.fillMaxWidth().heightIn(max = 380.dp)) {
                if (showCreateNew) {
                    OutlinedTextField(
                        value = newPlaylistName,
                        onValueChange = { newPlaylistName = it },
                        label = { Text("新建歌单名称") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = GreenPrimary,
                            unfocusedBorderColor = Color.Gray
                        )
                    )
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.End
                    ) {
                        TextButton(onClick = { showCreateNew = false }) {
                            Text("取消", color = Color.Gray)
                        }
                        Button(
                            onClick = {
                                if (newPlaylistName.isNotBlank() && !isSubmitting) {
                                    scope.launch {
                                        isSubmitting = true
                                        try {
                                            val api = WebMusicNetwork.createApi(tokenManager)
                                            val created = withContext(Dispatchers.IO) {
                                                api.createPlaylist(CreatePlaylistRequest(name = newPlaylistName.trim()))
                                            }
                                            withContext(Dispatchers.IO) {
                                                api.addSongsToPlaylist(created.id, listOf(songId))
                                            }
                                            onSuccess(created.name)
                                            onDismiss()
                                        } catch (e: Exception) {
                                            statusText = "创建并添加失败: ${e.message}"
                                        } finally {
                                            isSubmitting = false
                                        }
                                    }
                                }
                            },
                            colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                            enabled = newPlaylistName.isNotBlank() && !isSubmitting
                        ) {
                            Text("创建并添加", color = Color.Black)
                        }
                    }
                } else {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(8.dp))
                            .background(DarkSurface)
                            .clickable { showCreateNew = true }
                            .padding(12.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Icon(Icons.Default.Add, contentDescription = "新建歌单", tint = GreenPrimary)
                        Spacer(modifier = Modifier.width(8.dp))
                        Text("新建自建歌单", color = GreenPrimary, fontWeight = FontWeight.SemiBold)
                    }

                    Spacer(modifier = Modifier.height(10.dp))

                    if (isLoading) {
                        Box(modifier = Modifier.fillMaxWidth().height(120.dp), contentAlignment = Alignment.Center) {
                            CircularProgressIndicator(color = GreenPrimary, modifier = Modifier.size(24.dp))
                        }
                    } else if (playlists.isEmpty()) {
                        Text("暂无自建歌单，可点击上方新建", color = Color.Gray, fontSize = 13.sp, modifier = Modifier.padding(8.dp))
                    } else {
                        LazyColumn(modifier = Modifier.fillMaxWidth().weight(1f, fill = false)) {
                            items(playlists) { pl ->
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            if (!isSubmitting) {
                                                scope.launch {
                                                    isSubmitting = true
                                                    try {
                                                        val api = WebMusicNetwork.createApi(tokenManager)
                                                        withContext(Dispatchers.IO) {
                                                            api.addSongsToPlaylist(pl.id, listOf(songId))
                                                        }
                                                        onSuccess(pl.name)
                                                        onDismiss()
                                                    } catch (e: Exception) {
                                                        statusText = "添加到 ${pl.name} 失败: ${e.message}"
                                                    } finally {
                                                        isSubmitting = false
                                                    }
                                                }
                                            }
                                        }
                                        .padding(vertical = 10.dp, horizontal = 4.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Icon(
                                        imageVector = Icons.Default.QueueMusic,
                                        contentDescription = null,
                                        tint = Color.LightGray,
                                        modifier = Modifier.size(22.dp)
                                    )
                                    Spacer(modifier = Modifier.width(10.dp))
                                    Column(modifier = Modifier.weight(1f)) {
                                        Text(pl.name, color = Color.White, fontWeight = FontWeight.Medium, fontSize = 15.sp)
                                        Text("${pl.count} 首歌曲", color = Color.Gray, fontSize = 12.sp)
                                    }
                                }
                            }
                        }
                    }
                }

                if (statusText.isNotEmpty()) {
                    Text(statusText, color = MaterialTheme.colorScheme.error, fontSize = 12.sp, modifier = Modifier.padding(top = 6.dp))
                }
            }
        },
        confirmButton = {},
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("关闭", color = Color.Gray)
            }
        },
        containerColor = DarkBackground
    )
}
