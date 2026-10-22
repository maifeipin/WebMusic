package com.maifeipin.webmusic.core.network

import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Path
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
    val contentVersion: String? = null,
    /** 服务端以秒（含小数）返回的曲目时长，网易云歌词匹配按它就近排序 */
    val totalSeconds: Double? = null,
    val mediaType: String? = null
) {
    fun isVideo(): Boolean {
        if (mediaType.equals("video", ignoreCase = true) || mediaType.equals("mv", ignoreCase = true)) return true
        val path = filePath?.lowercase() ?: ""
        return path.endsWith(".mp4") || path.endsWith(".mkv") || path.endsWith(".webm") ||
               path.endsWith(".avi") || path.endsWith(".mov") || path.endsWith(".flv") ||
               path.endsWith(".ts") || path.endsWith(".m4v")
    }
}

data class PagedResult<T>(
    val total: Int,
    val page: Int,
    val pageSize: Int,
    val files: List<T>
)

data class UserMediaItem(
    val id: Int,
    val title: String?,
    val artist: String?,
    val album: String?,
    val genre: String? = null,
    val duration: Any? = null,
    val totalSeconds: Double? = null,
    val filePath: String? = null,
    val coverArt: String? = null,
    val mediaType: String? = null
) {
    fun toMediaFileDto(): MediaFileDto {
        val seconds = totalSeconds ?: when (val raw = duration) {
            is Number -> raw.toDouble()
            is String -> raw.toDoubleOrNull()
            else -> null
        }
        return MediaFileDto(
            id = id,
            filePath = filePath,
            title = title,
            artist = artist,
            album = album,
            genre = genre,
            year = null,
            duration = duration?.toString(),
            sizeBytes = null,
            fileHash = null,
            contentVersion = null,
            totalSeconds = seconds,
            mediaType = mediaType
        )
    }
}

data class ToggleFavoriteResponse(val isFavorite: Boolean)

data class FavoriteItem(
    val mediaFileId: Int,
    val createdAt: String?,
    val mediaFile: UserMediaItem?
)

data class PagedFavoriteResult(
    val total: Int,
    val page: Int,
    val pageSize: Int,
    val items: List<FavoriteItem>
)

data class HistoryItem(
    val mediaFileId: Int,
    val playedAt: String?,
    val mediaFile: UserMediaItem?
)

data class PagedHistoryResult(
    val total: Int,
    val page: Int,
    val pageSize: Int,
    val items: List<HistoryItem>
)

data class PlaylistSummaryDto(
    val id: Int,
    val name: String,
    val coverArt: String?,
    val createdAt: String?,
    val type: String?,
    val shareToken: String?,
    val count: Int
)

data class CreatePlaylistRequest(val name: String, val coverArt: String? = null)

data class PlaylistSongItem(
    val id: Int,
    val song: UserMediaItem?,
    val addedAt: String?
)

data class PlaylistDetailDto(
    val id: Int,
    val name: String,
    val coverArt: String?,
    val createdAt: String?,
    val songs: List<PlaylistSongItem>
)

/** 服务端已安装的插件（如网易云音乐代理），用于代理搜索与歌词拉取 */
data class PluginDto(
    val id: Int,
    val name: String? = null,
    val baseUrl: String? = null,
    val isEnabled: Boolean = false
)

/** 歌词的直写请求体（后端 /api/lyrics/{id}/save 契约） */
data class SaveLyricsBody(
    val content: String,
    val source: String? = null,
    val version: String? = null
)

data class AiStatusDto(val available: Boolean = false)

interface WebMusicApi {
    @POST("api/auth/login")
    suspend fun login(@Body req: LoginRequest): LoginResponse

    @GET("api/media")
    suspend fun getMediaFiles(
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 50,
        @Query("search") search: String? = null,
        @Query("mediaType") mediaType: String? = null
    ): PagedResult<MediaFileDto>

    @GET("api/lyrics/{mediaId}")
    suspend fun getLyrics(
        @Path("mediaId") mediaId: Int
    ): com.maifeipin.webmusic.core.lyrics.LyricsResponse

    // --- 歌词管理（网易云匹配 / AI 生成） ---
    @GET("api/plugins")
    suspend fun getPlugins(): List<PluginDto>

    /** 通过服务端插件代理搜索网易云歌曲；返回原始 JSON，字段随上游变化，调用方自行解析 */
    @GET("api/plugins/{pluginId}/proxy/search")
    suspend fun searchNeteaseSongs(
        @Path("pluginId") pluginId: Int,
        @Query("keywords") keywords: String
    ): com.google.gson.JsonObject

    /** 通过服务端插件代理拉取指定网易云歌曲的 LRC 歌词 */
    @GET("api/plugins/{pluginId}/proxy/lyric")
    suspend fun getNeteaseLyric(
        @Path("pluginId") pluginId: Int,
        @Query("id") neteaseSongId: Long
    ): com.google.gson.JsonObject

    /** 直写歌词库（需管理员），失败时返回非 2xx 供上层提示 */
    @POST("api/lyrics/{mediaId}/save")
    suspend fun saveLyrics(
        @Path("mediaId") mediaId: Int,
        @Body body: SaveLyricsBody
    ): retrofit2.Response<com.google.gson.JsonObject>

    /** 触发服务端 AI 生成歌词（同步返回，耗时较长，需长超时客户端） */
    @POST("api/lyrics/{mediaId}/generate")
    suspend fun generateLyrics(
        @Path("mediaId") mediaId: Int,
        @Query("lang") lang: String? = null,
        @Query("prompt") prompt: String? = null
    ): com.maifeipin.webmusic.core.lyrics.LyricsResponse

    @GET("api/lyrics/status")
    suspend fun getAiStatus(): AiStatusDto

    // --- 收藏夹 Favorites ---
    @GET("api/user/favorites/ids")
    suspend fun getFavoriteIds(): List<Int>

    @POST("api/user/favorite/{mediaId}")
    suspend fun toggleFavorite(
        @Path("mediaId") mediaId: Int
    ): ToggleFavoriteResponse

    @GET("api/user/favorites")
    suspend fun getFavorites(
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 50
    ): PagedFavoriteResult

    // --- 播放历史 History ---
    @POST("api/user/history/{mediaId}")
    suspend fun recordHistory(
        @Path("mediaId") mediaId: Int
    ): retrofit2.Response<Unit>

    @GET("api/user/history")
    suspend fun getHistory(
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 50
    ): PagedHistoryResult

    // --- 自建歌单 Playlists ---
    @GET("api/playlist")
    suspend fun getPlaylists(
        @Query("type") type: String = "normal"
    ): List<PlaylistSummaryDto>

    @POST("api/playlist")
    suspend fun createPlaylist(
        @Body req: CreatePlaylistRequest
    ): PlaylistSummaryDto

    @GET("api/playlist/{id}")
    suspend fun getPlaylist(
        @Path("id") id: Int
    ): PlaylistDetailDto

    @POST("api/playlist/{id}/songs")
    suspend fun addSongsToPlaylist(
        @Path("id") id: Int,
        @Body mediaFileIds: List<Int>
    ): retrofit2.Response<Unit>

    @DELETE("api/playlist/{id}/songs")
    suspend fun removeSongsFromPlaylist(
        @Path("id") id: Int,
        @Query("ids") ids: String
    ): retrofit2.Response<Unit>

    @DELETE("api/playlist/{id}")
    suspend fun deletePlaylist(
        @Path("id") id: Int
    ): retrofit2.Response<Unit>
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

    private class StaticAuthInterceptor(private val token: String?) : Interceptor {
        override fun intercept(chain: Interceptor.Chain): Response {
            val original = chain.request()
            val requestBuilder = original.newBuilder()
            if (!token.isNullOrEmpty()) {
                requestBuilder.header("Authorization", "Bearer $token")
            }
            return chain.proceed(requestBuilder.build())
        }
    }

    fun createClientWithToken(token: String?, readTimeoutSeconds: Long = 30): OkHttpClient {
        val logging = HttpLoggingInterceptor().apply {
            level = HttpLoggingInterceptor.Level.HEADERS
            redactHeader("Authorization")
            redactHeader("Cookie")
        }
        return OkHttpClient.Builder()
            .addInterceptor(StaticAuthInterceptor(token))
            .addInterceptor(logging)
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(readTimeoutSeconds, TimeUnit.SECONDS)
            .build()
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

    fun createApi(serverUrl: String, token: String?, readTimeoutSeconds: Long = 30): WebMusicApi {
        val baseUrl = if (serverUrl.endsWith("/")) serverUrl else "$serverUrl/"
        val client = createClientWithToken(token, readTimeoutSeconds)
        return Retrofit.Builder()
            .baseUrl(baseUrl)
            .client(client)
            .addConverterFactory(GsonConverterFactory.create())
            .build()
            .create(WebMusicApi::class.java)
    }

    fun createApi(tokenManager: TokenManager): WebMusicApi {
        return createApi(tokenManager.serverUrl, tokenManager.token)
    }

    /** AI 生成歌词是同步长任务，单独给足读超时（默认 30s 不够） */
    fun createLongTimeoutApi(tokenManager: TokenManager): WebMusicApi {
        return createApi(tokenManager.serverUrl, tokenManager.token, readTimeoutSeconds = 180)
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
