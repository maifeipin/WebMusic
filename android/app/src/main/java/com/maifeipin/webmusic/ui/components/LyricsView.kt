package com.maifeipin.webmusic.ui.components

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.maifeipin.webmusic.core.lyrics.LyricLine
import com.maifeipin.webmusic.core.lyrics.LyricsParser
import com.maifeipin.webmusic.ui.theme.GreenPrimary

@Composable
fun LyricsView(
    lyrics: List<LyricLine>,
    currentPositionMs: Long,
    onSeekTo: (Long) -> Unit,
    modifier: Modifier = Modifier,
    onRequestLyrics: (() -> Unit)? = null
) {
    if (lyrics.isEmpty()) {
        Box(modifier = modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text(
                    text = "纯音乐 / 暂无歌词",
                    style = MaterialTheme.typography.bodyLarge,
                    color = Color.Gray
                )
                if (onRequestLyrics != null) {
                    Spacer(modifier = Modifier.height(16.dp))
                    Button(
                        onClick = onRequestLyrics,
                        colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp)
                    ) {
                        Icon(
                            imageVector = Icons.Default.Search,
                            contentDescription = null,
                            tint = Color.Black,
                            modifier = Modifier.size(18.dp)
                        )
                        Spacer(modifier = Modifier.width(6.dp))
                        Text(
                            text = "搜索 / 匹配歌词",
                            color = Color.Black,
                            fontWeight = FontWeight.Bold,
                            fontSize = 13.sp
                        )
                    }
                }
            }
        }
        return
    }

    val currentIndex = LyricsParser.findCurrentIndex(lyrics, currentPositionMs)
    val listState = rememberLazyListState()

    // 自动居中平滑滚动到当前歌词行
    LaunchedEffect(currentIndex) {
        if (currentIndex in lyrics.indices) {
            val targetScroll = (currentIndex - 3).coerceAtLeast(0)
            listState.animateScrollToItem(targetScroll)
        }
    }

    LazyColumn(
        state = listState,
        modifier = modifier
            .fillMaxSize()
            .padding(horizontal = 24.dp),
        contentPadding = PaddingValues(vertical = 120.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        itemsIndexed(lyrics) { index, line ->
            val isCurrent = index == currentIndex
            val alpha by animateFloatAsState(targetValue = if (isCurrent) 1.0f else 0.45f, label = "alpha")
            val fontSize = if (isCurrent) 20.sp else 16.sp
            val fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.Normal
            val color = if (isCurrent) GreenPrimary else Color.White

            Text(
                text = line.text,
                fontSize = fontSize,
                fontWeight = fontWeight,
                color = color,
                textAlign = TextAlign.Center,
                modifier = Modifier
                    .fillMaxWidth()
                    .alpha(alpha)
                    .clickable { onSeekTo(line.timeMs) }
                    .padding(vertical = 4.dp)
            )
        }
    }
}
