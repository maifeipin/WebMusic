package com.maifeipin.webmusic.core.storage

import android.net.Uri
import java.io.File

object MediaImportFilter {

    const val MAX_FILE_SIZE_BYTES: Long = 2L * 1024 * 1024 * 1024 // 2GB

    val AUDIO_EXTENSIONS: Set<String> = setOf(
        "mp3", "flac", "wav", "m4a", "aac", "ogg", "opus", "wma", "ape", "dsf", "dff"
    )

    val VIDEO_EXTENSIONS: Set<String> = setOf(
        "mp4", "mkv", "webm", "avi", "mov", "flv", "ts", "m4v", "3gp"
    )

    /**
     * 判断文件是否属于可导入的音频或视频文件
     */
    fun isSupportedMedia(displayName: String?, mimeType: String?, sizeBytes: Long): Boolean {
        if (displayName.isNullOrBlank()) return false
        if (displayName.startsWith(".")) return false // 隐藏文件
        if (displayName.equals(".nomedia", ignoreCase = true)) return false

        // 超过大小上限或空文件
        if (sizeBytes <= 0 || sizeBytes > MAX_FILE_SIZE_BYTES) return false

        val ext = displayName.substringAfterLast('.', "").lowercase()
        // 排除歌单与清单文件
        if (ext in setOf("m3u", "m3u8", "pls", "wpl", "xspf")) {
            return false
        }
        if (ext.isNotEmpty() && (ext in AUDIO_EXTENSIONS || ext in VIDEO_EXTENSIONS)) {
            return true
        }

        if (mimeType != null) {
            val lowerMime = mimeType.lowercase()
            if (lowerMime.startsWith("audio/") || lowerMime.startsWith("video/")) {
                // 排除一些非媒体流数据类（如歌单、描述文件）
                if (!lowerMime.contains("playlist") && !lowerMime.contains("m3u") && !lowerMime.contains("mpegurl")) {
                    return true
                }
            }
        }

        return false
    }

    /**
     * 规范化 SAF 文档唯一身份。
     * 对于 SAF 文档，使用 authority:documentId 规避因父/子目录授权不同产生的 URI 差异。
     */
    fun normalizeUriKey(authority: String?, documentId: String?, fallbackUriOrPath: String): String {
        if (!authority.isNullOrBlank() && !documentId.isNullOrBlank()) {
            return "$authority:$documentId"
        }
        val trimmed = fallbackUriOrPath.trim()
        return if (trimmed.startsWith("file://")) {
            trimmed.removePrefix("file://")
        } else {
            trimmed
        }
    }

    /**
     * 转换存储路径/URI 为 ExoPlayer 可播放的 Uri 对象
     */
    fun toPlayableUri(filePath: String): Uri {
        val trimmed = filePath.trim()
        return when {
            trimmed.startsWith("content://") || trimmed.startsWith("file://") -> Uri.parse(trimmed)
            trimmed.startsWith("/") -> Uri.fromFile(File(trimmed))
            else -> Uri.parse(trimmed)
        }
    }

    /**
     * 格式化导入结果汇总提示
     */
    fun formatImportSummary(added: Int, skippedDuplicate: Int, unsupported: Int, failed: Int): String {
        val parts = mutableListOf<String>()
        parts.add("新增 $added 首")
        if (skippedDuplicate > 0) {
            parts.add("已存在 $skippedDuplicate 首")
        }
        if (unsupported > 0) {
            parts.add("跳过不支持 $unsupported 个")
        }
        if (failed > 0) {
            parts.add("失败 $failed 个")
        }
        return "导入完成：${parts.joinToString(" · ")}"
    }
}
