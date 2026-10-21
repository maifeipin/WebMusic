package com.maifeipin.webmusic.core.cache

import android.content.Context
import androidx.media3.database.StandaloneDatabaseProvider
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.cache.CacheDataSource
import androidx.media3.datasource.cache.LeastRecentlyUsedCacheEvictor
import androidx.media3.datasource.cache.SimpleCache
import androidx.media3.datasource.okhttp.OkHttpDataSource
import okhttp3.OkHttpClient
import java.io.File

/**
 * 流式媒体 LRU 缓存管理器 (边听边存)
 * 基于 ExoPlayer SimpleCache，默认上限 2GB，自动淘汰最旧分片
 * 听过的歌曲二次收听 0 流量秒开
 */
object MediaCacheManager {

    private const val MAX_CACHE_BYTES = 2L * 1024 * 1024 * 1024 // 2GB
    private var simpleCache: SimpleCache? = null

    @Synchronized
    fun getCache(context: Context): SimpleCache {
        if (simpleCache == null) {
            val cacheDir = File(context.cacheDir, "webmusic_media_cache")
            if (!cacheDir.exists()) {
                cacheDir.mkdirs()
            }
            val evictor = LeastRecentlyUsedCacheEvictor(MAX_CACHE_BYTES)
            val databaseProvider = StandaloneDatabaseProvider(context)
            simpleCache = SimpleCache(cacheDir, evictor, databaseProvider)
        }
        return simpleCache!!
    }

    /**
     * 包装 OkHttp 数据源，生成支持透明边听边存的 CacheDataSource.Factory
     */
    fun createCacheDataSourceFactory(
        context: Context,
        okHttpClient: OkHttpClient
    ): DataSource.Factory {
        val upstreamFactory = OkHttpDataSource.Factory(okHttpClient)
        val cache = getCache(context)
        return CacheDataSource.Factory()
            .setCache(cache)
            .setUpstreamDataSourceFactory(upstreamFactory)
            .setFlags(CacheDataSource.FLAG_IGNORE_CACHE_ON_ERROR)
    }
}
