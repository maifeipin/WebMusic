package com.maifeipin.webmusic.core.storage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LocalMediaIdTest {

    @Test
    fun verifyDeviceLocalScopeDeterministicHash() {
        val hash1 = LocalSongDatabase.computeServerHash("__device_local__")
        val hash2 = LocalSongDatabase.computeUserHash("__device_local__")

        assertEquals(LocalSongDatabase.DEVICE_LOCAL_SCOPE, hash1)
        assertEquals(LocalSongDatabase.DEVICE_LOCAL_SCOPE, hash2)
        assertEquals(64, hash1.length)
        assertTrue(hash1.matches(Regex("^[0-9a-f]{64}$")))
    }

    @Test
    fun downloadedSongIsVideoDetection() {
        val audioSong = DownloadedSong(
            serverHash = LocalSongDatabase.DEVICE_LOCAL_SCOPE,
            userHash = LocalSongDatabase.DEVICE_LOCAL_SCOPE,
            username = "device",
            id = -1,
            title = "Test Audio",
            artist = "Artist",
            album = "Album",
            genre = null,
            duration = "03:45",
            sizeBytes = 5000000L,
            localFilePath = "content://media/external/audio/media/100",
            lyrics = null,
            coverUrl = null,
            downloadedAt = System.currentTimeMillis(),
            source = DownloadedSong.SOURCE_IMPORT,
            mimeType = "audio/flac"
        )
        assertFalse("Audio flac should not be marked as video", audioSong.isVideo())

        val videoByMime = audioSong.copy(
            id = -2,
            mimeType = "video/mp4",
            localFilePath = "content://media/external/video/media/200"
        )
        assertTrue("video/mp4 MIME should be marked as video", videoByMime.isVideo())

        val videoByExtension = audioSong.copy(
            id = -3,
            mimeType = null,
            localFilePath = "/storage/emulated/0/Movies/live_performance.mkv"
        )
        assertTrue(".mkv file path should be marked as video", videoByExtension.isVideo())
    }

    @Test
    fun monotonicIdSequenceSimulatedSafety() {
        // 模拟 local_id_seq 的分配逻辑与防溢出检查
        var currentSeq = -1
        fun allocateId(): Int {
            if (currentSeq <= Int.MIN_VALUE) {
                throw IllegalStateException("Local ID sequence exhausted")
            }
            val allocated = currentSeq
            currentSeq -= 1
            return allocated
        }

        assertEquals(-1, allocateId())
        assertEquals(-2, allocateId())
        assertEquals(-3, allocateId())
        assertEquals(-4, allocateId())

        // 验证 Int.MIN_VALUE 溢出保护
        currentSeq = Int.MIN_VALUE
        try {
            allocateId()
            org.junit.Assert.fail("Must throw when sequence exhausted")
        } catch (e: IllegalStateException) {
            assertEquals("Local ID sequence exhausted", e.message)
        }
    }
}
