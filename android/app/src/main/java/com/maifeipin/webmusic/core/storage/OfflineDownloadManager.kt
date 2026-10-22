package com.maifeipin.webmusic.core.storage

import android.content.Context
import android.os.Environment
import android.util.Log
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.TokenManager
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.concurrent.CancellationException
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.coroutineContext
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

object OfflineDownloadManager {
    private const val TAG = "OfflineDownloadManager"

    // 内存中的下载任务进度字典 (taskKey -> Float 0..1)
    val activeDownloads = ConcurrentHashMap<String, Float>()
    private val activeCalls = ConcurrentHashMap<String, Call>()
    private val activeJobs = ConcurrentHashMap<String, Job>()

    fun makeTaskKey(serverHash: String, userHash: String, songId: Int): String {
        return "${serverHash}_${userHash}_$songId"
    }

    suspend fun downloadSong(
        context: Context,
        tokenManager: TokenManager,
        song: MediaFileDto,
        onProgress: (Float) -> Unit = {}
    ): Result<DownloadedSong> = withContext(Dispatchers.IO) {
        // 1. 拍摄当前会话的不变凭据快照，彻底解耦可变的 TokenManager
        val snapshotServerUrl = tokenManager.serverUrl.trimEnd('/')
        val snapshotUsername = tokenManager.username?.ifEmpty { "anon" } ?: "anon"
        val snapshotToken = tokenManager.token

        if (snapshotToken.isNullOrEmpty()) {
            return@withContext Result.failure(IllegalStateException("未登录，无法下载"))
        }

        val serverHash = LocalSongDatabase.computeServerHash(snapshotServerUrl)
        val userHash = LocalSongDatabase.computeUserHash(snapshotUsername)
        val taskKey = makeTaskKey(serverHash, userHash, song.id)

        // 2. 强隔离安全路径校验：使用用户名哈希构造子目录，并校验规范化路径在根目录内
        val musicDir = context.getExternalFilesDir(Environment.DIRECTORY_MUSIC)
            ?: context.filesDir
        val baseDownloadsDir = File(musicDir, "downloads").canonicalFile
        val serverDir = File(baseDownloadsDir, serverHash).canonicalFile
        val userDir = File(serverDir, userHash).canonicalFile

        if (!userDir.canonicalPath.startsWith(baseDownloadsDir.canonicalPath)) {
            return@withContext Result.failure(SecurityException("非法存储路径，已阻止跨目录访问"))
        }
        if (!userDir.exists()) userDir.mkdirs()

        // 3. 独立版本临时文件与提交目标文件
        val tempFile = File(userDir, "wm_${song.id}_${System.currentTimeMillis()}.tmp")
        var committedFile: File? = null
        var isCommittedToDatabase = false

        val currentJob = coroutineContext[Job]
        var cancellationHandle: kotlinx.coroutines.DisposableHandle? = null

        try {
            activeDownloads[taskKey] = 0f
            onProgress(0f)

            val streamUrl = "$snapshotServerUrl/api/media/stream/${song.id}"
            // 使用凭据快照创建独立的网络客户端，防止后续切账号时混用
            val client = WebMusicNetwork.createClientWithToken(snapshotToken)
            val request = Request.Builder()
                .url(streamUrl)
                .addHeader("Authorization", "Bearer $snapshotToken")
                .build()

            val call = client.newCall(request)
            activeCalls[taskKey] = call
            if (currentJob != null) {
                activeJobs[taskKey] = currentJob
                cancellationHandle = currentJob.invokeOnCompletion { cause ->
                    if (cause is CancellationException) {
                        call.cancel()
                    }
                }
            }

            var totalRead = 0L
            var expectedBytes = song.sizeBytes ?: -1L

            // 采用 suspendCancellableCoroutine + enqueue，直接将协程取消绑定到 Call
            // 并使用 resume(response) { cause -> response.close() } 处理取消竞态下未交付 Response 的资源释放
            val response = suspendCancellableCoroutine<Response> { continuation ->
                continuation.invokeOnCancellation {
                    call.cancel()
                }
                call.enqueue(object : Callback {
                    override fun onResponse(call: Call, response: Response) {
                        continuation.resume(response) { cause ->
                            response.close()
                        }
                    }

                    override fun onFailure(call: Call, e: IOException) {
                        if (continuation.isActive) {
                            continuation.resumeWithException(e)
                        }
                    }
                })
            }

            response.use { resp ->
                if (!resp.isSuccessful) {
                    return@withContext Result.failure(IllegalStateException("下载失败: HTTP ${resp.code}"))
                }

                val body = resp.body
                    ?: return@withContext Result.failure(IllegalStateException("响应体为空"))

                val contentLength = body.contentLength()
                if (contentLength > 0) {
                    expectedBytes = contentLength
                }

                // 取消绑定覆盖整个响应体复制过程：使用 suspendCancellableCoroutine 确保在 input.read() 阻塞期间，
                // 一旦协程取消（如 Activity 销毁）能立即通过 continuation.invokeOnCancellation 触发 call.cancel() 关闭底层 Socket
                totalRead = copyStreamWithCancellation(
                    call = call,
                    body = body,
                    targetFile = tempFile,
                    expectedBytes = expectedBytes,
                    taskKey = taskKey,
                    onProgress = onProgress
                )
            }

            // 完整性校验：必须有读取数据，且若服务端提供了 ContentLength 则必须全量匹配
            if (totalRead <= 0L) {
                return@withContext Result.failure(IllegalStateException("下载内容为空"))
            }
            if (expectedBytes > 0 && totalRead != expectedBytes) {
                return@withContext Result.failure(IllegalStateException("文件校验失败: 预期 $expectedBytes 字节，实际下载 $totalRead 字节"))
            }

            // 提交文件前严格校验协程取消状态
            if (!coroutineContext.isActive) {
                throw CancellationException("下载任务已在提交前取消")
            }

            // 4. 原子提交新文件：重命名到独立的版本文件，绝不先删旧文件
            val targetVersionFile = File(userDir, "wm_${song.id}_${System.currentTimeMillis()}.audio")
            val renameSuccess = tempFile.renameTo(targetVersionFile)
            if (!renameSuccess || !targetVersionFile.exists() || targetVersionFile.length() != totalRead) {
                if (targetVersionFile.exists()) targetVersionFile.delete()
                return@withContext Result.failure(IllegalStateException("提交离线目标文件失败"))
            }
            committedFile = targetVersionFile

            // 5. 使用快照拉取 LRC 歌词（若失败不阻断音频保存，遇到取消则重抛）
            var lyricsContent: String? = null
            try {
                val api = WebMusicNetwork.createApi(snapshotServerUrl, snapshotToken)
                val lyricsResp = api.getLyrics(song.id)
                lyricsContent = lyricsResp.content
            } catch (e: Exception) {
                if (e is CancellationException) throw e
                Log.w(TAG, "Lyrics not found for song ${song.id}, proceed without lyrics")
            }

            // 入库前再次核验取消状态
            if (!coroutineContext.isActive) {
                throw CancellationException("下载任务在入库前已被取消")
            }

            // 6. 查找旧记录（以备数据库更新成功后清理旧文件）
            val oldRecord = LocalSongDatabase.getInstance(context).getById(serverHash, userHash, song.id)
            val oldFilePath = oldRecord?.localFilePath

            val downloadedSong = DownloadedSong(
                serverHash = serverHash,
                userHash = userHash,
                username = snapshotUsername,
                id = song.id,
                title = song.title ?: "未知歌曲",
                artist = song.artist ?: "未知歌手",
                album = song.album ?: "未知专辑",
                genre = song.genre,
                duration = song.duration,
                sizeBytes = targetVersionFile.length(),
                localFilePath = targetVersionFile.absolutePath,
                lyrics = lyricsContent,
                coverUrl = WebMusicNetwork.buildCoverUrl(snapshotServerUrl, song.filePath),
                downloadedAt = System.currentTimeMillis()
            )

            // 7. 持久化写入本地离线数据库，若写入失败则抛出异常并触发安全清理
            LocalSongDatabase.getInstance(context).insertOrUpdate(downloadedSong)
            isCommittedToDatabase = true

            // 8. 仅在数据库更新完全成功后，安全清理历史版本旧文件
            if (oldFilePath != null && oldFilePath != targetVersionFile.absolutePath) {
                try {
                    val oldFile = File(oldFilePath)
                    if (oldFile.exists()) oldFile.delete()
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to clean old file: $oldFilePath", e)
                }
            }

            onProgress(1f)
            Result.success(downloadedSong)
        } catch (e: Throwable) {
            if (e is CancellationException) {
                // 必须重新抛出 CancellationException，绝不吞掉协程取消
                throw e
            }
            Log.e(TAG, "Download song ${song.id} failed", e)
            Result.failure(e as? Exception ?: Exception(e))
        } finally {
            // 9. 统一收尾：若未成功提交入库，坚决清理新创建的中间文件，彻底防止孤儿文件泄露
            if (!isCommittedToDatabase && committedFile != null && committedFile.exists()) {
                committedFile.delete()
            }
            if (tempFile.exists()) {
                tempFile.delete()
            }
            cancellationHandle?.dispose()
            activeCalls.remove(taskKey)
            activeJobs.remove(taskKey)
            activeDownloads.remove(taskKey)
        }
    }

    /**
     * 将网络响应体流式复制到目标临时文件
     * 纳入结构化并发 (coroutineScope)：
     * 1. 启动取消监听子协程，单一注册 continuation.invokeOnCancellation 绑定底层 call.cancel()；
     * 2. 在当前协程中直接执行阻塞流式复制，一旦取消立即中断底层 Socket 并经由 .use 关闭输入输出流；
     * 3. 严格保证输入输出流关闭后才退出作用域，彻底避免清理临时文件时的文件占用竞态。
     */
    private suspend fun copyStreamWithCancellation(
        call: Call,
        body: ResponseBody,
        targetFile: File,
        expectedBytes: Long,
        taskKey: String,
        onProgress: (Float) -> Unit
    ): Long = coroutineScope {
        var isFinished = false
        val cancelWatcher = launch {
            suspendCancellableCoroutine<Unit> { continuation ->
                continuation.invokeOnCancellation {
                    if (!isFinished) {
                        call.cancel()
                    }
                }
            }
        }

        try {
            var total = 0L
            body.byteStream().use { input ->
                FileOutputStream(targetFile).use { output ->
                    val buffer = ByteArray(32 * 1024)
                    var read: Int

                    while (input.read(buffer).also { read = it } != -1) {
                        if (!isActive) {
                            throw CancellationException("下载任务已在传输中取消")
                        }
                        output.write(buffer, 0, read)
                        total += read

                        if (expectedBytes > 0) {
                            val progress = (total.toFloat() / expectedBytes.toFloat()).coerceIn(0f, 1f)
                            activeDownloads[taskKey] = progress
                            onProgress(progress)
                        }
                    }
                    output.flush()
                }
            }
            isFinished = true
            total
        } finally {
            cancelWatcher.cancel()
        }
    }

    fun getDownloadProgress(serverUrl: String, username: String?, songId: Int): Float? {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val userHash = LocalSongDatabase.computeUserHash(username)
        return activeDownloads[makeTaskKey(serverHash, userHash, songId)]
    }

    fun deleteDownload(context: Context, serverUrl: String, username: String?, songId: Int): Boolean {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val userHash = LocalSongDatabase.computeUserHash(username)
        val db = LocalSongDatabase.getInstance(context)
        val song = db.getById(serverHash, userHash, songId)
        if (song != null) {
            val file = File(song.localFilePath)
            if (file.exists()) file.delete()
            return db.delete(serverHash, userHash, songId)
        }
        return false
    }

    fun isDownloaded(context: Context, serverUrl: String, username: String?, songId: Int): Boolean {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val userHash = LocalSongDatabase.computeUserHash(username)
        return LocalSongDatabase.getInstance(context).isDownloaded(serverHash, userHash, songId)
    }

    fun getDownloadedSong(context: Context, serverUrl: String, username: String?, songId: Int): DownloadedSong? {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val userHash = LocalSongDatabase.computeUserHash(username)
        return LocalSongDatabase.getInstance(context).getById(serverHash, userHash, songId)
    }

    fun getAllDownloaded(context: Context, serverUrl: String, username: String?): List<DownloadedSong> {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val userHash = LocalSongDatabase.computeUserHash(username)
        return LocalSongDatabase.getInstance(context).getAll(serverHash, userHash)
    }

    fun cancelDownload(serverUrl: String, username: String?, songId: Int) {
        val serverHash = LocalSongDatabase.computeServerHash(serverUrl)
        val userHash = LocalSongDatabase.computeUserHash(username)
        val key = makeTaskKey(serverHash, userHash, songId)
        activeCalls.remove(key)?.cancel()
        activeJobs.remove(key)?.cancel()
        activeDownloads.remove(key)
    }

    fun cancelAllDownloads() {
        activeCalls.values.forEach { it.cancel() }
        activeCalls.clear()
        activeJobs.values.forEach { it.cancel() }
        activeJobs.clear()
        activeDownloads.clear()
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
        fileHash = "offline_${serverHash}_${userHash}_$id",
        contentVersion = "offline"
    )
}
