package com.maifeipin.webmusic.core.storage

import android.content.Context
import android.os.Environment
import android.util.Log
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.TokenManager
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.concurrent.CancellationException
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

object OfflineDownloadManager {
    private const val TAG = "OfflineDownloadManager"

    /** 网络抖动（服务端 SMB 读超时返回 500、连接被重置）时的最大尝试次数 */
    private const val MAX_ATTEMPTS = 3

    /** 每次重试前的等待，指数退避 */
    private val RETRY_BACKOFF_MS = longArrayOf(1_000L, 2_000L)

    /**
     * 传输归属：任务跑在管理器自己的 scope 里，而不是调用方（Compose `rememberCoroutineScope`）里。
     * 这样「自动缓存」先起的传输不会因为界面重组/退出页面而中断；
     * 真正需要停的时候由 [cancelAllDownloads] / [cancelDownload] 显式撤销。
     */
    private val managerScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** 同一 taskKey 全局只允许一次传输，手动下载与自动缓存共用这一道闸门 */
    private val taskGate = DownloadTaskGate<Result<DownloadedSong>>(managerScope) { key ->
        // 自动缓存与手动下载撞车时会走到这里：不重复传输，只等同一份结果
        Log.i(TAG, "Reuse in-flight download task: $key")
    }

    // 内存中的下载任务进度字典 (taskKey -> Float 0..1)
    val activeDownloads = ConcurrentHashMap<String, Float>()
    private val activeCalls = ConcurrentHashMap<String, Call>()
    private val activeJobs = ConcurrentHashMap<String, Job>()

    // Compose 侧可观察的进度快照：0% 的进度环在深色背景上等于空白，
    // 必须让 UI 能确定「任务已经开始了」，否则看起来像点了没反应。
    private val _downloadStates = MutableStateFlow<Map<String, Float>>(emptyMap())
    val downloadStates: StateFlow<Map<String, Float>> = _downloadStates.asStateFlow()

    fun makeTaskKey(serverHash: String, userHash: String, songId: Int): String {
        return "${serverHash}_${userHash}_$songId"
    }

    /** 供 UI 直接取当前曲目的任务键，避免各处重复哈希计算 */
    fun taskKeyFor(serverUrl: String, username: String?, songId: Int): String {
        return makeTaskKey(
            LocalSongDatabase.computeServerHash(serverUrl),
            LocalSongDatabase.computeUserHash(username),
            songId
        )
    }

    private fun publishProgress(taskKey: String, progress: Float?) {
        _downloadStates.update { current ->
            if (progress == null) current - taskKey else current + (taskKey to progress)
        }
    }

    private class HttpStatusException(val code: Int, message: String) : IOException(message)

    private fun isRetryable(e: Throwable): Boolean = when (e) {
        is HttpStatusException -> e.code >= 500 || e.code == 408 || e.code == 429
        // SocketTimeout / 连接重置 / 传输中断都属于瞬时故障
        is IOException -> true
        else -> false
    }

    /** 一次下载会话的不变凭据快照，彻底解耦可变的 TokenManager */
    private data class DownloadSession(
        val serverUrl: String,
        val username: String,
        val token: String,
        val serverHash: String,
        val userHash: String
    )

    /**
     * 下载单曲到本地离线库。
     *
     * 幂等：同一（服务器 + 账号 + 曲目）重复调用不会发起第二次传输，后来的调用方直接复用
     * 在途任务的结果。服务端读 SMB 偶发 `STATUS_IO_TIMEOUT`（500）属于瞬时故障，最多
     * [MAX_ATTEMPTS] 次尝试（1s、2s 退避），每次都用全新的临时文件。协程取消永远原样抛出，不参与重试。
     */
    suspend fun downloadSong(
        context: Context,
        tokenManager: TokenManager,
        song: MediaFileDto,
        onProgress: (Float) -> Unit = {}
    ): Result<DownloadedSong> {
        val session = snapshotSession(tokenManager)
            ?: return Result.failure(IllegalStateException("未登录，无法下载"))

        val taskKey = makeTaskKey(session.serverHash, session.userHash, song.id)
        val appContext = context.applicationContext

        return try {
            taskGate.run(taskKey) {
                performDownload(appContext, session, song, taskKey, onProgress)
            }
        } catch (e: CancellationException) {
            // 调用方自己被取消（退出登录 / 页面销毁），保持原语义
            throw e
        } catch (e: Throwable) {
            Result.failure(e as? Exception ?: Exception(e))
        }
    }

    private fun snapshotSession(tokenManager: TokenManager): DownloadSession? {
        val serverUrl = tokenManager.serverUrl.trimEnd('/')
        val token = tokenManager.token
        if (token.isNullOrEmpty() || serverUrl.isBlank()) return null
        val username = tokenManager.username?.ifEmpty { "anon" } ?: "anon"
        return DownloadSession(
            serverUrl = serverUrl,
            username = username,
            token = token,
            serverHash = LocalSongDatabase.computeServerHash(serverUrl),
            userHash = LocalSongDatabase.computeUserHash(username)
        )
    }

    /**
     * 任务级框架：路径校验只做一次，随后交给 [retryOfflineDownload] 跑尝试/退避。
     * `onFinished` 挂在最外层 finally，保证「退避等待中被取消」也会清空进度与在途登记。
     */
    private suspend fun performDownload(
        context: Context,
        session: DownloadSession,
        song: MediaFileDto,
        taskKey: String,
        onProgress: (Float) -> Unit
    ): Result<DownloadedSong> {
        // 强隔离安全路径校验：使用用户名哈希构造子目录，并校验规范化路径在根目录内
        val musicDir = context.getExternalFilesDir(Environment.DIRECTORY_MUSIC) ?: context.filesDir
        val baseDownloadsDir = File(musicDir, "downloads").canonicalFile
        val serverDir = File(baseDownloadsDir, session.serverHash).canonicalFile
        val userDir = File(serverDir, session.userHash).canonicalFile

        if (!userDir.canonicalPath.startsWith(baseDownloadsDir.canonicalPath)) {
            return Result.failure(SecurityException("非法存储路径，已阻止跨目录访问"))
        }
        if (!userDir.exists()) userDir.mkdirs()

        val currentJob = coroutineContext[Job]
        if (currentJob != null) activeJobs[taskKey] = currentJob

        val downloaded = try {
            retryOfflineDownload(
                maxAttempts = MAX_ATTEMPTS,
                backoffs = RETRY_BACKOFF_MS,
                isRetryable = ::isRetryable,
                onFinished = {
                    // 只摘自己这一笔登记，避免误删后来者。闸门保证同一 key 不并存，
                    // 但 cancelAllDownloads() 会先清空 activeJobs：若旧任务收尾前同 key 新任务已
                    // 接管登记，这里不能再按 key 无条件清进度，否则旧任务会把新任务打成 0%。
                    val ownsSlot = currentJob == null || activeJobs[taskKey] === currentJob
                    if (currentJob != null) activeJobs.remove(taskKey, currentJob)
                    if (ownsSlot) {
                        activeDownloads.remove(taskKey)
                        publishProgress(taskKey, null)
                    }
                }
            ) { attempt ->
                performAttempt(context, session, song, taskKey, userDir, attempt, onProgress)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Throwable) {
            return Result.failure(e as? Exception ?: Exception(e))
        }

        onProgress(1f)
        return Result.success(downloaded)
    }

    /**
     * 单次尝试。成功返回已入库的条目，失败直接抛异常交给外层决定是否重试。
     * 本次尝试产生的临时文件 / 目标文件 / 连接登记，全部在本方法的 finally 里就地收尾。
     */
    private suspend fun performAttempt(
        context: Context,
        session: DownloadSession,
        song: MediaFileDto,
        taskKey: String,
        userDir: File,
        attempt: Int,
        onProgress: (Float) -> Unit
    ): DownloadedSong {
        // 每次尝试都用独立的版本临时文件与提交目标文件
        val tempFile = File(userDir, "wm_${song.id}_${System.currentTimeMillis()}_$attempt.tmp")
        var committedFile: File? = null
        var isCommittedToDatabase = false
        var call: Call? = null
        var cancellationHandle: kotlinx.coroutines.DisposableHandle? = null

        try {
            activeDownloads[taskKey] = 0f
            publishProgress(taskKey, 0f)
            onProgress(0f)

            val streamUrl = "${session.serverUrl}/api/media/stream/${song.id}"
            // 使用凭据快照创建独立的网络客户端，防止后续切账号时混用
            val client = WebMusicNetwork.createClientWithToken(session.token)
            val request = Request.Builder()
                .url(streamUrl)
                .addHeader("Authorization", "Bearer ${session.token}")
                .build()

            val newCall = client.newCall(request)
            call = newCall
            activeCalls[taskKey] = newCall
            val currentJob = coroutineContext[Job]
            if (currentJob != null) {
                cancellationHandle = currentJob.invokeOnCompletion { cause ->
                    if (cause is CancellationException) {
                        newCall.cancel()
                    }
                }
            }

            var expectedBytes = song.sizeBytes ?: -1L

            // 采用 suspendCancellableCoroutine + enqueue，直接将协程取消绑定到 Call
            val response = suspendCancellableCoroutine<Response> { continuation ->
                continuation.invokeOnCancellation { newCall.cancel() }
                newCall.enqueue(object : Callback {
                    override fun onResponse(call: Call, response: Response) {
                        continuation.resume(response) { _ -> response.close() }
                    }

                    override fun onFailure(call: Call, e: IOException) {
                        if (continuation.isActive) {
                            continuation.resumeWithException(e)
                        }
                    }
                })
            }

            val readBytes = response.use { resp ->
                if (!resp.isSuccessful) {
                    // 5xx / 408 / 429 交给外层重试，4xx 直接失败
                    throw HttpStatusException(
                        resp.code,
                        if (resp.code >= 500) "服务端读取源文件失败（HTTP ${resp.code}）" else "下载失败：HTTP ${resp.code}"
                    )
                }

                val body = resp.body ?: throw IOException("响应体为空")
                val contentLength = body.contentLength()
                if (contentLength > 0) {
                    expectedBytes = contentLength
                }

                copyStreamWithCancellation(
                    call = newCall,
                    body = body,
                    targetFile = tempFile,
                    expectedBytes = expectedBytes,
                    taskKey = taskKey,
                    onProgress = onProgress
                )
            }
            val totalRead = readBytes

            // 完整性校验：必须有读取数据，且若服务端提供了 ContentLength 则必须全量匹配
            if (totalRead <= 0L) {
                throw IOException("下载内容为空")
            }
            if (expectedBytes > 0 && totalRead != expectedBytes) {
                throw IOException("文件校验失败: 预期 $expectedBytes 字节，实际下载 $totalRead 字节")
            }

            // 提交文件前严格校验协程取消状态
            if (!coroutineContext.isActive) {
                throw CancellationException("下载任务已在提交前取消")
            }

            // 原子提交新文件：重命名到独立的版本文件，绝不先删旧文件
            val targetVersionFile = File(userDir, "wm_${song.id}_${System.currentTimeMillis()}.audio")
            val renameSuccess = tempFile.renameTo(targetVersionFile)
            if (!renameSuccess || !targetVersionFile.exists() || targetVersionFile.length() != totalRead) {
                if (targetVersionFile.exists()) targetVersionFile.delete()
                throw IOException("提交离线目标文件失败")
            }
            committedFile = targetVersionFile

            // 使用快照拉取 LRC 歌词（若失败不阻断音频保存，遇到取消则重抛）
            var lyricsContent: String? = null
            try {
                val api = WebMusicNetwork.createApi(session.serverUrl, session.token)
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

            // 查找旧记录（以备数据库更新成功后清理旧文件）
            val oldRecord = LocalSongDatabase.getInstance(context)
                .getById(session.serverHash, session.userHash, song.id)
            val oldFilePath = oldRecord?.localFilePath

            val downloadedSong = DownloadedSong(
                serverHash = session.serverHash,
                userHash = session.userHash,
                username = session.username,
                id = song.id,
                title = song.title ?: "未知歌曲",
                artist = song.artist ?: "未知歌手",
                album = song.album ?: "未知专辑",
                genre = song.genre,
                duration = song.duration,
                sizeBytes = targetVersionFile.length(),
                localFilePath = targetVersionFile.absolutePath,
                lyrics = lyricsContent,
                coverUrl = WebMusicNetwork.buildCoverUrl(session.serverUrl, song.filePath),
                downloadedAt = System.currentTimeMillis()
            )

            // 持久化写入本地离线数据库，若写入失败则抛出异常并触发安全清理
            LocalSongDatabase.getInstance(context).insertOrUpdate(downloadedSong)
            isCommittedToDatabase = true

            // 仅在数据库更新完全成功后，安全清理历史版本旧文件
            if (oldFilePath != null && oldFilePath != targetVersionFile.absolutePath) {
                try {
                    val oldFile = File(oldFilePath)
                    if (oldFile.exists()) oldFile.delete()
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to clean old file: $oldFilePath", e)
                }
            }

            return downloadedSong
        } finally {
            // 只收尾「本次尝试」的东西，任务级状态归 [performDownload] 的外层 finally 管
            val cleanupTarget = committedFile
            if (!isCommittedToDatabase && cleanupTarget != null && cleanupTarget.exists()) {
                cleanupTarget.delete()
            }
            if (tempFile.exists()) {
                tempFile.delete()
            }
            cancellationHandle?.dispose()
            call?.let { activeCalls.remove(taskKey, it) }
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
            var lastPublished = -1f
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
                            // 进度环以 1% 为粒度发布，避免每个 32KB 分片都触发一次重组
                            val rounded = kotlin.math.floor(progress * 100f) / 100f
                            if (rounded > lastPublished) {
                                lastPublished = rounded
                                publishProgress(taskKey, rounded)
                            }
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
        taskGate.cancel(key)
        activeCalls.remove(key)?.cancel()
        activeJobs.remove(key)?.cancel()
        activeDownloads.remove(key)
        publishProgress(key, null)
    }

    fun cancelAllDownloads() {
        taskGate.cancelAll()
        activeCalls.values.forEach { it.cancel() }
        activeCalls.clear()
        activeJobs.values.forEach { it.cancel() }
        activeJobs.clear()
        activeDownloads.clear()
        _downloadStates.value = emptyMap()
    }

    /** 供测试与排障：当前真正在跑的传输任务数 */
    fun activeTaskKeys(): Set<String> = taskGate.activeKeys()
}

/**
 * 把下载异常翻成用户能看懂的短句。
 *
 * 直接把 `Unable to resolve host "music.xxx"` 之类的系统消息甩到 Toast 上既不好读，
 * 也会被系统截断；这里只保留「发生了什么」这一层信息，纯函数便于单测。
 */
fun friendlyDownloadError(e: Throwable): String {
    val raw = e.message.orEmpty()
    return when {
        e is DownloadCancelledException -> "下载已取消"
        raw.contains("Unable to resolve host", true) || raw.contains("No address associated", true) ->
            "网络不可用，无法解析服务器地址"
        raw.contains("timeout", true) || raw.contains("timed out", true) ->
            "服务端读取超时"
        raw.contains("stream was reset", true) || raw.contains("Connection reset", true) ||
            raw.contains("Broken pipe", true) || raw.contains("unexpected end of stream", true) ->
            "连接被重置，请重试"
        raw.contains("HTTP 500") || raw.contains("HTTP 502") || raw.contains("HTTP 503") || raw.contains("HTTP 504") ->
            "服务端读取源文件失败"
        raw.contains("HTTP 401") || raw.contains("HTTP 403") ->
            "登录状态失效，请重新登录"
        raw.isBlank() -> "未知错误"
        else -> raw
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
