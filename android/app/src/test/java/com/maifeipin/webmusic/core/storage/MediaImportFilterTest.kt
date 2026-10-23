package com.maifeipin.webmusic.core.storage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MediaImportFilterTest {

    @Test
    fun recognizesSupportedAudioExtensions() {
        val audioFiles = listOf(
            "test.mp3",
            "track.FLAC",
            "audio.wav",
            "song.m4a",
            "sound.aac",
            "sample.ogg",
            "voice.opus",
            "classic.wma",
            "lossless.ape",
            "hires.dsf",
            "hires2.dff"
        )
        for (file in audioFiles) {
            assertTrue("Should support audio file: $file", MediaImportFilter.isSupportedMedia(file, null, 1024L))
        }
    }

    @Test
    fun recognizesSupportedVideoExtensions() {
        val videoFiles = listOf(
            "movie.mp4",
            "clip.MKV",
            "video.webm",
            "recording.avi",
            "film.mov",
            "stream.flv",
            "broadcast.ts",
            "short.m4v",
            "mobile.3gp"
        )
        for (file in videoFiles) {
            assertTrue("Should support video file: $file", MediaImportFilter.isSupportedMedia(file, null, 2048L))
        }
    }

    @Test
    fun rejectsHiddenAndNomediaFiles() {
        assertFalse(MediaImportFilter.isSupportedMedia(".nomedia", null, 100L))
        assertFalse(MediaImportFilter.isSupportedMedia(".hidden_track.mp3", null, 1024L))
        assertFalse(MediaImportFilter.isSupportedMedia(".DS_Store", null, 1024L))
    }

    @Test
    fun rejectsEmptyOrOversizedFiles() {
        assertFalse("Zero bytes must be rejected", MediaImportFilter.isSupportedMedia("empty.mp3", null, 0L))
        assertFalse("Negative size must be rejected", MediaImportFilter.isSupportedMedia("bad.mp3", null, -5L))
        assertFalse(
            "Over 2GB must be rejected",
            MediaImportFilter.isSupportedMedia("huge.mp4", null, MediaImportFilter.MAX_FILE_SIZE_BYTES + 1)
        )
        assertTrue(
            "Up to 2GB is allowed",
            MediaImportFilter.isSupportedMedia("huge.mp4", null, MediaImportFilter.MAX_FILE_SIZE_BYTES)
        )
    }

    @Test
    fun recognizesMediaByMimeTypeWhenExtensionMissing() {
        assertTrue(MediaImportFilter.isSupportedMedia("unknown_file", "audio/mpeg", 1024L))
        assertTrue(MediaImportFilter.isSupportedMedia("unknown_video", "video/mp4", 1024L))
    }

    @Test
    fun rejectsPlaylistsAndNonMediaMimeTypes() {
        assertFalse(MediaImportFilter.isSupportedMedia("list.m3u", "audio/x-mpegurl", 1024L))
        assertFalse(MediaImportFilter.isSupportedMedia("document.pdf", "application/pdf", 1024L))
        assertFalse(MediaImportFilter.isSupportedMedia("app.apk", "application/vnd.android.package-archive", 1024L))
    }

    @Test
    fun normalizesUriKeyCorrectly() {
        // Document URI with authority and document ID
        val key1 = MediaImportFilter.normalizeUriKey(
            authority = "com.android.providers.media.documents",
            documentId = "audio:12345",
            fallbackUriOrPath = "content://com.android.providers.media.documents/tree/downloads/document/audio%3A12345"
        )
        assertEquals("com.android.providers.media.documents:audio:12345", key1)

        // Raw file URI
        val key2 = MediaImportFilter.normalizeUriKey(
            authority = null,
            documentId = null,
            fallbackUriOrPath = "file:///storage/emulated/0/Music/song.flac"
        )
        assertEquals("/storage/emulated/0/Music/song.flac", key2)

        // Plain filesystem path
        val key3 = MediaImportFilter.normalizeUriKey(
            authority = "",
            documentId = "",
            fallbackUriOrPath = "/sdcard/Music/song.flac"
        )
        assertEquals("/sdcard/Music/song.flac", key3)
    }

    @Test
    fun formatsSummaryCorrectly() {
        val s1 = MediaImportFilter.formatImportSummary(added = 10, skippedDuplicate = 0, unsupported = 0, failed = 0)
        assertEquals("导入完成：新增 10 首", s1)

        val s2 = MediaImportFilter.formatImportSummary(added = 5, skippedDuplicate = 3, unsupported = 2, failed = 1)
        assertEquals("导入完成：新增 5 首 · 已存在 3 首 · 跳过不支持 2 个 · 失败 1 个", s2)
    }
}
