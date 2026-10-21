package com.maifeipin.webmusic.core.storage

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

data class DownloadedSong(
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
)

class LocalSongDatabase(context: Context) : SQLiteOpenHelper(context, DATABASE_NAME, null, DATABASE_VERSION) {

    companion object {
        private const val DATABASE_NAME = "webmusic_offline.db"
        private const val DATABASE_VERSION = 1

        private const val TABLE_NAME = "downloaded_songs"
        private const val COL_ID = "id"
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
    }

    override fun onCreate(db: SQLiteDatabase) {
        val createSql = """
            CREATE TABLE $TABLE_NAME (
                $COL_ID INTEGER PRIMARY KEY,
                $COL_TITLE TEXT NOT NULL,
                $COL_ARTIST TEXT NOT NULL,
                $COL_ALBUM TEXT NOT NULL,
                $COL_GENRE TEXT,
                $COL_DURATION TEXT,
                $COL_SIZE_BYTES INTEGER,
                $COL_FILE_PATH TEXT NOT NULL,
                $COL_LYRICS TEXT,
                $COL_COVER_URL TEXT,
                $COL_DOWNLOADED_AT INTEGER
            )
        """.trimIndent()
        db.execSQL(createSql)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        db.execSQL("DROP TABLE IF EXISTS $TABLE_NAME")
        onCreate(db)
    }

    @Synchronized
    fun insertOrUpdate(song: DownloadedSong) {
        val db = writableDatabase
        val values = ContentValues().apply {
            put(COL_ID, song.id)
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
        db.insertWithOnConflict(TABLE_NAME, null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    @Synchronized
    fun delete(id: Int): Boolean {
        val db = writableDatabase
        val rows = db.delete(TABLE_NAME, "$COL_ID = ?", arrayOf(id.toString()))
        return rows > 0
    }

    @Synchronized
    fun isDownloaded(id: Int): Boolean {
        val db = readableDatabase
        val cursor = db.rawQuery("SELECT 1 FROM $TABLE_NAME WHERE $COL_ID = ?", arrayOf(id.toString()))
        val exists = cursor.moveToFirst()
        cursor.close()
        return exists
    }

    @Synchronized
    fun getById(id: Int): DownloadedSong? {
        val db = readableDatabase
        val cursor = db.query(
            TABLE_NAME,
            null,
            "$COL_ID = ?",
            arrayOf(id.toString()),
            null,
            null,
            null
        )
        return cursor.use {
            if (it.moveToFirst()) {
                cursorToSong(it)
            } else null
        }
    }

    @Synchronized
    fun getAll(): List<DownloadedSong> {
        val db = readableDatabase
        val list = mutableListOf<DownloadedSong>()
        val cursor = db.query(TABLE_NAME, null, null, null, null, null, "$COL_DOWNLOADED_AT DESC")
        cursor.use {
            while (it.moveToNext()) {
                list.add(cursorToSong(it))
            }
        }
        return list
    }

    @Synchronized
    fun search(query: String): List<DownloadedSong> {
        val db = readableDatabase
        val list = mutableListOf<DownloadedSong>()
        val q = "%$query%"
        val cursor = db.rawQuery(
            "SELECT * FROM $TABLE_NAME WHERE $COL_TITLE LIKE ? OR $COL_ARTIST LIKE ? OR $COL_ALBUM LIKE ? ORDER BY $COL_DOWNLOADED_AT DESC",
            arrayOf(q, q, q)
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
            id = cursor.getInt(cursor.getColumnIndexOrThrow(COL_ID)),
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
