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
    val downloadedAt: Long
) {
    fun isVideo(): Boolean {
        val path = localFilePath.lowercase()
        return path.endsWith(".mp4") || path.endsWith(".mkv") || path.endsWith(".webm") ||
               path.endsWith(".avi") || path.endsWith(".mov") || path.endsWith(".flv") ||
               path.endsWith(".ts") || path.endsWith(".m4v")
    }
}

class LocalSongDatabase(context: Context) : SQLiteOpenHelper(context, DATABASE_NAME, null, DATABASE_VERSION) {

    companion object {
        private const val DATABASE_NAME = "webmusic_offline.db"
        private const val DATABASE_VERSION = 3

        private const val TABLE_NAME = "downloaded_songs"
        private const val COL_SERVER_HASH = "server_hash"
        private const val COL_USER_HASH = "user_hash"
        private const val COL_USERNAME = "username"
        private const val COL_SONG_ID = "song_id"
        private const val COL_TITLE = "title"
        private const val COL_ARTIST = "artist"
        private const val COL_ALBUM = "album"
        private const val COL_GENRE = "genre"
        private const val COL_DURATION = "duration"
        private const val COL_SIZE_BYTES = "size_bytes"
        private const val COL_FILE_PATH = "file_path"
        private const val COL_LYRICS = "lyrics"
        private const val COL_COVER_URL = "cover_url"
        private const val COL_DOWNLOADED_AT = "downloaded_at"

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
                PRIMARY KEY ($COL_SERVER_HASH, $COL_USER_HASH, $COL_SONG_ID)
            )
        """.trimIndent()
        db.execSQL(createSql)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        if (oldVersion < 2) {
            // v1 -> v3: 清理旧版本未隔离的物理文件，防止产生不可管理的孤儿垃圾文件
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
                val expectedRows: Int
                var migratedRows = 0
                cursor.use {
                    expectedRows = it.count
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
                        val rowId = db.insertOrThrow(tempTable, null, cv)
                        if (rowId == -1L) {
                            throw android.database.SQLException("写入迁移临时表失败 (rowId == -1, songId=$sId)")
                        }
                        migratedRows++
                    }
                }

                if (migratedRows != expectedRows) {
                    throw android.database.SQLException("数据迁移行数不一致: 预期 $expectedRows 行，实际完成 $migratedRows 行")
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
        }
        val rowId = db.insertWithOnConflict(TABLE_NAME, null, values, SQLiteDatabase.CONFLICT_REPLACE)
        if (rowId == -1L) {
            throw android.database.SQLException("写入离线歌曲数据库失败 (rowId == -1, songId=${song.id})")
        }
    }

    @Synchronized
    fun delete(serverHash: String, userHash: String, songId: Int): Boolean {
        val db = writableDatabase
        val rows = db.delete(
            TABLE_NAME,
            "$COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SONG_ID = ?",
            arrayOf(serverHash, userHash, songId.toString())
        )
        return rows > 0
    }

    @Synchronized
    fun isDownloaded(serverHash: String, userHash: String, songId: Int): Boolean {
        val db = readableDatabase
        val cursor = db.rawQuery(
            "SELECT 1 FROM $TABLE_NAME WHERE $COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SONG_ID = ?",
            arrayOf(serverHash, userHash, songId.toString())
        )
        val exists = cursor.moveToFirst()
        cursor.close()
        return exists
    }

    /**
     * 批量查询已离线的本地文件路径，供播放队列"本地优先"使用。
     * SQLite 的 IN 参数上限为 999，这里按 500 分块，避免大歌单一次绑定几百上千个 id 时报错。
     */
    @Synchronized
    fun getLocalPaths(serverHash: String, userHash: String, songIds: List<Int>): Map<Int, String> {
        if (songIds.isEmpty()) return emptyMap()
        val db = readableDatabase
        val result = HashMap<Int, String>(songIds.size)
        for (chunk in songIds.distinct().chunked(500)) {
            val placeholders = chunk.joinToString(",") { "?" }
            val args = ArrayList<String>(chunk.size + 2)
            args.add(serverHash)
            args.add(userHash)
            chunk.forEach { args.add(it.toString()) }
            val cursor = db.rawQuery(
                "SELECT $COL_SONG_ID, $COL_FILE_PATH FROM $TABLE_NAME " +
                    "WHERE $COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SONG_ID IN ($placeholders)",
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

    /** 歌词在另外的界面被更新后，同步刷新已离线曲目的本地歌词副本 */
    @Synchronized
    fun updateLyrics(serverHash: String, userHash: String, songId: Int, lyrics: String?): Boolean {
        val db = writableDatabase
        val values = ContentValues().apply { put(COL_LYRICS, lyrics) }
        val rows = db.update(
            TABLE_NAME,
            values,
            "$COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SONG_ID = ?",
            arrayOf(serverHash, userHash, songId.toString())
        )
        return rows > 0
    }

    @Synchronized
    fun getById(serverHash: String, userHash: String, songId: Int): DownloadedSong? {
        val db = readableDatabase
        val cursor = db.query(
            TABLE_NAME,
            null,
            "$COL_SERVER_HASH = ? AND $COL_USER_HASH = ? AND $COL_SONG_ID = ?",
            arrayOf(serverHash, userHash, songId.toString()),
            null,
            null,
            null
        )
        return cursor.use {
            if (it.moveToFirst()) cursorToSong(it) else null
        }
    }

    @Synchronized
    fun getAll(serverHash: String, userHash: String): List<DownloadedSong> {
        val db = readableDatabase
        val list = mutableListOf<DownloadedSong>()
        val cursor = db.query(
            TABLE_NAME,
            null,
            "$COL_SERVER_HASH = ? AND $COL_USER_HASH = ?",
            arrayOf(serverHash, userHash),
            null,
            null,
            "$COL_DOWNLOADED_AT DESC"
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
        val db = readableDatabase
        val list = mutableListOf<DownloadedSong>()
        val q = "%$query%"
        val cursor = db.rawQuery(
            """
            SELECT * FROM $TABLE_NAME 
            WHERE $COL_SERVER_HASH = ? AND $COL_USER_HASH = ? 
            AND ($COL_TITLE LIKE ? OR $COL_ARTIST LIKE ? OR $COL_ALBUM LIKE ?) 
            ORDER BY $COL_DOWNLOADED_AT DESC
            """.trimIndent(),
            arrayOf(serverHash, userHash, q, q, q)
        )
        cursor.use {
            while (it.moveToNext()) {
                list.add(cursorToSong(it))
            }
        }
        return list
    }

    private fun cursorToSong(cursor: android.database.Cursor): DownloadedSong {
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
            downloadedAt = cursor.getLong(cursor.getColumnIndexOrThrow(COL_DOWNLOADED_AT))
        )
    }
}
