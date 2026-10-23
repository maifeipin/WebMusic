package com.maifeipin.webmusic.core.storage

import android.content.Context
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import java.util.Locale

class LocalMediaImporter(
    private val context: Context,
    private val db: LocalSongDatabase = LocalSongDatabase.getInstance(context)
) {

    data class ImportProgress(
        val processed: Int,
        val totalFound: Int,
        val currentTitle: String
    )

    data class ImportResult(
        val added: Int,
        val skippedDuplicate: Int,
        val unsupported: Int,
        val failed: Int
    )

    private data class FileItem(
        val uri: Uri,
        val documentId: String?,
        val authority: String?,
        val displayName: String,
        val mimeType: String?,
        val sizeBytes: Long
    )

    /**
     * 导入 SAF 授权目录树（递归抓取媒体文件，单次 query 批量游标遍历）
     */
    suspend fun importDirectoryTree(
        treeUri: Uri,
        onProgress: (ImportProgress) -> Unit = {}
    ): ImportResult = withContext(Dispatchers.IO) {
        val rootDocId = DocumentsContract.getTreeDocumentId(treeUri)
        val files = mutableListOf<FileItem>()

        crawlDirectory(treeUri, rootDocId, depth = 0, maxDepth = 4, files = files)

        processFileItems(files, onProgress)
    }

    /**
     * 导入用户通过 ACTION_OPEN_DOCUMENT 多选的离散文档
     */
    suspend fun importDocumentUris(
        uris: List<Uri>,
        onProgress: (ImportProgress) -> Unit = {}
    ): ImportResult = withContext(Dispatchers.IO) {
        val files = mutableListOf<FileItem>()

        for (uri in uris) {
            if (!isActive) break
            var displayName = uri.lastPathSegment ?: "unknown"
            var sizeBytes = 0L
            var mimeType = context.contentResolver.getType(uri)
            var docId: String? = null

            try {
                if (DocumentsContract.isDocumentUri(context, uri)) {
                    docId = DocumentsContract.getDocumentId(uri)
                }
            } catch (_: Exception) {}

            try {
                context.contentResolver.query(
                    uri,
                    arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
                    null,
                    null,
                    null
                )?.use { cursor ->
                    if (cursor.moveToFirst()) {
                        val nameIdx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                        val sizeIdx = cursor.getColumnIndex(OpenableColumns.SIZE)
                        if (nameIdx >= 0) displayName = cursor.getString(nameIdx) ?: displayName
                        if (sizeIdx >= 0) sizeBytes = cursor.getLong(sizeIdx)
                    }
                }
            } catch (_: Exception) {}

            files.add(
                FileItem(
                    uri = uri,
                    documentId = docId,
                    authority = uri.authority,
                    displayName = displayName,
                    mimeType = mimeType,
                    sizeBytes = sizeBytes
                )
            )
        }

        processFileItems(files, onProgress)
    }

    private suspend fun crawlDirectory(
        treeUri: Uri,
        parentDocId: String,
        depth: Int,
        maxDepth: Int,
        files: MutableList<FileItem>
    ) = withContext(Dispatchers.IO) {
        if (depth > maxDepth || !isActive) return@withContext

        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parentDocId)
        val projection = arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,
            DocumentsContract.Document.COLUMN_SIZE
        )

        val subDirs = mutableListOf<String>()

        try {
            context.contentResolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
                val idIdx = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                val nameIdx = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                val mimeIdx = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_MIME_TYPE)
                val sizeIdx = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_SIZE)

                while (cursor.moveToNext() && isActive) {
                    val docId = if (idIdx >= 0) cursor.getString(idIdx) else null ?: continue
                    val name = if (nameIdx >= 0) cursor.getString(nameIdx) else "" ?: ""
                    val mime = if (mimeIdx >= 0) cursor.getString(mimeIdx) else null
                    val size = if (sizeIdx >= 0) cursor.getLong(sizeIdx) else 0L

                    if (name.startsWith(".") || name.equals(".nomedia", ignoreCase = true)) {
                        continue
                    }

                    if (mime == DocumentsContract.Document.MIME_TYPE_DIR) {
                        subDirs.add(docId)
                    } else {
                        val fileDocUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, docId)
                        files.add(
                            FileItem(
                                uri = fileDocUri,
                                documentId = docId,
                                authority = treeUri.authority,
                                displayName = name,
                                mimeType = mime,
                                sizeBytes = size
                            )
                        )
                    }
                }
            }
        } catch (_: Exception) {
            // 目录不可读或底层 Provider 拒绝直接忽略
        }

        for (subDocId in subDirs) {
            if (!isActive) break
            crawlDirectory(treeUri, subDocId, depth + 1, maxDepth, files)
        }
    }

    private suspend fun processFileItems(
        items: List<FileItem>,
        onProgress: (ImportProgress) -> Unit
    ): ImportResult = withContext(Dispatchers.IO) {
        var added = 0
        var skippedDuplicate = 0
        var unsupported = 0
        var failed = 0

        val total = items.size

        for ((index, item) in items.withIndex()) {
            if (!isActive) break

            onProgress(
                ImportProgress(
                    processed = index + 1,
                    totalFound = total,
                    currentTitle = item.displayName
                )
            )

            if (!MediaImportFilter.isSupportedMedia(item.displayName, item.mimeType, item.sizeBytes)) {
                unsupported++
                continue
            }

            val uriKey = MediaImportFilter.normalizeUriKey(
                item.authority,
                item.documentId,
                item.uri.toString()
            )

            val existing = db.findByUriKey(uriKey)
            if (existing != null) {
                // 已存在时，更新其可访问 URI（应对重叠目录授权变更），不分配新 ID
                db.updateImportFilePath(uriKey, item.uri.toString())
                skippedDuplicate++
                continue
            }

            try {
                var title = item.displayName.substringBeforeLast('.').ifBlank { item.displayName }
                var artist = "未知艺术家"
                var album = "本地导入"
                var durationMs: Long? = null
                var resolvedMime = item.mimeType

                val retriever = MediaMetadataRetriever()
                try {
                    retriever.setDataSource(context, item.uri)
                    val metaTitle = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)
                    val metaArtist = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST)
                    val metaAlbum = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ALBUM)
                    val metaDuration = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)
                    val metaMime = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_MIMETYPE)

                    if (!metaTitle.isNullOrBlank()) title = metaTitle.trim()
                    if (!metaArtist.isNullOrBlank()) artist = metaArtist.trim()
                    if (!metaAlbum.isNullOrBlank()) album = metaAlbum.trim()
                    if (!metaDuration.isNullOrBlank()) durationMs = metaDuration.toLongOrNull()
                    if (!metaMime.isNullOrBlank()) resolvedMime = metaMime
                } catch (_: Exception) {
                    // 解码失败时使用降级默认属性
                } finally {
                    try {
                        retriever.release()
                    } catch (_: Exception) {}
                }

                val songId = db.allocateMonotonicLocalId()
                val durationStr = if (durationMs != null && durationMs > 0) {
                    val sec = durationMs / 1000
                    String.format(Locale.US, "%02d:%02d", sec / 60, sec % 60)
                } else {
                    "00:00"
                }

                val song = DownloadedSong(
                    serverHash = LocalSongDatabase.DEVICE_LOCAL_SCOPE,
                    userHash = LocalSongDatabase.DEVICE_LOCAL_SCOPE,
                    username = "device",
                    id = songId,
                    title = title,
                    artist = artist,
                    album = album,
                    genre = null,
                    duration = durationStr,
                    sizeBytes = item.sizeBytes,
                    localFilePath = item.uri.toString(),
                    lyrics = null,
                    coverUrl = null,
                    downloadedAt = System.currentTimeMillis(),
                    source = DownloadedSong.SOURCE_IMPORT,
                    uriKey = uriKey,
                    durationMs = durationMs,
                    mimeType = resolvedMime,
                    unavailable = false
                )

                db.insertOrUpdate(song)
                added++
            } catch (e: Exception) {
                if (e is kotlinx.coroutines.CancellationException) throw e
                failed++
            }
        }

        ImportResult(
            added = added,
            skippedDuplicate = skippedDuplicate,
            unsupported = unsupported,
            failed = failed
        )
    }
}
