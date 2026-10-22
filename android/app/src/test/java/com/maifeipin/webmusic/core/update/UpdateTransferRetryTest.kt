package com.maifeipin.webmusic.core.update

import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class UpdateTransferRetryTest {
    @Test fun resumesAfterTransientFailureWithBackoff() = runBlocking {
        var attempts = 0
        val waits = mutableListOf<Long>()
        val result = retryUpdateTransfer(pause = { waits.add(it); Unit }) {
            if (++attempts < 3) throw IOException("stream reset")
            "complete"
        }
        assertEquals("complete", result)
        assertEquals(listOf(1000L, 2000L), waits)
        assertEquals(3, attempts)
    }

    @Test fun stopsAfterThreeRetriesWithLocalizedError() = runBlocking {
        var attempts = 0
        try {
            retryUpdateTransfer(pause = {}) { attempts++; throw IOException("CANCEL") }
            fail("Expected failure")
        } catch (e: IOException) {
            assertEquals(4, attempts)
            assertTrue(e.message!!.contains("已重试 3 次"))
        }
    }

    @Test fun cancellationIsNeverRetried() = runBlocking {
        var attempts = 0
        try {
            retryUpdateTransfer(pause = { fail("Must not wait") }) { attempts++; throw CancellationException() }
            fail("Expected cancellation")
        } catch (_: CancellationException) { assertEquals(1, attempts) }
    }

    @Test fun rejectedResponseIsNeverRetried() = runBlocking {
        var attempts = 0
        try {
            retryUpdateTransfer(pause = { fail("Must not wait") }) { attempts++; throw UpdateTransferRejected("bad range") }
            fail("Expected rejection")
        } catch (_: UpdateTransferRejected) { assertEquals(1, attempts) }
    }
}
