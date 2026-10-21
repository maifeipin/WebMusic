package com.maifeipin.webmusic.core.storage

import android.content.Context
import android.os.Environment
import android.util.Log
import com.maifeipin.webmusic.core.auth.TokenManager
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Request
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.ConcurrentHashMap

object OfflineDownloadManager {
    private const val TAG = "OfflineDownloadManager"

    // 内存中的下载进度状态字典 (songId -> Float 0..1)
    val activeDownloads = ConcurrentHashMap<Int, Float>()

    suspend fun downloadSong(
        context: Context,
        tokenManager: TokenManager,
        song: MediaFileDto,
        onProgress: (Float) -> Unit = {}
    ): Result<DownloadedSong> = withContext(Dispatchers.IO) {
        val serverUrl = tokenManager.serverUrl.trimEnd('/')
        val token = tokenManager.token
        if (token.isNullOrEmpty()) {
            return@withContext Result.failure(IllegalStateException("未登录，无法下载"))
        }

        val musicDir = context.getExternalFilesDir(Environment.DIRECTORY_MUSIC)
            ?: context.filesDir
        if (!musicDir.exists()) musicDir.mkdirs()

        val targetFile = File(musicDir, "webmusic_${song.id}.audio")
        val tempFile = File(musicDir, "webmusic_${song.id}.tmp")

        try {
            activeDownloads[song.id] = 0f
            onProgress(0f)

            // 1. 流式下载音频主体并计算写入进度
            val streamUrl = "$serverUrl/api/media/stream/${song.id}"
            val client = WebMusicNetwork.getUnsafeOkHttpClient()
            val request = Request.Builder()
                .url(streamUrl)
                .addHeader("Authorization", "Bearer $token")
                .build()

            val response = client.newCall(request).execute()
            if (!response.isSuccessful) {
                return@withContext Result.failure(IllegalStateException("下载失败: HTTP ${response.code}"))
            }

            val body = response.body ?: return@withContext Result.failure(IllegalStateException("空响应体"))
            val totalBytes = body.contentLength().let { if (it > 0) it else song.sizeBytes ?: 1L }

            body.byteStream().use { input ->
                FileOutputStream(tempFile).use { output ->
                    val buffer = ByteArray(32 * 1024)
                    var read: Int
                    var totalRead = 0L

                    while (input.read(buffer).also { read = it } != -1) {
                        output.write(buffer, 0, read)
                        totalRead += read
                        val progress = (totalRead.toFloat() / totalBytes.toFloat()).coerceIn(0f, 1f)
                        activeDownloads[song.id] = progress
                        onProgress(progress)
                    }
                    output.flush()
                }
            }

            if (tempFile.exists()) {
                if (targetFile.exists()) targetFile.delete()
                tempFile.renameTo(targetFile)
            }

            // 2. 尝试拉取对应的 LRC 歌词并缓存入库
            var lyricsContent: String? = null
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val lyricsResp = api.getLyrics(song.id)
                lyricsContent = lyricsResp.content
            } catch (e: Exception) {
                Log.w(TAG, "Lyrics not found for song ${song.id}, proceed without lyrics")
            }

            // 3. 固化写入本地数据库
            val downloadedSong = DownloadedSong(
                id = song.id,
                title = song.title ?: "未知歌曲",
                artist = song.artist ?: "未知歌手",
                album = song.album ?: "未知专辑",
                genre = song.genre,
                duration = song.duration,
                sizeBytes = targetFile.length(),
                localFilePath = targetFile.absolutePath,
                lyrics = lyricsContent,
                coverUrl = song.filePath?.let { WebMusicNetwork.getCoverUrl(serverUrl, it) },
                downloadedAt = System.currentTimeMillis()
            )

            LocalSongDatabase.getInstance(context).insertOrUpdate(downloadedSong)
            activeDownloads.remove(song.id)
            onProgress(1f)

            Result.success(downloadedSong)
        } catch (e: Exception) {
            Log.e(TAG, "Download song ${song.id} failed", e)
            activeDownloads.remove(song.id)
            if (tempFile.exists()) tempFile.delete()
            Result.failure(e)
        }
    }

    fun deleteDownload(context: Context, songId: Int): Boolean {
        val db = LocalSongDatabase.getInstance(context)
        val song = db.getById(songId)
        if (song != null) {
            val file = File(song.localFilePath)
            if (file.exists()) file.delete()
            return db.delete(songId)
        }
        return false
    }

    fun isDownloaded(context: Context, songId: Int): Boolean {
        return LocalSongDatabase.getInstance(context).isDownloaded(songId)
    }

    fun getDownloadedSong(context: Context, songId: Int): DownloadedSong? {
        return LocalSongDatabase.getInstance(context).getById(songId)
    }

    fun getAllDownloaded(context: Context): List<DownloadedSong> {
        return LocalSongDatabase.getInstance(context).getAll()
    }
}

fun DownloadedSong.toMediaFileDto(): MediaFileDto {
    return MediaFileDto(
        id = id,
        filePath = localFilePath,
        title = title,
        artist = artist,
        album = album,
        genre = genre,
        year = null,
        duration = duration,
        sizeBytes = sizeBytes,
        fileHash = "offline_$id",
        contentVersion = "offline"
    )
}
