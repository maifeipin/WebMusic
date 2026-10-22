package com.maifeipin.webmusic.core.storage

import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/**
 * 覆盖离线下载的三个并发/生命周期约束：
 * 1. 退避等待期间被取消，任务级清理仍然执行（否则界面永久转圈）；
 * 2. 同一 taskKey 的手动下载与自动缓存并发时只跑一次传输；
 * 3. 取消全部后占位保留到旧任务真正退出，避免旧任务收尾误伤同 key 的新任务。
 */
class DownloadTaskSupportTest {

    // ---------- retryOfflineDownload：退避中取消后的收尾 ----------

    @Test
    fun cancellingDuringBackoffStillRunsTaskCleanup() = runBlocking {
        val attempts = AtomicInteger(0)
        val finished = AtomicInteger(0)
        val inBackoff = CompletableDeferred<Unit>()

        val job = launch {
            retryOfflineDownload<String>(
                maxAttempts = 3,
                backoffs = longArrayOf(60_000L),
                isRetryable = { true },
                pause = {
                    inBackoff.complete(Unit)
                    delay(60_000L) // 真实场景里就在这个点上被取消
                },
                onFinished = { finished.incrementAndGet() }
            ) {
                attempts.incrementAndGet()
                throw IOException("SMB Read failed: STATUS_IO_TIMEOUT")
            }
        }

        inBackoff.await()
        assertEquals(1, attempts.get())
        job.cancelAndJoin()

        assertEquals("退避等待中被取消时不能重试", 1, attempts.get())
        assertEquals("任务级清理必须执行且只执行一次", 1, finished.get())
    }

    @Test
    fun successAlsoRunsTaskCleanupExactlyOnce() = runBlocking {
        val finished = AtomicInteger(0)
        val value = retryOfflineDownload(
            maxAttempts = 3,
            backoffs = longArrayOf(1L),
            isRetryable = { true },
            onFinished = { finished.incrementAndGet() }
        ) { "ok" }

        assertEquals("ok", value)
        assertEquals(1, finished.get())
    }

    @Test
    fun transientFailureRetriesThenSucceeds() = runBlocking {
        val attempts = AtomicInteger(0)
        val waits = mutableListOf<Long>()
        val finished = AtomicInteger(0)

        val value = retryOfflineDownload(
            maxAttempts = 3,
            backoffs = longArrayOf(1_000L, 2_000L),
            isRetryable = { it is IOException },
            pause = { waits.add(it) },
            onFinished = { finished.incrementAndGet() }
        ) {
            if (attempts.incrementAndGet() < 3) throw IOException("attempt failed")
            "done"
        }

        assertEquals("done", value)
        assertEquals(listOf(1_000L, 2_000L), waits)
        assertEquals(3, attempts.get())
        assertEquals(1, finished.get())
    }

    @Test
    fun exhaustedAttemptsSurfaceLastErrorAndStillCleanUp() = runBlocking {
        val attempts = AtomicInteger(0)
        val finished = AtomicInteger(0)
        try {
            retryOfflineDownload<String>(
                maxAttempts = 3,
                backoffs = longArrayOf(1L, 2L),
                isRetryable = { true },
                onFinished = { finished.incrementAndGet() }
            ) {
                attempts.incrementAndGet()
                throw IOException("always down")
            }
            fail("Expected failure")
        } catch (e: IOException) {
            assertEquals("always down", e.message)
        }
        assertEquals(3, attempts.get())
        assertEquals(1, finished.get())
    }

    @Test
    fun nonRetryableErrorDoesNotWaitOrRepeat() = runBlocking {
        val attempts = AtomicInteger(0)
        val finished = AtomicInteger(0)
        try {
            retryOfflineDownload<String>(
                maxAttempts = 3,
                backoffs = longArrayOf(1L),
                isRetryable = { false },
                pause = { fail("Must not wait") },
                onFinished = { finished.incrementAndGet() }
            ) {
                attempts.incrementAndGet()
                throw IllegalStateException("bad path")
            }
            fail("Expected failure")
        } catch (_: IllegalStateException) {
            // expected
        }
        assertEquals(1, attempts.get())
        assertEquals(1, finished.get())
    }

    // ---------- DownloadTaskGate：手动下载与自动缓存撞车 ----------

    @Test
    fun concurrentRequestsForSameKeyShareOneTransfer() = runBlocking {
        val joins = AtomicInteger(0)
        val gate = DownloadTaskGate<String>(scope = this, onJoin = { joins.incrementAndGet() })
        val transfers = AtomicInteger(0)
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()

        // 手动下载先起
        val manual = launch {
            gate.run("srv_user_42") {
                transfers.incrementAndGet()
                started.complete(Unit)
                release.await()
                "downloaded"
            }
        }
        started.await()

        // 自动缓存后到，必须复用同一份传输而不是再开一条
        val auto = launch {
            gate.run("srv_user_42") {
                transfers.incrementAndGet()
                "second-transfer"
            }
        }
        yield()
        yield()
        release.complete(Unit)

        manual.join()
        auto.join()

        assertEquals("同一曲目只允许一次传输", 1, transfers.get())
        assertEquals("后到者必须走复用分支", 1, joins.get())
        assertTrue("任务结束后不应残留登记", gate.activeKeys().isEmpty())
    }

    @Test
    fun differentKeysRunIndependently() = runBlocking {
        val gate = DownloadTaskGate<String>(this)
        val transfers = AtomicInteger(0)
        val results = mutableListOf<String>()

        val job1 = launch { results.add(gate.run("srv_a_user_1") { transfers.incrementAndGet(); "a" }) }
        val job2 = launch { results.add(gate.run("srv_a_user_2") { transfers.incrementAndGet(); "b" }) }
        job1.join()
        job2.join()

        assertEquals(2, transfers.get())
        assertEquals(listOf("a", "b"), results.sorted())
        assertTrue(gate.activeKeys().isEmpty())
    }

    @Test
    fun cancelledSharedTaskReleasesSlotAndNotifiesWaiter() = runBlocking {
        val gate = DownloadTaskGate<String>(this)
        val started = CompletableDeferred<Unit>()

        val waiter = launch {
            try {
                gate.run("srv_user_7") {
                    started.complete(Unit)
                    awaitCancellation()
                }
                fail("Expected DownloadCancelledException")
            } catch (_: DownloadCancelledException) {
                // 共享任务被 cancelAllDownloads 撤掉，等待方拿到的是普通失败而不是自身被取消
            }
        }
        started.await()
        gate.cancel("srv_user_7")
        waiter.join()

        assertTrue("取消后必须摘除在途登记", gate.activeKeys().isEmpty())

        // 槽位释放后可以重新开始
        val retried = gate.run("srv_user_7") { "retried" }
        assertEquals("retried", retried)
        assertTrue(gate.activeKeys().isEmpty())
    }

    @Test
    fun callerCancellationPropagatesWithoutKillingSharedTransfer() = runBlocking {
        val gate = DownloadTaskGate<String>(this)
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val transfers = AtomicInteger(0)

        val owner = launch {
            gate.run("srv_user_9") {
                transfers.incrementAndGet()
                started.complete(Unit)
                release.await()
                "done"
            }
        }
        started.await()

        // 第二个等待方（比如界面重建）取消，不应该连带运传输
        val impatient = launch { gate.run("srv_user_9") { "other" } }
        yield()
        yield()
        impatient.cancelAndJoin()

        assertFalse("传输仍在跑，不能残留第二个任务", gate.activeKeys().isEmpty())
        release.complete(Unit)
        owner.join()

        assertEquals(1, transfers.get())
        assertTrue(gate.activeKeys().isEmpty())
    }

    /**
     * cancelAll() 的取消只是「请求」：旧任务可能还卡在阻塞读里，收尾也要一点时间。
     * 占位必须留在原处，否则同 key 新任务会与尚未退出的旧任务并存，旧任务收尾时按 key 清理，
     * 新任务的进度就被抹掉了。
     *
     * 同时锁定「取消中不挂起」：第二个调用必须在旧任务放行前就拿到 [DownloadCancelledException]，
     * 而不是 await 到旧任务收尾结束。
     */
    @Test
    fun cancelAllKeepsSlotUntilCancelledTaskActuallyExits() = runBlocking {
        val key = "srv_user_11"
        val gate = DownloadTaskGate<String>(this)
        val transfers = AtomicInteger(0)
        val progress = ConcurrentHashMap<String, String>()
        val firstStarted = CompletableDeferred<Unit>()
        val tailGate = CompletableDeferred<Unit>()
        val staleExited = CompletableDeferred<Unit>()

        val stale = launch {
            try {
                gate.run(key) {
                    transfers.incrementAndGet()
                    progress[key] = "stale"
                    firstStarted.complete(Unit)
                    try {
                        awaitCancellation()
                    } finally {
                        // 模拟真实收尾：Call.cancel() 之后仍有一段不可取消的清理
                        withContext(NonCancellable) { tailGate.await() }
                        progress.remove(key)
                        staleExited.complete(Unit)
                    }
                }
            } catch (_: DownloadCancelledException) {
                // 旧任务最终以取消失败收场
            }
        }
        firstStarted.await()
        gate.cancelAll()

        assertTrue(
            "取消信号发出后必须保留占位，直到任务真正退出",
            gate.activeKeys().contains(key)
        )

        // 取消全部后立刻重新请求同一曲目：旧任务仍在（不可取消的）收尾阶段
        val secondOutcome = CompletableDeferred<Throwable?>()
        val second = launch {
            val error = try {
                gate.run(key) { transfers.incrementAndGet(); "fresh" }
                null
            } catch (e: Throwable) {
                e
            }
            secondOutcome.complete(error)
        }

        try {
            // 限定 2s：若实现退回「await 到旧任务结束」，这里会超时并留下明确失败信息
            val outcome = withTimeoutOrNull(2_000L) { secondOutcome.await() }

            assertTrue(
                "旧任务退出前第二个调用必须已有结论（当前仍挂起）",
                secondOutcome.isCompleted
            )
            assertNotNull("第二个调用不应成功启动新传输", outcome)
            assertTrue(
                "第二个调用必须收到 DownloadCancelledException，实际：$outcome",
                outcome is DownloadCancelledException
            )
            // 结论已拿到，join 只是等协程真正收尾（此时 tailGate 仍未放行）
            second.join()
            assertEquals("旧任务退出前不得启动第二次传输", 1, transfers.get())
        } finally {
            // 无论断言是否失败都放行旧任务，避免 runBlocking 等子协程挂死
            tailGate.complete(Unit)
        }

        staleExited.await()
        stale.join()
        assertTrue("旧任务退出后占位必须被摘除", gate.activeKeys().isEmpty())

        // 旧任务退出后重新下载：新进度不会被旧任务的清理影响
        val value = gate.run(key) {
            transfers.incrementAndGet()
            progress[key] = "fresh"
            "ok"
        }
        assertEquals("ok", value)
        assertEquals(2, transfers.get())
        assertEquals("旧任务清理不得影响新进度", "fresh", progress[key])
        assertTrue(gate.activeKeys().isEmpty())
    }
}
