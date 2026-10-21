package com.maifeipin.webmusic.core.storage

import android.content.Context
import android.os.Environment
import android.util.Log
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.TokenManager
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import okhttp3.Request
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.CancellationException
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.coroutineContext

object OfflineDownloadManager {
    private const val TAG = "OfflineDownloadManager"

    // 内存中的下载任务进度字典 (taskKey -> Float 0..1)
    val activeDownloads = ConcurrentHashMap<String, Float>()

    fun makeTaskKey(serverHash: String, username: String, songId: Int): String {
        return "${serverHash}_${username}_$songId"
    }

    suspend fun downloadSong(
        context: Context,
        tokenManager: TokenManager,
        song: MediaFileDto,
        onProgress: (Float) -> Unit = {}
    ): Result<DownloadedSong> = withContext(Dispatchers.IO) {
        val serverUrl = tokenManager.serverUrl.trimEnd('/')
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val safeUser = tokenManager.username?.ifEmpty { "anon" } ?: "anon"
        val token = tokenManager.token

        if (token.isNullOrEmpty()) {
            return@withContext Result.failure(IllegalStateException("未登录，无法下载"))
        }

        val taskKey = makeTaskKey(serverHash, safeUser, song.id)

        // 强隔离存储目录：downloads/<ServerHash>/<Username>/
        val musicDir = context.getExternalFilesDir(Environment.DIRECTORY_MUSIC)
            ?: context.filesDir
        val userDir = File(musicDir, "downloads/$serverHash/$safeUser")
        if (!userDir.exists()) userDir.mkdirs()

        val targetFile = File(userDir, "wm_${song.id}.audio")
        val tempFile = File(userDir, "wm_${song.id}_${System.currentTimeMillis()}.tmp")

        try {
            activeDownloads[taskKey] = 0f
            onProgress(0f)

            val streamUrl = "$serverUrl/api/media/stream/${song.id}"
            val client = WebMusicNetwork.createClient(tokenManager)
            val request = Request.Builder()
                .url(streamUrl)
                .addHeader("Authorization", "Bearer $token")
                .build()

            var totalRead = 0L
            var expectedBytes = song.sizeBytes ?: -1L

            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    return@withContext Result.failure(IllegalStateException("下载失败: HTTP ${response.code}"))
                }

                val body = response.body
                    ?: return@withContext Result.failure(IllegalStateException("响应体为空"))

                val contentLength = body.contentLength()
                if (contentLength > 0) {
                    expectedBytes = contentLength
                }

                body.byteStream().use { input ->
                    FileOutputStream(tempFile).use { output ->
                        val buffer = ByteArray(32 * 1024)
                        var read: Int

                        while (input.read(buffer).also { read = it } != -1) {
                            if (!coroutineContext.isActive) {
                                throw CancellationException("下载任务已被取消")
                            }
                            output.write(buffer, 0, read)
                            totalRead += read

                            if (expectedBytes > 0) {
                                val progress = (totalRead.toFloat() / expectedBytes.toFloat()).coerceIn(0f, 1f)
                                activeDownloads[taskKey] = progress
                                onProgress(progress)
                            }
                        }
                        output.flush()
                    }
                }
            }

            // 完整性校验：必须有读取数据，且若服务端提供了 ContentLength 则必须全量匹配
            if (totalRead <= 0L) {
                if (tempFile.exists()) tempFile.delete()
                return@withContext Result.failure(IllegalStateException("下载内容为空"))
            }
            if (expectedBytes > 0 && totalRead != expectedBytes) {
                if (tempFile.exists()) tempFile.delete()
                return@withContext Result.failure(IllegalStateException("文件校验失败: 预期 $expectedBytes 字节，实际下载 $totalRead 字节"))
            }

            // 原子替换文件：先清理目标再重命名，并严格检验重命名返回值
            if (targetFile.exists()) {
                targetFile.delete()
            }
            val renameSuccess = tempFile.renameTo(targetFile)
            if (!renameSuccess || !targetFile.exists() || targetFile.length() != totalRead) {
                if (tempFile.exists()) tempFile.delete()
                return@withContext Result.failure(IllegalStateException("提交离线目标文件失败"))
            }

            // 尝试获取该曲目的 LRC 歌词
            var lyricsContent: String? = null
            try {
                val api = WebMusicNetwork.createApi(tokenManager)
                val lyricsResp = api.getLyrics(song.id)
                lyricsContent = lyricsResp.content
            } catch (e: Exception) {
                Log.w(TAG, "Lyrics not found for song ${song.id}, proceed without lyrics")
            }

            val downloadedSong = DownloadedSong(
                serverHash = serverHash,
                username = safeUser,
                id = song.id,
                title = song.title ?: "未知歌曲",
                artist = song.artist ?: "未知歌手",
                album = song.album ?: "未知专辑",
                genre = song.genre,
                duration = song.duration,
                sizeBytes = targetFile.length(),
                localFilePath = targetFile.absolutePath,
                lyrics = lyricsContent,
                coverUrl = WebMusicNetwork.buildCoverUrl(serverUrl, song.filePath),
                downloadedAt = System.currentTimeMillis()
            )

            // 仅在物理文件原子提交成功后，才持久化写入本地离线数据库
            LocalSongDatabase.getInstance(context).insertOrUpdate(downloadedSong)
            onProgress(1f)
            Result.success(downloadedSong)
        } catch (e: Exception) {
            Log.e(TAG, "Download song ${song.id} failed", e)
            if (tempFile.exists()) tempFile.delete()
            Result.failure(e)
        } finally {
            // 无论成功、失败或取消，一律在 finally 中释放活跃任务状态
            activeDownloads.remove(taskKey)
        }
    }

    fun getDownloadProgress(serverUrl: String, username: String?, songId: Int): Float? {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val safeUser = username?.ifEmpty { "anon" } ?: "anon"
        return activeDownloads[makeTaskKey(serverHash, safeUser, songId)]
    }

    fun deleteDownload(context: Context, serverUrl: String, username: String?, songId: Int): Boolean {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val safeUser = username?.ifEmpty { "anon" } ?: "anon"
        val db = LocalSongDatabase.getInstance(context)
        val song = db.getById(serverHash, safeUser, songId)
        if (song != null) {
            val file = File(song.localFilePath)
            if (file.exists()) file.delete()
            return db.delete(serverHash, safeUser, songId)
        }
        return false
    }

    fun isDownloaded(context: Context, serverUrl: String, username: String?, songId: Int): Boolean {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val safeUser = username?.ifEmpty { "anon" } ?: "anon"
        return LocalSongDatabase.getInstance(context).isDownloaded(serverHash, safeUser, songId)
    }

    fun getDownloadedSong(context: Context, serverUrl: String, username: String?, songId: Int): DownloadedSong? {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val safeUser = username?.ifEmpty { "anon" } ?: "anon"
        return LocalSongDatabase.getInstance(context).getById(serverHash, safeUser, songId)
    }

    fun getAllDownloaded(context: Context, serverUrl: String, username: String?): List<DownloadedSong> {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val safeUser = username?.ifEmpty { "anon" } ?: "anon"
        return LocalSongDatabase.getInstance(context).getAll(serverHash, safeUser)
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
        fileHash = "offline_${serverHash}_${username}_$id",
        contentVersion = "offline"
    )
}
