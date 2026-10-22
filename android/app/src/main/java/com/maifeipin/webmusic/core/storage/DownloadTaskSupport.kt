package com.maifeipin.webmusic.core.storage

import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

/**
 * 共享任务被 `cancelDownload()` / `cancelAllDownloads()` 主动撤掉时抛出的异常。
 *
 * 单独成型（而不是直接抛 [CancellationException]）的原因：等待方自己的协程并没有被取消，
 * 把 CancellationException 交给调用方会被 Kotlin 结构化并发当成「调用方该结束了」，
 * 连累上层的 scope；这里降级成普通 [IOException]，只影响本次调用的结果。
 */
internal class DownloadCancelledException(message: String = "下载已取消") : IOException(message)

/**
 * 按 taskKey（服务器 + 账号 + 曲目）去重的任务闸门。
 *
 * 手动下载与「播放即缓存」都走同一个入口，同一曲目必然会撞车。没有这层闸门时：
 * 后启动的任务会覆盖 `activeCalls` / `activeJobs` 的登记，先结束的那个又把自己那笔登记删掉，
 * 于是进度消失、取消漏掉任务、还可能留下多份中间文件。
 *
 * 语义：
 * 1. 同一 key 只有一个真正的传输在跑，其余调用方 `await` 同一份结果；
 * 2. 共享任务跑在 [scope] 里，等待方单独取消不会带走传输（UI 重建不该打断下载）；
 * 3. 最后一个完成时（无论成功/失败/取消）自动从在途表摘除，不会留下永久占位；
 * 4. 取消后占位保留到任务真正退出；此期间同 key 的新请求直接拿到「已取消」，不启动第二次传输。
 */
internal class DownloadTaskGate<T>(
    private val scope: CoroutineScope,
    private val onJoin: (String) -> Unit = {}
) {

    private val inFlight = ConcurrentHashMap<String, Deferred<T>>()

    suspend fun run(key: String, block: suspend () -> T): T {
        while (true) {
            inFlight[key]?.let { existing ->
                onJoin(key)
                // 显式处理「取消中」：Deferred.cancel() 只是把 Job 置成 cancelling，旧任务可能
                // 还卡在不可取消的收尾里。此时若直接 await()，会一直挂到它真正完成，调用方
                // 看起来像点了没反应。这里立刻返回「已取消」，占位仍留给完成回调摘除。
                if (existing.isCancelled) throw DownloadCancelledException()
                return awaitShared(existing)
            }

            val deferred = scope.async(start = CoroutineStart.LAZY) { block() }
            // 竞态下可能有别的调用方先占位：自己这份立刻丢弃，回去等它的结果
            if (inFlight.putIfAbsent(key, deferred) != null) {
                deferred.cancel()
                continue
            }
            // 只摘除自己这一份，避免误删后来者的登记
            deferred.invokeOnCompletion { inFlight.remove(key, deferred) }
            deferred.start()
            return awaitShared(deferred)
        }
    }

    /** 主动撤销某条共享任务（取消下载 / 退出登录）。占位由完成回调摘除，见 [cancelAll]。 */
    fun cancel(key: String) {
        inFlight[key]?.cancel()
    }

    /**
     * 撤销全部共享任务。
     *
     * 这里**只发取消信号，不清空在途表**：`cancel()` 只是把 Job 标成取消态，真正退出还要等
     * 阻塞中的读操作（OkHttp `Call.cancel()`）返回。若在此处 `clear()`，占位会提前释放，
     * 同 key 的新任务立刻进场，随后旧任务的 `onFinished` 收尾时按 key 清理，会把新任务的
     * 进度 / 在途登记一起抹掉。
     *
     * 占位一律留给 `run()` 里注册的 `invokeOnCompletion { remove(key, deferred) }` 摘除。
     * 因此在旧任务真正退出之前，同一曲目的新请求会立刻得到 [DownloadCancelledException]
     * （不会挂起等待，也不会启动第二次传输），退出之后再请求才会重新开始。
     */
    fun cancelAll() {
        inFlight.values.forEach { it.cancel() }
    }

    /** 当前在途的 taskKey 快照，供测试与排障使用 */
    fun activeKeys(): Set<String> = inFlight.keys.toSet()

    private suspend fun awaitShared(deferred: Deferred<T>): T = try {
        deferred.await()
    } catch (e: CancellationException) {
        // 自己也被取消了 → 原样上抛，交给结构化并发
        if (!currentCoroutineContext().isActive) throw e
        throw DownloadCancelledException()
    }
}

/**
 * 下载的尝试 / 退避骨架。
 *
 * 关键约束：`onFinished` 必须覆盖「成功、失败、以及退避等待中被取消」三种收尾路径。
 * 之前的实现把清理写在单次尝试的 finally 里、并用 `retryScheduled` 标记跳过清理，
 * 结果退避期间被取消时既不会重试、也不会清理进度，界面就永久卡在转圈。
 *
 * 因此这里的结构是：`pause` 位于尝试级 finally **之外**，任务级 `onFinished` 位于整个循环外层。
 *
 * @param attempt 单次尝试，失败直接抛异常；`attempt` 序号从 1 开始
 */
internal suspend fun <T> retryOfflineDownload(
    maxAttempts: Int,
    backoffs: LongArray,
    isRetryable: (Throwable) -> Boolean,
    pause: suspend (Long) -> Unit = { delay(it) },
    onFinished: () -> Unit = {},
    attempt: suspend (Int) -> T
): T {
    try {
        var index = 0
        var lastError: Throwable? = null

        while (index < maxAttempts) {
            index++
            var backoffMs = 0L
            try {
                return attempt(index)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Throwable) {
                lastError = e
                if (!isRetryable(e) || index >= maxAttempts) throw e
                backoffMs = backoffs.getOrElse(index - 1) { backoffs.lastOrNull() ?: 0L }
            }
            // 退避等待不放在 try/finally 内：此处被取消时不会有「跳过重试但跳过清理」的空档
            pause(backoffMs)
        }

        throw lastError ?: IOException("下载失败：未知错误")
    } finally {
        // 任务级收尾：成功 / 失败 / 退避中取消，全都要走到这里
        onFinished()
    }
}
