package com.maifeipin.webmusic.core.network

import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Query
import java.util.concurrent.TimeUnit

data class LoginRequest(val username: String, val password: String)
data class LoginResponse(val token: String)

data class MediaFileDto(
    val id: Int,
    val filePath: String?,
    val title: String?,
    val artist: String?,
    val album: String?,
    val genre: String?,
    val year: Int?,
    val duration: String?,
    val sizeBytes: Long?,
    val fileHash: String?,
    val contentVersion: String? = null
)

data class PagedResult<T>(
    val total: Int,
    val page: Int,
    val pageSize: Int,
    val files: List<T>
)

interface WebMusicApi {
    @POST("api/auth/login")
    suspend fun login(@Body req: LoginRequest): LoginResponse

    @GET("api/media")
    suspend fun getMediaFiles(
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 50,
        @Query("search") search: String? = null
    ): PagedResult<MediaFileDto>

    @GET("api/lyrics/{mediaId}")
    suspend fun getLyrics(
        @retrofit2.http.Path("mediaId") mediaId: Int
    ): com.maifeipin.webmusic.core.lyrics.LyricsResponse
}

object WebMusicNetwork {

    private class AuthInterceptor(private val tokenManager: TokenManager) : Interceptor {
        override fun intercept(chain: Interceptor.Chain): Response {
            val original = chain.request()
            val token = tokenManager.token
            val requestBuilder = original.newBuilder()
            if (!token.isNullOrEmpty()) {
                requestBuilder.header("Authorization", "Bearer $token")
            }
            return chain.proceed(requestBuilder.build())
        }
    }

    fun createClient(tokenManager: TokenManager): OkHttpClient {
        val logging = HttpLoggingInterceptor().apply {
            level = HttpLoggingInterceptor.Level.HEADERS
            // 严格脱敏 Authorization 与 Cookie，防止 Token 泄漏到 Logcat
            redactHeader("Authorization")
            redactHeader("Cookie")
        }
        return OkHttpClient.Builder()
            .addInterceptor(AuthInterceptor(tokenManager))
            .addInterceptor(logging)
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .build()
    }

    fun createApi(tokenManager: TokenManager): WebMusicApi {
        val baseUrl = if (tokenManager.serverUrl.endsWith("/")) tokenManager.serverUrl else "${tokenManager.serverUrl}/"
        val client = createClient(tokenManager)
        return Retrofit.Builder()
            .baseUrl(baseUrl)
            .client(client)
            .addConverterFactory(GsonConverterFactory.create())
            .build()
            .create(WebMusicApi::class.java)
    }

    /**
     * 生成原文件媒体流 URL
     * 规范：使用服务端真实路由 /api/media/stream/{id}
     * Token 不拼接到 Query 参数中，由 OkHttp 拦截器在 Header 中安全承载
     */
    fun buildStreamUrl(serverUrl: String, mediaId: Int): String {
        val base = serverUrl.trimEnd('/')
        return "$base/api/media/stream/$mediaId"
    }

    /**
     * 生成专辑封面图片完整 URL
     */
    fun buildCoverUrl(serverUrl: String, coverArt: String?): String? {
        if (coverArt.isNullOrEmpty()) return null
        val base = serverUrl.trimEnd('/')
        return if (coverArt.startsWith("http://") || coverArt.startsWith("https://")) {
            coverArt
        } else {
            val clean = coverArt.trimStart('/')
            "$base/$clean"
        }
    }

    /**
     * 生成安全强隔离缓存键
     * 结合：ServerUrl 完整 256 位 SHA-256 散列（极低碰撞概率，移除异常降级硬编码）+ 用户身份 + 媒体ID + 内容版本/哈希/大小
     * 优先采用服务端独立 ContentVersion；若未提供则降级至 FileHash，最后降级至 SizeBytes
     */
    fun generateSafeCacheKey(serverUrl: String, username: String?, song: MediaFileDto): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        val bytes = md.digest(serverUrl.trimEnd('/').toByteArray(Charsets.UTF_8))
        val serverHash = bytes.joinToString("") { "%02x".format(it) }

        val safeUser = username?.ifEmpty { "anon" } ?: "anon"
        val versionTag = song.contentVersion?.ifEmpty { null }
            ?: song.fileHash?.ifEmpty { null }
            ?: "sz_${song.sizeBytes ?: 0L}"
        return "wm_${serverHash}_${safeUser}_${song.id}_${versionTag}"
    }
}
