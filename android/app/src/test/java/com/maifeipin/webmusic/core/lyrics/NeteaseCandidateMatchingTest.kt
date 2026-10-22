package com.maifeipin.webmusic.core.lyrics

import com.maifeipin.webmusic.core.network.MediaFileDto
import org.junit.Assert.assertEquals
import org.junit.Test

class NeteaseCandidateMatchingTest {

    private fun song(totalSeconds: Double? = null, duration: String? = null) = MediaFileDto(
        id = 1,
        filePath = null,
        title = "晴天",
        artist = "周杰伦",
        album = null,
        genre = null,
        year = null,
        duration = duration,
        sizeBytes = null,
        fileHash = null,
        totalSeconds = totalSeconds
    )

    private fun candidate(id: Long, durationMs: Long, name: String = "晴天") =
        NeteaseCandidate(id, name, "周杰伦", "叶惠美", durationMs)

    @Test
    fun putsClosestDurationFirst() {
        val list = listOf(
            candidate(11, 112_000),
            candidate(22, 268_000),
            candidate(33, 4_300_000)
        )
        val sorted = sortByDurationProximity(list, 270_738)
        assertEquals(listOf(22L, 11L, 33L), sorted.map { it.id })
    }

    @Test
    fun keepsUnknownDurationsAtTheEndInOriginalOrder() {
        val list = listOf(
            candidate(1, 0),
            candidate(2, 300_000),
            candidate(3, 0),
            candidate(4, 268_000)
        )
        val sorted = sortByDurationProximity(list, 270_000)
        assertEquals(listOf(4L, 2L, 1L, 3L), sorted.map { it.id })
    }

    @Test
    fun doesNotReorderWhenLocalDurationUnknown() {
        val list = listOf(candidate(1, 100_000), candidate(2, 200_000))
        assertEquals(list, sortByDurationProximity(list, 0L))
    }

    @Test
    fun resolvesDurationFromTotalSecondsFirst() {
        assertEquals(231_454L, resolveSongDurationMs(song(totalSeconds = 231.4548), knownDurationMs = 999_000))
    }

    @Test
    fun resolvesDurationFromSecondString() {
        assertEquals(231_454L, resolveSongDurationMs(song(duration = "231.4548")))
    }

    @Test
    fun resolvesDurationFromMinuteString() {
        assertEquals(231_000L, resolveSongDurationMs(song(duration = "3:51")))
        assertEquals(3_600_000L + 3_000L + 60_000L, resolveSongDurationMs(song(duration = "1:01:03")))
    }

    @Test
    fun fallsBackToPlayerDuration() {
        assertEquals(188_000L, resolveSongDurationMs(song(duration = "unknown"), knownDurationMs = 188_000))
        assertEquals(0L, resolveSongDurationMs(song()))
    }

    @Test
    fun formatsDurationAndDelta() {
        assertEquals("4:30", formatDurationMs(270_738))
        assertEquals("--:--", formatDurationMs(0))
        assertEquals("1:01:03", formatDurationMs(3_663_000))
        assertEquals("3s", formatDurationDelta(2_600))
        assertEquals("1:05", formatDurationDelta(65_000))
    }
}
