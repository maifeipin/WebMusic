package com.maifeipin.webmusic.core.storage

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import java.security.MessageDigest

data class DownloadedSong(
    val serverHash: String,
    val userHash: String,
    val username: String,
    val id: Int,
    val title: String,
    val artist: String,
    val album: String,
    val genre: String?,
    val duration: String?,
    val sizeBytes: Long,
    val localFilePath: String,
    val lyrics: String?,
    val coverUrl: String?,
    val downloadedAt: Long,
    val source: String = SOURCE_DOWNLOAD,
    val uriKey: String? = null,
    val durationMs: Long? = null,
    val mimeType: String? = null,
    val unavailable: Boolean = false
) {
    companion object {
        const val SOURCE_DOWNLOAD = "download"
        const val SOURCE_IMPORT = "import"
    }

    fun isVideo(): Boolean {
        if (mimeType?.startsWith("video/") == true) return true
        val path = localFilePath.lowercase()
        return path.endsWith(".mp4") || path.endsWith(".mkv") || path.endsWith(".webm") ||
               path.endsWith(".avi") || path.endsWith(".mov") || path.endsWith(".flv") ||
               path.endsWith(".ts") || path.endsWith(".m4v")
    }
}

class LocalSongDatabase(context: Context) : SQLiteOpenHelper(context, DATABASE_NAME, null, DATABASE_VERSION) {

    companion object {
        private const val DATABASE_NAME = "webmusic_offline.db"
        private const val DATABASE_VERSION = 4

        const val TABLE_NAME = "downloaded_songs"
        const val COL_SERVER_HASH = "server_hash"
        const val COL_USER_HASH = "user_hash"
        const val COL_USERNAME = "username"
        const val COL_SONG_ID = "song_id"
        const val COL_TITLE = "title"
        const val COL_ARTIST = "artist"
        const val COL_ALBUM = "album"
        const val COL_GENRE = "genre"
        const val COL_DURATION = "duration"
        const val COL_SIZE_BYTES = "size_bytes"
        const val COL_FILE_PATH = "file_path"
        const val COL_LYRICS = "lyrics"
        const val COL_COVER_URL = "cover_url"
        const val COL_DOWNLOADED_AT = "downloaded_at"

        // v4 导入扩展字段
        const val COL_SOURCE = "source"
        const val COL_URI_KEY = "uri_key"
        const val COL_DURATION_MS = "duration_ms"
        const val COL_MIME_TYPE = "mime_type"
        const val COL_UNAVAILABLE = "unavailable"

        // 负数 ID 序列表
        const val TABLE_LOCAL_ID_SEQ = "local_id_seq"

        // 本机导入专属设备命名空间 sha256("__device_local__")
        val DEVICE_LOCAL_SCOPE: String by lazy { computeServerHash("__device_local__") }

        @Volatile
        private var instance: LocalSongDatabase? = null

        fun getInstance(context: Context): LocalSongDatabase {
            return instance ?: synchronized(this) {
                instance ?: LocalSongDatabase(context.applicationContext).also { instance = it }
            }
        }

        fun computeServerHash(serverUrl: String): String {
            val md = MessageDigest.getInstance("SHA-256")
            val bytes = md.digest(serverUrl.trimEnd('/').toByteArray(Charsets.UTF_8))
            return bytes.joinToString("") { "%02x".format(it) }
        }

        fun computeUserHash(username: String?): String {
            val raw = username?.trim()?.ifEmpty { "anon" } ?: "anon"
            val md = MessageDigest.getInstance("SHA-256")
            val bytes = md.digest(raw.toByteArray(Charsets.UTF_8))
            return bytes.joinToString("") { "%02x".format(it) }
        }
    }

    override fun onCreate(db: SQLiteDatabase) {
        val createSql = """
            CREATE TABLE $TABLE_NAME (
                $COL_SERVER_HASH TEXT NOT NULL,
                $COL_USER_HASH TEXT NOT NULL,
                $COL_USERNAME TEXT NOT NULL,
                $COL_SONG_ID INTEGER NOT NULL,
                $COL_TITLE TEXT NOT NULL,
                $COL_ARTIST TEXT NOT NULL,
                $COL_ALBUM TEXT NOT NULL,
                $COL_GENRE TEXT,
                $COL_DURATION TEXT,
                $COL_SIZE_BYTES INTEGER,
                $COL_FILE_PATH TEXT NOT NULL,
                $COL_LYRICS TEXT,
                $COL_COVER_URL TEXT,
                $COL_DOWNLOADED_AT INTEGER,
                $COL_SOURCE TEXT NOT NULL DEFAULT '${DownloadedSong.SOURCE_DOWNLOAD}',
                $COL_URI_KEY TEXT,
                $COL_DURATION_MS INTEGER,
                $COL_MIME_TYPE TEXT,
                $COL_UNAVAILABLE INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY ($COL_SERVER_HASH, $COL_USER_HASH, $COL_SONG_ID)
            )
        """.trimIndent()
        db.execSQL(createSql)
        db.execSQL("CREATE UNIQUE INDEX IF NOT EXISTS idx_local_uri ON $TABLE_NAME ($COL_URI_KEY)")
        db.execSQL("CREATE INDEX IF NOT EXISTS idx_source ON $TABLE_NAME ($COL_SOURCE)")

        // 负数 ID 独立序列表（单行约束，初始值为 -1，限定负数）
        db.execSQL("""
            CREATE TABLE IF NOT EXISTS $TABLE_LOCAL_ID_SEQ (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                next_val INTEGER NOT NULL CHECK (next_val < 0)
            )
        """.trimIndent())
        db.execSQL("INSERT OR IGNORE INTO $TABLE_LOCAL_ID_SEQ VALUES (1, -1)")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        if (oldVersion < 2) {
            // v1 -> v4: 清理旧版本未隔离的物理文件，防止产生不可管理的孤儿垃圾文件
            try {
                val cursor = db.rawQuery("SELECT file_path FROM $TABLE_NAME", null)
                cursor.use {
                    val pathIdx = it.getColumnIndex("file_path")
                    if (pathIdx >= 0) {
                        while (it.moveToNext()) {
                            val path = it.getString(pathIdx)
                            if (!path.isNullOrEmpty()) {
                                val file = java.io.File(path)
                                if (file.exists()) file.delete()
                            }
                        }
                    }
                }
            } catch (e: Exception) {
                // 如果旧表不存在或读取失败，继续升级流程
            }
            db.execSQL("DROP TABLE IF EXISTS $TABLE_NAME")
            onCreate(db)
            return
        }

        if (oldVersion == 2) {
            // v2 -> v3: 迁移增加 user_hash 列并重构复合主键 (server_hash, user_hash, song_id)
            db.beginTransaction()
            val tempTable = "${TABLE_NAME}_v3"
            try {
                db.execSQL("DROP TABLE IF EXISTS $tempTable")
                val createSql = """
                    CREATE TABLE $tempTable (
                        $COL_SERVER_HASH TEXT NOT NULL,
                        $COL_USER_HASH TEXT NOT NULL,
                        $COL_USERNAME TEXT NOT NULL,
                        $COL_SONG_ID INTEGER NOT NULL,
                        $COL_TITLE TEXT NOT NULL,
                        $COL_ARTIST TEXT NOT NULL,
                        $COL_ALBUM TEXT NOT NULL,
                        $COL_GENRE TEXT,
                        $COL_DURATION TEXT,
                        $COL_SIZE_BYTES INTEGER,
                        $COL_FILE_PATH TEXT NOT NULL,
                        $COL_LYRICS TEXT,
                        $COL_COVER_URL TEXT,
                        $COL_DOWNLOADED_AT INTEGER,
                        PRIMARY KEY ($COL_SERVER_HASH, $COL_USER_HASH, $COL_SONG_ID)
                    )
                """.trimIndent()
                db.execSQL(createSql)

                val cursor = db.rawQuery("SELECT * FROM $TABLE_NAME", null)
                cursor.use {
                    while (it.moveToNext()) {
                        val sHash = it.getString(it.getColumnIndexOrThrow(COL_SERVER_HASH))
                        val uName = it.getString(it.getColumnIndexOrThrow(COL_USERNAME))
                        val uHash = computeUserHash(uName)
                        val sId = it.getInt(it.getColumnIndexOrThrow(COL_SONG_ID))
                        val title = it.getString(it.getColumnIndexOrThrow(COL_TITLE))
                        val artist = it.getString(it.getColumnIndexOrThrow(COL_ARTIST))
                        val album = it.getString(it.getColumnIndexOrThrow(COL_ALBUM))
                        val genre = it.getString(it.getColumnIndexOrThrow(COL_GENRE))
                        val duration = it.getString(it.getColumnIndexOrThrow(COL_DURATION))
                        val sizeBytes = it.getLong(it.getColumnIndexOrThrow(COL_SIZE_BYTES))
                        val filePath = it.getString(it.getColumnIndexOrThrow(COL_FILE_PATH))
                        val lyrics = it.getString(it.getColumnIndexOrThrow(COL_LYRICS))
                        val coverUrl = it.getString(it.getColumnIndexOrThrow(COL_COVER_URL))
                        val downloadedAt = it.getLong(it.getColumnIndexOrThrow(COL_DOWNLOADED_AT))

                        val cv = ContentValues().apply {
                            put(COL_SERVER_HASH, sHash)
                            put(COL_USER_HASH, uHash)
                            put(COL_USERNAME, uName)
                            put(COL_SONG_ID, sId)
                            put(COL_TITLE, title)
                            put(COL_ARTIST, artist)
                            put(COL_ALBUM, album)
                            put(COL_GENRE, genre)
                            put(COL_DURATION, duration)
                            put(COL_SIZE_BYTES, sizeBytes)
                            put(COL_FILE_PATH, filePath)
                            put(COL_LYRICS, lyrics)
                            put(COL_COVER_URL, coverUrl)
                            put(COL_DOWNLOADED_AT, downloadedAt)
                        }
                        db.insert(tempTable, null, cv)
                    }
                }

                db.execSQL("DROP TABLE $TABLE_NAME")
                db.execSQL("ALTER TABLE $tempTable RENAME TO $TABLE_NAME")
                db.setTransactionSuccessful()
            } finally {
                if (db.inTransaction()) {
                    db.endTransaction()
                }
            }
        }

        if (oldVersion < 4) {
            // v3 -> v4: 平滑追加本机导入扩展字段与独立序列表
            try {
                db.execSQL("ALTER TABLE $TABLE_NAME ADD COLUMN $COL_SOURCE TEXT NOT NULL DEFAULT '${DownloadedSong.SOURCE_DOWNLOAD}'")
            } catch (_: Exception) {}
            try {
                db.execSQL("ALTER TABLE $TABLE_NAME ADD COLUMN $COL_URI_KEY TEXT")
            } catch (_: Exception) {}
            try {
                db.execSQL("ALTER TABLE $TABLE_NAME ADD COLUMN $COL_DURATION_MS INTEGER")
            } catch (_: Exception) {}
            try {
                db.execSQL("ALTER TABLE $TABLE_NAME ADD COLUMN $COL_MIME_TYPE TEXT")
            } catch (_: Exception) {}
            try {
                db.execSQL("ALTER TABLE $TABLE_NAME ADD COLUMN $COL_UNAVAILABLE INTEGER NOT NULL DEFAULT 0")
            } catch (_: Exception) {}

            db.execSQL("CREATE UNIQUE INDEX IF NOT EXISTS idx_local_uri ON $TABLE_NAME ($COL_URI_KEY)")
            db.execSQL("CREATE INDEX IF NOT EXISTS idx_source ON $TABLE_NAME ($COL_SOURCE)")

            db.execSQL("""
                CREATE TABLE IF NOT EXISTS $TABLE_LOCAL_ID_SEQ (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    next_val INTEGER NOT NULL CHECK (next_val < 0)
                )
            """.trimIndent())
            db.execSQL("INSERT OR IGNORE INTO $TABLE_LOCAL_ID_SEQ VALUES (1, -1)")
        }
    }

    /**
     * 单调递减分配负数 ID，保证首个 ID 为 -1，并在生命周期内永不复用。
     * 若数值达到 Int.MIN_VALUE 下溢阈值则抛出异常拒绝分配。
     */
    @Synchronized
    fun allocateMonotonicLocalId(): Int {
        val db = writableDatabase
        db.beginTransaction()
        try {
            var nextVal = -1
            val cursor = db.rawQuery("SELECT next_val FROM $TABLE_LOCAL_ID_SEQ WHERE id = 1", null)
            cursor.use {
                if (it.moveToFirst()) {
                    nextVal = it.getInt(0)
                }
            }
            if (nextVal <= Int.MIN_VALUE + 10) {
                throw IllegalStateException("本地音频负数 ID 序列已耗尽: $nextVal")
            }
            val assignedId = nextVal
            val nextToStore = nextVal - 1
            val cv = ContentValues().apply { put("next_val", nextToStore) }
            db.update(TABLE_LOCAL_ID_SEQ, cv, "id = 1", null)
            db.setTransactionSuccessful()
            return assignedId
        } finally {
            if (db.inTransaction()) {
                db.endTransaction()
            }
        }
    }

    @Synchronized
    fun insertOrUpdate(song: DownloadedSong) {
        val db = writableDatabase
        val values = ContentValues().apply {
            put(COL_SERVER_HASH, song.serverHash)
            put(COL_USER_HASH, song.userHash)
            put(COL_USERNAME, song.username)
            put(COL_SONG_ID, song.id)
            put(COL_TITLE, song.title)
            put(COL_ARTIST, song.artist)
            put(COL_ALBUM, song.album)
            put(COL_GENRE, song.genre)
            put(COL_DURATION, song.duration)
            put(COL_SIZE_BYTES, song.sizeBytes)
            put(COL_FILE_PATH, song.localFilePath)
            put(COL_LYRICS, song.lyrics)
            put(COL_COVER_URL, song.coverUrl)
            put(COL_DOWNLOADED_AT, song.downloadedAt)
            put(COL_SOURCE, song.source)
            put(COL_URI_KEY, song.uriKey)
            put(COL_DURATION_MS, song.durationMs)
            put(COL_MIME_TYPE, song.mimeType)
            put(COL_UNAVAILABLE, if (song.unavailable) 1 else 0)
        }
        val rowId = db.insertWithOnConflict(TABLE_NAME, null, values, SQLiteDatabase.CONFLICT_REPLACE)
        if (rowId == -1L) {
            throw android.database.SQLException("写入离线歌曲数据库失败 (rowId == -1, songId=${song.id})")
        }
    }

    /**
     * 根据规范化 uri_key 查找已导入的歌曲
     */
    @Synchronized
    fun findByUriKey(uriKey: String): DownloadedSong? {
        val db = readableDatabase
        val cursor = db.query(
            TABLE_NAME,
            null,
            "$COL_URI_KEY = ?",
            arrayOf(uriKey),
            null,
            null,
            null
        )
        return cursor.use {
            if (it.moveToFirst()) cursorToSong(it) else null
        }
    }

    /**
     * 当文件重新授权或路径变更时，更新其可访问 URI
     */
    @Synchronized
    fun updateImportFilePath(uriKey: String, newPath: String): Boolean {
        val db = writableDatabase
        val cv = ContentValues().apply {
            put(COL_FILE_PATH, newPath)
            put(COL_UNAVAILABLE, 0)
        }
        val rows = db.update(TABLE_NAME, cv, "$COL_URI_KEY = ?", arrayOf(uriKey))
        return rows > 0
    }

    /**
     * 精确删除单条记录（使用完整的复合主键）
     */
    @Synchronized
    fun deleteExact(serverHash: String, userHash: String, songId: Int): Boolean {
        val db = writableDatabase
        val rows = db.delete(
            TABLE_NAME,
            "$COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SONG_ID = ?",
            arrayOf(serverHash, userHash, songId.toString())
        )
        return rows > 0
    }

    @Synchronized
    fun delete(serverHash: String, userHash: String, songId: Int): Boolean {
        return deleteExact(serverHash, userHash, songId)
    }

    @Synchronized
    fun isDownloaded(serverHash: String, userHash: String, songId: Int): Boolean {
        val db = readableDatabase
        val cursor = db.rawQuery(
            """
            SELECT 1 FROM $TABLE_NAME 
            WHERE (($COL_SERVER_HASH = ? AND $COL_USER_HASH = ?) 
               OR ($COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SOURCE = '${DownloadedSong.SOURCE_IMPORT}'))
              AND $COL_SONG_ID = ?
            """.trimIndent(),
            arrayOf(serverHash, userHash, DEVICE_LOCAL_SCOPE, DEVICE_LOCAL_SCOPE, songId.toString())
        )
        val exists = cursor.moveToFirst()
        cursor.close()
        return exists
    }

    /**
     * 批量查询本地文件路径，合并当前账号下载与设备级导入，供播放队列使用。
     * 带严格双翼约束，避免未限定的 OR 影响。
     */
    @Synchronized
    fun getLocalPathsWithImports(serverHash: String, userHash: String, songIds: List<Int>): Map<Int, String> {
        if (songIds.isEmpty()) return emptyMap()
        val db = readableDatabase
        val result = HashMap<Int, String>(songIds.size)
        val deviceScope = DEVICE_LOCAL_SCOPE
        for (chunk in songIds.distinct().chunked(500)) {
            val placeholders = chunk.joinToString(",") { "?" }
            val args = ArrayList<String>(chunk.size + 4)
            args.add(serverHash)
            args.add(userHash)
            args.add(deviceScope)
            args.add(deviceScope)
            chunk.forEach { args.add(it.toString()) }
            val cursor = db.rawQuery(
                """
                SELECT $COL_SONG_ID, $COL_FILE_PATH FROM $TABLE_NAME 
                WHERE (($COL_SERVER_HASH = ? AND $COL_USER_HASH = ?) 
                   OR ($COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SOURCE = '${DownloadedSong.SOURCE_IMPORT}'))
                  AND $COL_SONG_ID IN ($placeholders)
                """.trimIndent(),
                args.toTypedArray()
            )
            cursor.use {
                val idIdx = it.getColumnIndexOrThrow(COL_SONG_ID)
                val pathIdx = it.getColumnIndexOrThrow(COL_FILE_PATH)
                while (it.moveToNext()) {
                    result[it.getInt(idIdx)] = it.getString(pathIdx)
                }
            }
        }
        return result
    }

    @Synchronized
    fun getLocalPaths(serverHash: String, userHash: String, songIds: List<Int>): Map<Int, String> {
        return getLocalPathsWithImports(serverHash, userHash, songIds)
    }

    @Synchronized
    fun updateLyrics(serverHash: String, userHash: String, songId: Int, lyrics: String?): Boolean {
        val db = writableDatabase
        val values = ContentValues().apply { put(COL_LYRICS, lyrics) }
        val rows = db.update(
            TABLE_NAME,
            values,
            """
            (($COL_SERVER_HASH = ? AND $COL_USER_HASH = ?) 
             OR ($COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SOURCE = '${DownloadedSong.SOURCE_IMPORT}'))
            AND $COL_SONG_ID = ?
            """.trimIndent(),
            arrayOf(serverHash, userHash, DEVICE_LOCAL_SCOPE, DEVICE_LOCAL_SCOPE, songId.toString())
        )
        return rows > 0
    }

    @Synchronized
    fun getByIdWithImports(serverHash: String, userHash: String, songId: Int): DownloadedSong? {
        val db = readableDatabase
        val cursor = db.rawQuery(
            """
            SELECT * FROM $TABLE_NAME 
            WHERE (($COL_SERVER_HASH = ? AND $COL_USER_HASH = ?) 
               OR ($COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SOURCE = '${DownloadedSong.SOURCE_IMPORT}'))
              AND $COL_SONG_ID = ?
            """.trimIndent(),
            arrayOf(serverHash, userHash, DEVICE_LOCAL_SCOPE, DEVICE_LOCAL_SCOPE, songId.toString())
        )
        return cursor.use {
            if (it.moveToFirst()) cursorToSong(it) else null
        }
    }

    @Synchronized
    fun getById(serverHash: String, userHash: String, songId: Int): DownloadedSong? {
        return getByIdWithImports(serverHash, userHash, songId)
    }

    @Synchronized
    fun getAllWithImports(serverHash: String, userHash: String): List<DownloadedSong> {
        val db = readableDatabase
        val list = mutableListOf<DownloadedSong>()
        val cursor = db.rawQuery(
            """
            SELECT * FROM $TABLE_NAME 
            WHERE ($COL_SERVER_HASH = ? AND $COL_USER_HASH = ?) 
               OR ($COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SOURCE = '${DownloadedSong.SOURCE_IMPORT}') 
            ORDER BY $COL_DOWNLOADED_AT DESC
            """.trimIndent(),
            arrayOf(serverHash, userHash, DEVICE_LOCAL_SCOPE, DEVICE_LOCAL_SCOPE)
        )
        cursor.use {
            while (it.moveToNext()) {
                list.add(cursorToSong(it))
            }
        }
        return list
    }

    @Synchronized
    fun getAll(serverHash: String, userHash: String): List<DownloadedSong> {
        return getAllWithImports(serverHash, userHash)
    }

    @Synchronized
    fun searchWithImports(serverHash: String, userHash: String, query: String): List<DownloadedSong> {
        val db = readableDatabase
        val list = mutableListOf<DownloadedSong>()
        val q = "%$query%"
        val cursor = db.rawQuery(
            """
            SELECT * FROM $TABLE_NAME 
            WHERE (($COL_SERVER_HASH = ? AND $COL_USER_HASH = ?) 
               OR ($COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SOURCE = '${DownloadedSong.SOURCE_IMPORT}'))
              AND ($COL_TITLE LIKE ? OR $COL_ARTIST LIKE ? OR $COL_ALBUM LIKE ?) 
            ORDER BY $COL_DOWNLOADED_AT DESC
            """.trimIndent(),
            arrayOf(serverHash, userHash, DEVICE_LOCAL_SCOPE, DEVICE_LOCAL_SCOPE, q, q, q)
        )
        cursor.use {
            while (it.moveToNext()) {
                list.add(cursorToSong(it))
            }
        }
        return list
    }

    @Synchronized
    fun search(serverHash: String, userHash: String, query: String): List<DownloadedSong> {
        return searchWithImports(serverHash, userHash, query)
    }

    private fun cursorToSong(cursor: android.database.Cursor): DownloadedSong {
        val srcIdx = cursor.getColumnIndex(COL_SOURCE)
        val uriKeyIdx = cursor.getColumnIndex(COL_URI_KEY)
        val durMsIdx = cursor.getColumnIndex(COL_DURATION_MS)
        val mimeIdx = cursor.getColumnIndex(COL_MIME_TYPE)
        val unavailIdx = cursor.getColumnIndex(COL_UNAVAILABLE)

        return DownloadedSong(
            serverHash = cursor.getString(cursor.getColumnIndexOrThrow(COL_SERVER_HASH)),
            userHash = cursor.getString(cursor.getColumnIndexOrThrow(COL_USER_HASH)),
            username = cursor.getString(cursor.getColumnIndexOrThrow(COL_USERNAME)),
            id = cursor.getInt(cursor.getColumnIndexOrThrow(COL_SONG_ID)),
            title = cursor.getString(cursor.getColumnIndexOrThrow(COL_TITLE)),
            artist = cursor.getString(cursor.getColumnIndexOrThrow(COL_ARTIST)),
            album = cursor.getString(cursor.getColumnIndexOrThrow(COL_ALBUM)),
            genre = cursor.getString(cursor.getColumnIndexOrThrow(COL_GENRE)),
            duration = cursor.getString(cursor.getColumnIndexOrThrow(COL_DURATION)),
            sizeBytes = cursor.getLong(cursor.getColumnIndexOrThrow(COL_SIZE_BYTES)),
            localFilePath = cursor.getString(cursor.getColumnIndexOrThrow(COL_FILE_PATH)),
            lyrics = cursor.getString(cursor.getColumnIndexOrThrow(COL_LYRICS)),
            coverUrl = cursor.getString(cursor.getColumnIndexOrThrow(COL_COVER_URL)),
            downloadedAt = cursor.getLong(cursor.getColumnIndexOrThrow(COL_DOWNLOADED_AT)),
            source = if (srcIdx >= 0 && !cursor.isNull(srcIdx)) cursor.getString(srcIdx) else DownloadedSong.SOURCE_DOWNLOAD,
            uriKey = if (uriKeyIdx >= 0 && !cursor.isNull(uriKeyIdx)) cursor.getString(uriKeyIdx) else null,
            durationMs = if (durMsIdx >= 0 && !cursor.isNull(durMsIdx)) cursor.getLong(durMsIdx) else null,
            mimeType = if (mimeIdx >= 0 && !cursor.isNull(mimeIdx)) cursor.getString(mimeIdx) else null,
            unavailable = if (unavailIdx >= 0 && !cursor.isNull(unavailIdx)) cursor.getInt(unavailIdx) == 1 else false
        )
    }
}
