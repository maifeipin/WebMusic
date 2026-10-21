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
    val sizeBytes: Long?
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
}
