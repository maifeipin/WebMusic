package com.maifeipin.webmusic.core.lyrics

import com.maifeipin.webmusic.core.network.MediaFileDto
import kotlin.math.abs

/** 网易云搜索结果条目（只保留展示与拉取歌词需要的字段） */
data class NeteaseCandidate(
    val id: Long,
    val name: String,
    val artists: String,
    val album: String,
    val durationMs: Long
)

/**
 * 解析本曲时长（毫秒）。顺序：接口 totalSeconds → duration 字符串（"231.45" 秒 或 "3:51"）
 * → 播放器实测时长。播放器实测值只在前面都拿不到时兜底。
 *
 * 时长用于把网易云候选按「最接近本曲」排序，解析失败会让排序整体失效，因此这里对
 * 三种常见存储形态都做兼容。
 */
fun resolveSongDurationMs(song: MediaFileDto, knownDurationMs: Long = 0L): Long {
    song.totalSeconds?.let { if (it > 0) return (it * 1000).toLong() }

    val raw = song.duration?.trim().orEmpty()
    if (raw.isNotEmpty()) {
        raw.toDoubleOrNull()?.let { if (it > 0) return (it * 1000).toLong() }
        if (raw.contains(":")) {
            val parts = raw.split(":").mapNotNull { it.trim().toDoubleOrNull() }
            if (parts.size in 2..3 && parts.all { it >= 0 }) {
                var seconds = 0.0
                parts.forEach { seconds = seconds * 60 + it }
                if (seconds > 0) return (seconds * 1000).toLong()
            }
        }
    }

    return if (knownDurationMs > 0) knownDurationMs else 0L
}

/**
 * 按「与本曲时长差最小」升序排序；时长未知（0）的候选保留原顺序排在末尾。
 * 本曲时长未知时不做任何排序，避免把列表打乱成没有依据的顺序。
 */
fun sortByDurationProximity(
    candidates: List<NeteaseCandidate>,
    songDurationMs: Long
): List<NeteaseCandidate> {
    if (songDurationMs <= 0L || candidates.size < 2) return candidates
    return candidates.sortedWith(
        compareBy<NeteaseCandidate> { candidate ->
            if (candidate.durationMs > 0L) abs(candidate.durationMs - songDurationMs) else Long.MAX_VALUE
        }
    )
}

/** 毫秒 → "m:ss"（超过 1 小时给 "h:mm:ss"） */
fun formatDurationMs(ms: Long): String {
    if (ms <= 0L) return "--:--"
    val totalSeconds = ms / 1000
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) {
        String.format("%d:%02d:%02d", hours, minutes, seconds)
    } else {
        String.format("%d:%02d", minutes, seconds)
    }
}

/** 两条时长的差值，人类可读："3s" / "1:05" */
fun formatDurationDelta(deltaMs: Long): String {
    val seconds = (abs(deltaMs) + 500) / 1000
    return if (seconds < 60) "${seconds}s" else formatDurationMs(seconds * 1000)
}
