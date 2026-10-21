package com.maifeipin.webmusic.core.lyrics

data class LyricLine(
    val timeMs: Long,
    val text: String
)

data class LyricsResponse(
    val id: Int,
    val content: String?,
    val language: String?,
    val source: String?
)

object LyricsParser {

    private val LRC_REGEX = Regex("""\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)""")

    fun parse(lrcContent: String?): List<LyricLine> {
        if (lrcContent.isNullOrBlank()) return emptyList()

        val lines = mutableListOf<LyricLine>()
        for (rawLine in lrcContent.lines()) {
            val trimmed = rawLine.trim()
            if (trimmed.isEmpty()) continue

            val match = LRC_REGEX.matchEntire(trimmed)
            if (match != null) {
                val min = match.groupValues[1].toLongOrNull() ?: 0L
                val sec = match.groupValues[2].toLongOrNull() ?: 0L
                val msStr = match.groupValues[3]
                val ms = when (msStr.length) {
                    1 -> (msStr.toLongOrNull() ?: 0L) * 100
                    2 -> (msStr.toLongOrNull() ?: 0L) * 10
                    3 -> msStr.toLongOrNull() ?: 0L
                    else -> 0L
                }
                val totalMs = min * 60 * 1000 + sec * 1000 + ms
                val text = match.groupValues[4].trim()
                if (text.isNotEmpty()) {
                    lines.add(LyricLine(totalMs, text))
                }
            }
        }
        return lines.sortedBy { it.timeMs }
    }

    /**
     * 根据当前播放毫秒，二分查找或线性匹配当前正在高亮播放的歌词行索引
     */
    fun findCurrentIndex(lines: List<LyricLine>, currentMs: Long): Int {
        if (lines.isEmpty()) return -1
        if (currentMs < lines.first().timeMs) return 0

        for (i in lines.indices.reversed()) {
            if (currentMs >= lines[i].timeMs) {
                return i
            }
        }
        return 0
    }
}
