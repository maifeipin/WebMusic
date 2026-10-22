package com.maifeipin.webmusic.core.update

import android.content.pm.PackageManager
import android.os.Build
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import com.google.gson.Gson
import com.maifeipin.webmusic.BuildConfig
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

/**
 * 服务端 /api/app/version 的响应体。
 *
 * 所有字段都带默认值，Gson 反序列化时缺失字段会保留默认值，便于新旧服务端兼容。
 */
data class AppVersionDto(
    val hasUpdate: Boolean = false,
    val forceUpdate: Boolean = false,
    val channel: String? = null,
    val versionCode: Int = 0,
    val versionName: String? = null,
    val packageName: String? = null,
    val minSupportedVersionCode: Int? = null,
    val sizeBytes: Long = 0L,
    val sha256: String? = null,
    val changelog: String? = null,
    val publishedAt: String? = null,
    val downloadUrl: String? = null
)

/** 下载进度（receivedBytes / totalBytes，totalBytes 未知时为 0）。 */
data class UpdateDownloadProgress(val receivedBytes: Long, val totalBytes: Long) {
    val fraction: Float
        get() = if (totalBytes > 0L) {
            (receivedBytes.toDouble() / totalBytes.toDouble()).toFloat().coerceIn(0f, 1f)
        } else {
            0f
        }
}

/**
 * 应用内静默更新：版本检查 → 断点续传下载 → SHA-256 校验 → FileProvider 调起系统安装。
 *
 * 设计约束（对应"不做进程壳/动态 Dex"的结论）：
 * - 只下载并安装完整 APK，绝不动态加载代码；
 * - 安装走系统 PackageInstaller 会话，清单、权限、签名校验全部由系统保证；
 * - 覆盖安装保留 Token、离线音乐文件与 SQLite 数据库（同一 applicationId + 同一签名）。
 */
object AppUpdateManager {

    private const val UPDATE_DIR = "updates"
    private const val APK_MIME = "application/vnd.android.package-archive"
    private const val BUFFER_SIZE = 64 * 1024

    private val gson = Gson()

    /**
     * 更新通道与安装包一一对应：
     * debug 构建 applicationId 带 .debug 后缀、使用仓库内置固定 debug keystore 签名，
     * 因此必须与正式渠道分开分发，否则会因签名/包名不一致安装失败。
     */
    val channel: String
        get() = if (BuildConfig.DEBUG) "debug" else "release"

    val currentVersionCode: Int
        get() = BuildConfig.VERSION_CODE

    val currentVersionName: String
        get() = BuildConfig.VERSION_NAME

    private val client: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(60, TimeUnit.SECONDS)
            .writeTimeout(60, TimeUnit.SECONDS)
            .retryOnConnectionFailure(true)
            .build()
    }

    /**
     * 查询最新版本。服务端不可达、响应异常时抛出 IOException，由调用方决定是否静默忽略。
     */
    suspend fun checkForUpdate(
        context: Context,
        serverUrl: String,
        token: String?
    ): AppVersionDto = withContext(Dispatchers.IO) {
        // Internal debug builds use the cloud release feed independently of backend deployment.
        if (channel == "debug") {
            try {
                val feed = Request.Builder().url("https://github.com/maifeipin/WebMusic/releases/download/android-debug-latest/update.json")
                    .header("Cache-Control", "no-cache").build()
                val info = withResponse(feed) { result ->
                    if (!result.isSuccessful) throw IOException("更新源尚未发布：HTTP ${result.code}")
                    gson.fromJson(result.body?.string() ?: throw IOException("更新清单为空"), AppVersionDto::class.java)
                }
                if (info.packageName == context.packageName && info.channel == channel) return@withContext info
            } catch (e: kotlinx.coroutines.CancellationException) { throw e }
            catch (_: Exception) { /* Fall back to the configured server. */ }
        }
        val url = buildString {
            append(serverUrl.trimEnd('/'))
            append("/api/app/version?currentCode=").append(currentVersionCode)
            append("&channel=").append(Uri.encode(channel))
            append("&packageName=").append(Uri.encode(context.packageName))
        }

        val request = Request.Builder()
            .url(url)
            .header("Accept", "application/json")
            .header("Cache-Control", "no-cache")
            .apply {
                if (!token.isNullOrEmpty()) header("Authorization", "Bearer $token")
            }
            .build()

        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                if (channel == "debug" && response.code == 404) {
                    val feed = Request.Builder().url("https://github.com/maifeipin/WebMusic/releases/download/android-debug-latest/update.json").build()
                    return@withContext client.newCall(feed).execute().use { result ->
                        if (!result.isSuccessful) throw IOException("更新源尚未发布：HTTP ${result.code}")
                        gson.fromJson(result.body?.string() ?: throw IOException("更新清单为空"), AppVersionDto::class.java)
                    }
                }
                throw IOException("版本检查失败：HTTP ${response.code}")
            }
            val body = response.body?.string()
                ?: throw IOException("版本检查响应为空")
            gson.fromJson(body, AppVersionDto::class.java)
                ?: throw IOException("版本检查响应无法解析")
        }
    }

    /**
     * 下载安装包。已存在的 .part 文件会以 Range 请求续传；服务端忽略 Range 时自动整包重下。
     * 下载完成且 SHA-256 校验通过后才返回可安装文件。
     */
    suspend fun downloadApk(
        context: Context,
        serverUrl: String,
        info: AppVersionDto,
        token: String?,
        onProgress: (UpdateDownloadProgress) -> Unit
    ): File = withContext(Dispatchers.IO) {
        require(info.versionCode > currentVersionCode) { "更新版本必须高于当前版本" }
        require(info.packageName == context.packageName && info.channel == channel) { "更新包名或渠道不匹配" }
        require(info.sizeBytes > 0 && info.sha256?.matches(Regex("[a-fA-F0-9]{64}")) == true) { "缺少有效的安装包大小或 SHA-256" }

        val rawUrl = info.downloadUrl?.takeIf { it.isNotBlank() }
            ?: throw IOException("服务端未返回下载地址")
        val url = resolveDownloadUrl(serverUrl, rawUrl)
        require(Uri.parse(url).scheme == "https") { "更新下载必须使用 HTTPS" }

        val dir = File(context.getExternalFilesDir(null) ?: context.filesDir, UPDATE_DIR)
        if (!dir.exists() && !dir.mkdirs()) throw IOException("无法创建更新缓存目录")

        val targetName = "webmusic-${info.versionCode}.apk"
        val target = File(dir, targetName)
        val partial = File(dir, "$targetName.part")
        val expectedSha = info.sha256?.trim()?.lowercase()?.takeIf { it.length == 64 }

        // 1. 已下载且校验通过 → 直接复用
        if (target.exists()) {
            if (expectedSha != null && sha256Of(target) == expectedSha) {
                validateApk(context, target, info)
                onProgress(UpdateDownloadProgress(target.length(), target.length()))
                return@withContext target
            }
            target.delete()
        }

        // 2. 清理其它版本的残留，避免长期占用用户存储
        dir.listFiles()?.forEach { leftover ->
            val stale = leftover.name != targetName && leftover.name != partial.name &&
                (leftover.name.endsWith(".apk") || leftover.name.endsWith(".part"))
            if (stale) leftover.delete()
        }

        // Each retry re-reads the actual partial length and rebuilds the Range header.
        retryUpdateTransfer {
            // 3. 断点续传
            var existing = if (partial.exists()) partial.length() else 0L
            val requestBuilder = Request.Builder()
                .url(url)
                .header("Accept-Encoding", "identity")
                .apply {
                    if (!token.isNullOrEmpty() && Uri.parse(url).host == Uri.parse(serverUrl).host) header("Authorization", "Bearer $token")
                    if (existing > 0L) header("Range", "bytes=$existing-")
                }

            withResponse(requestBuilder.build()) { response ->
                if (response.code == 416 && existing > 0L) {
                    // Range 越界：本地分片已覆盖整包，进入校验流程
                } else if (!response.isSuccessful) {
                    if (response.code == 408 || response.code == 429 || response.code in 500..599) {
                        throw IOException("更新服务器暂时不可用")
                    }
                    throw UpdateTransferRejected("下载失败：服务器返回 HTTP ${response.code}，请重新检查更新")
                } else {
                    if (existing > 0L && response.code != 206) {
                        // 服务端（或代理）不支持 Range，返回了整包，只能从头写
                        partial.delete()
                        existing = 0L
                    }

                    if (response.code == 206) {
                        val range = response.header("Content-Range").orEmpty()
                        if (!range.startsWith("bytes $existing-")) {
                            partial.delete()
                            throw UpdateTransferRejected("续传响应范围不匹配，请重试")
                        }
                    }
                    val body = response.body ?: throw UpdateTransferRejected("下载响应为空")
                    val contentLength = body.contentLength()
                    val total = if (contentLength > 0L) existing + contentLength else info.sizeBytes
                    var received = existing
                    onProgress(UpdateDownloadProgress(received, total))

                    body.byteStream().use { input ->
                        FileOutputStream(partial, existing > 0L).use { output ->
                            val buffer = ByteArray(BUFFER_SIZE)
                            while (true) {
                                // 协程取消时立即中断 IO，避免 Activity 销毁后仍在后台写盘
                                currentCoroutineContext().ensureActive()
                                val read = input.read(buffer)
                                if (read == -1) break
                                output.write(buffer, 0, read)
                                received += read
                                if (received > info.sizeBytes) throw UpdateTransferRejected("安装包超过声明大小")
                                onProgress(UpdateDownloadProgress(received, total))
                            }
                            output.flush()
                            output.fd.sync()
                        }
                    }
                }
            }
        }

        // 4. 完整性校验
        val downloaded = partial.length()
        if (downloaded <= 0L) {
            partial.delete()
            throw IOException("下载结果为空，请重试")
        }
        if (info.sizeBytes > 0L && downloaded != info.sizeBytes) {
            // 保留分片以便下次续传，不删除
            throw IOException("下载不完整（${downloaded}/${info.sizeBytes} 字节），请重试")
        }
        if (expectedSha != null) {
            val actual = sha256Of(partial)
            if (!actual.equals(expectedSha, ignoreCase = true)) {
                partial.delete()
                throw IOException("安装包校验失败（SHA-256 不匹配），已丢弃，请重试")
            }
        }

        validateApk(context, partial, info)

        // 5. 提交为最终文件名
        if (target.exists()) target.delete()
        if (!partial.renameTo(target)) {
            partial.copyTo(target, overwrite = true)
            partial.delete()
        }
        target
    }


    /** Cancellation closes the call while its blocking body is being read; scope waits for IO cleanup. */
    private suspend fun <T> withResponse(request: Request, block: suspend (okhttp3.Response) -> T): T = coroutineScope {
        val call = client.newCall(request)
        val watcher = launch(Dispatchers.Default, start = CoroutineStart.UNDISPATCHED) {
            try { awaitCancellation() } finally { call.cancel() }
        }
        try {
            withContext(Dispatchers.IO) { call.execute().use { block(it) } }
        } finally { watcher.cancel() }
    }

    @Suppress("DEPRECATION")
    fun validateApk(context: Context, apk: File, info: AppVersionDto) {
        require(apk.length() == info.sizeBytes && sha256Of(apk).equals(info.sha256, true)) { "APK 完整性校验失败" }
        val pm = context.packageManager
        val flags = if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES else PackageManager.GET_SIGNATURES
        val candidate = pm.getPackageArchiveInfo(apk.absolutePath, flags) ?: error("无法解析 APK")
        val installed = pm.getPackageInfo(context.packageName, flags)
        val code = if (Build.VERSION.SDK_INT >= 28) candidate.longVersionCode else candidate.versionCode.toLong()
        require(candidate.packageName == context.packageName && code == info.versionCode.toLong() && code > currentVersionCode) { "APK 包名或版本不匹配" }
        require(candidate.applicationInfo?.minSdkVersion?.let { it <= Build.VERSION.SDK_INT } == true) { "APK 不支持当前系统" }
        fun signatures(pkg: android.content.pm.PackageInfo): Set<String> {
            val certs = if (Build.VERSION.SDK_INT >= 28) pkg.signingInfo?.apkContentsSigners else pkg.signatures
            return certs?.map { cert -> MessageDigest.getInstance("SHA-256").digest(cert.toByteArray()).joinToString("") { "%02x".format(it) } }?.toSet().orEmpty()
        }
        val trusted = signatures(installed)
        require(trusted.isNotEmpty() && trusted == signatures(candidate)) { "APK 签名与已安装应用不一致" }
    }

    /** 是否已获得"安装未知来源应用"授权（Android 8.0+ 必须由用户手动授予）。 */
    fun canInstallPackages(context: Context): Boolean =
        context.packageManager.canRequestPackageInstalls()

    /** 跳转到本应用的"安装未知应用"授权页。 */
    fun openInstallPermissionSettings(context: Context) {
        val packageUri = Uri.parse("package:${context.packageName}")
        val direct = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, packageUri)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (runCatching { context.startActivity(direct) }.isSuccess) return

        runCatching {
            context.startActivity(
                Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            )
        }.onFailure {
            runCatching {
                context.startActivity(
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, packageUri)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                )
            }
        }
    }

    /**
     * 调起系统安装界面（覆盖安装，保留登录态 / 离线文件 / 数据库）。
     * 调用前应先确认 canInstallPackages() 为 true。
     */
    fun installApk(context: Context, apk: File): Boolean {
        if (!apk.exists() || apk.length() <= 0L) return false
        return try {
            val uri = FileProvider.getUriForFile(
                context,
                "${context.packageName}.fileprovider",
                apk
            )
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, APK_MIME)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
            true
        } catch (e: Exception) {
            false
        }
    }

    /** 删除指定版本的缓存安装包（安装成功后可选清理）。 */
    fun clearDownloadedApk(context: Context, versionCode: Int) {
        val dir = File(context.getExternalFilesDir(null) ?: context.filesDir, UPDATE_DIR)
        listOf("webmusic-$versionCode.apk", "webmusic-$versionCode.apk.part").forEach {
            runCatching { File(dir, it).delete() }
        }
    }

    /** 服务端返回相对路径（推荐）或绝对 URL，这里统一解析为可下载地址。 */
    fun resolveDownloadUrl(serverUrl: String, raw: String): String {
        val value = raw.trim()
        return if (value.startsWith("http://", ignoreCase = true) ||
            value.startsWith("https://", ignoreCase = true)
        ) {
            value
        } else {
            serverUrl.trimEnd('/') + "/" + value.trimStart('/')
        }
    }

    private fun sha256Of(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(BUFFER_SIZE)
            while (true) {
                val read = input.read(buffer)
                if (read == -1) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
