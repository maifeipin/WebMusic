package com.maifeipin.webmusic.core.storage

import java.io.IOException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import org.junit.Assert.assertEquals
import org.junit.Test

class FriendlyDownloadErrorTest {

    @Test
    fun mapsDnsFailure() {
        val e = UnknownHostException("Unable to resolve host \"music.maifeipin.com\": No address associated with hostname")
        assertEquals("网络不可用，无法解析服务器地址", friendlyDownloadError(e))
    }

    @Test
    fun mapsSocketTimeout() {
        assertEquals("服务端读取超时", friendlyDownloadError(SocketTimeoutException("timeout")))
        assertEquals("服务端读取超时", friendlyDownloadError(IOException("Read timed out")))
    }

    @Test
    fun mapsConnectionReset() {
        assertEquals("连接被重置，请重试", friendlyDownloadError(IOException("stream was reset: CANCEL")))
        assertEquals("连接被重置，请重试", friendlyDownloadError(IOException("Connection reset by peer")))
    }

    @Test
    fun mapsServerSideReadFailure() {
        // 服务端 SMB 读超时会以 500 的形式返回，这正是「点了没反应」的真实原因
        assertEquals("服务端读取源文件失败", friendlyDownloadError(IOException("服务端读取源文件失败（HTTP 500）")))
        assertEquals("登录状态失效，请重新登录", friendlyDownloadError(IOException("下载失败：HTTP 403")))
    }

    @Test
    fun keepsUnknownMessagesButNeverEmpty() {
        assertEquals("File commit failed", friendlyDownloadError(IOException("File commit failed")))
        assertEquals("未知错误", friendlyDownloadError(IOException()))
    }
}
