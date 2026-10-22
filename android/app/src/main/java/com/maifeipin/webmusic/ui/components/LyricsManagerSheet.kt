package com.maifeipin.webmusic.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.google.gson.JsonObject
import com.maifeipin.webmusic.core.lyrics.LyricsParser
import com.maifeipin.webmusic.core.lyrics.NeteaseCandidate
import com.maifeipin.webmusic.core.lyrics.formatDurationDelta
import com.maifeipin.webmusic.core.lyrics.formatDurationMs
import com.maifeipin.webmusic.core.lyrics.resolveSongDurationMs
import com.maifeipin.webmusic.core.lyrics.sortByDurationProximity
import com.maifeipin.webmusic.core.network.MediaFileDto
import com.maifeipin.webmusic.core.network.PluginDto
import com.maifeipin.webmusic.core.network.SaveLyricsBody
import com.maifeipin.webmusic.core.network.TokenManager
import com.maifeipin.webmusic.core.network.WebMusicNetwork
import com.maifeipin.webmusic.ui.theme.DarkSurface
import com.maifeipin.webmusic.ui.theme.GreenPrimary
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 歌词管理面板：把网页版的「网易云搜索匹配」与「AI 生成」搬到 App。
 *
 * 契约对齐 Web 端 LyricsPanel：
 * - 插件：`baseUrl` 非空、`isEnabled`、名称含 netease/网易
 * - 搜索：GET /api/plugins/{id}/proxy/search?keywords=
 * - 歌词：GET /api/plugins/{id}/proxy/lyric?id=
 * - 保存：POST /api/lyrics/{mediaId}/save，source=网易云音乐 (官方LRC)，version=netease
 * - AI：  POST /api/lyrics/{mediaId}/generate （同步长任务，需长超时）
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LyricsManagerSheet(
    song: MediaFileDto,
    tokenManager: TokenManager,
    /** 播放器已知的当前曲目时长（毫秒），用于候选列表就近排序；0 表示未知 */
    knownDurationMs: Long = 0L,
    onDismiss: () -> Unit,
    onSaved: (String) -> Unit
) {
    val scope = rememberCoroutineScope()
    val api = remember(tokenManager.token, tokenManager.serverUrl) {
        WebMusicNetwork.createApi(tokenManager)
    }
    val longTimeoutApi = remember(tokenManager.token, tokenManager.serverUrl) {
        WebMusicNetwork.createLongTimeoutApi(tokenManager)
    }

    var tab by remember { mutableIntStateOf(0) }

    // —— 网易云匹配 ——
    var neteasePlugin by remember { mutableStateOf<PluginDto?>(null) }
    var pluginLoaded by remember { mutableStateOf(false) }
    var query by remember {
        mutableStateOf(listOfNotNull(song.title, song.artist).joinToString(" ").trim())
    }
    var searching by remember { mutableStateOf(false) }
    var results by remember { mutableStateOf<List<NeteaseCandidate>>(emptyList()) }
    var previewId by remember { mutableStateOf<Long?>(null) }
    var loadingPreview by remember { mutableStateOf(false) }
    var previewLrc by remember { mutableStateOf<String?>(null) }
    var previewLabel by remember { mutableStateOf("") }
    var saving by remember { mutableStateOf(false) }

    // —— AI 生成 ——
    var aiAvailable by remember { mutableStateOf<Boolean?>(null) }
    var aiLang by remember { mutableStateOf("zh") }
    var aiPrompt by remember { mutableStateOf("") }
    var generating by remember { mutableStateOf(false) }

    var error by remember { mutableStateOf<String?>(null) }
    var info by remember { mutableStateOf<String?>(null) }

    // —— 本曲时长：优先取接口的 totalSeconds，其次 duration 字段，最后退回播放器实测值 ——
    val songDurationMs = remember(song.id, song.totalSeconds, song.duration, knownDurationMs) {
        resolveSongDurationMs(song, knownDurationMs)
    }
    // 时长最接近本曲的候选排在最前（时长未知的候选放到最后，保持原始顺序）
    val sortedResults = remember(results, songDurationMs) {
        sortByDurationProximity(results, songDurationMs)
    }
    val bestMatchId = if (songDurationMs > 0L) {
        sortedResults.firstOrNull { it.durationMs > 0L }?.id
    } else {
        null
    }

    fun loadNeteaseLyric(candidate: NeteaseCandidate) {
        val pluginId = neteasePlugin?.id ?: return
        scope.launch {
            previewId = candidate.id
            loadingPreview = true
            previewLrc = null
            previewLabel = buildString {
                append(candidate.name)
                if (candidate.artists.isNotBlank()) append(" - ").append(candidate.artists)
            }
            error = null
            try {
                val json = withContext(Dispatchers.IO) { api.getNeteaseLyric(pluginId, candidate.id) }
                val lrc = json.objOrNull("lrc")?.stringOrNull("lyric").orEmpty()
                if (lrc.isBlank()) {
                    error = "该版本没有歌词内容，换一个候选试试"
                } else {
                    previewLrc = lrc
                }
            } catch (e: Exception) {
                error = "歌词拉取失败：${e.message ?: "网络异常"}"
            } finally {
                loadingPreview = false
            }
        }
    }

    fun searchNetease(keyword: String, overridePluginId: Int? = null) {
        val pluginId = overridePluginId ?: neteasePlugin?.id ?: return
        if (keyword.isBlank()) return
        scope.launch {
            searching = true
            error = null
            info = null
            results = emptyList()
            previewLrc = null
            try {
                val json = withContext(Dispatchers.IO) { api.searchNeteaseSongs(pluginId, keyword.trim()) }
                val list = sortByDurationProximity(parseNeteaseCandidates(json), songDurationMs)
                results = list
                if (list.isEmpty()) {
                    error = "没有搜到匹配的歌曲，试试改用「歌名 歌手」再搜"
                } else {
                    // 自动预览「时长最接近」的那一版，少一次手工比对
                    loadNeteaseLyric(list.first())
                }
            } catch (e: Exception) {
                error = "搜索失败：${e.message ?: "网络异常"}"
            } finally {
                searching = false
            }
        }
    }

    // 打开面板即探测插件与 AI 服务，并自动按「歌名 + 歌手」搜一次
    LaunchedEffect(Unit) {
        var foundPlugin: PluginDto? = null
        try {
            val plugins = withContext(Dispatchers.IO) { api.getPlugins() }
            foundPlugin = plugins.firstOrNull { p ->
                !p.baseUrl.isNullOrBlank() && p.isEnabled &&
                    (p.name?.lowercase()?.contains("netease") == true || p.name?.contains("网易") == true)
            }
            neteasePlugin = foundPlugin
        } catch (e: Exception) {
            // 插件列表拉取失败时，退化为"未安装插件"提示
        } finally {
            pluginLoaded = true
        }

        try {
            aiAvailable = withContext(Dispatchers.IO) { api.getAiStatus() }.available
        } catch (e: Exception) {
            aiAvailable = false
        }

        val keyword = query
        if (foundPlugin != null && keyword.isNotBlank()) {
            searchNetease(keyword, foundPlugin.id)
        }
    }

    // 半展开只会露出两三条候选，用户还得先上滑才能看到预览/保存；这里直接全展开
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = com.maifeipin.webmusic.ui.theme.DarkBackground,
        dragHandle = { BottomSheetDefaults.DragHandle(color = Color.Gray) },
        modifier = Modifier.fillMaxHeight(0.92f)
    ) {
        // ModalBottomSheet 的内容高度是自适应的：整块内容用 verticalScroll 承载，
        // 内部候选列表给固定高度（避免 LazyColumn 在无界高度下崩），
        // 这样预览与保存按钮一定可以通过下滑到达。
        val sheetContentHeight = (LocalConfiguration.current.screenHeightDp * 0.86f).dp
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(max = sheetContentHeight)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp)
                .padding(bottom = 16.dp)
        ) {
            // 顶部：标题 + 关闭
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = "歌词管理",
                        fontSize = 18.sp,
                        fontWeight = FontWeight.Bold,
                        color = Color.White
                    )
                    Text(
                        text = buildString {
                            append(listOfNotNull(song.title, song.artist).joinToString(" • "))
                            if (songDurationMs > 0L) append(" • 本曲 ").append(formatDurationMs(songDurationMs))
                        },
                        fontSize = 12.sp,
                        color = Color.Gray,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                IconButton(onClick = onDismiss) {
                    Icon(Icons.Default.Close, contentDescription = "关闭", tint = Color.White)
                }
            }

            Spacer(modifier = Modifier.height(10.dp))

            TabRow(
                selectedTabIndex = tab,
                containerColor = com.maifeipin.webmusic.ui.theme.DarkBackground,
                contentColor = GreenPrimary
            ) {
                Tab(
                    selected = tab == 0,
                    onClick = { tab = 0 },
                    text = {
                        Text(
                            "网易云匹配",
                            fontWeight = if (tab == 0) FontWeight.Bold else FontWeight.Normal,
                            color = if (tab == 0) GreenPrimary else Color.Gray
                        )
                    }
                )
                Tab(
                    selected = tab == 1,
                    onClick = { tab = 1 },
                    text = {
                        Text(
                            "AI 生成",
                            fontWeight = if (tab == 1) FontWeight.Bold else FontWeight.Normal,
                            color = if (tab == 1) GreenPrimary else Color.Gray
                        )
                    }
                )
            }

            Spacer(modifier = Modifier.height(12.dp))

            if (tab == 0) {
                // —— 搜索行 ——
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    OutlinedTextField(
                        value = query,
                        onValueChange = { query = it },
                        modifier = Modifier.weight(1f),
                        singleLine = true,
                        label = { Text("歌名 / 歌手", fontSize = 12.sp) },
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                        enabled = neteasePlugin != null && !searching
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Button(
                        onClick = { searchNetease(query) },
                        enabled = neteasePlugin != null && !searching && query.isNotBlank(),
                        colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 10.dp)
                    ) {
                        if (searching) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(16.dp),
                                color = Color.Black,
                                strokeWidth = 2.dp
                            )
                        } else {
                            Icon(Icons.Default.Search, contentDescription = null, tint = Color.Black, modifier = Modifier.size(18.dp))
                        }
                    }
                }

                if (!pluginLoaded) {
                    LinearProgressIndicator(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = 8.dp),
                        color = GreenPrimary
                    )
                } else if (neteasePlugin == null) {
                    Text(
                        text = "服务端未启用网易云插件：在网页版「插件」里添加并启用 netease 插件后，这里就能直接搜索匹配。",
                        color = Color(0xFFFFB74D),
                        fontSize = 12.sp,
                        modifier = Modifier.padding(top = 10.dp)
                    )
                }

                if (error != null) {
                    Text(
                        text = error!!,
                        color = Color(0xFFFF6E6E),
                        fontSize = 12.sp,
                        modifier = Modifier.padding(top = 8.dp)
                    )
                }
                if (info != null) {
                    Text(
                        text = info!!,
                        color = GreenPrimary,
                        fontSize = 12.sp,
                        modifier = Modifier.padding(top = 8.dp)
                    )
                }

                // —— 候选列表（按时长与本曲的差距升序，最接近的排第一） ——
                LazyColumn(
                    modifier = Modifier
                        .fillMaxWidth()
                        // 4 行候选（约 52dp/行）——比 236dp 少半行，避免最后一条被预览卡切一半
                        .height(208.dp)
                        .padding(top = 8.dp)
                ) {
                    items(sortedResults, key = { it.id }) { candidate ->
                        val selected = previewId == candidate.id
                        val isBest = candidate.id == bestMatchId
                        val deltaMs = if (songDurationMs > 0L && candidate.durationMs > 0L) {
                            kotlin.math.abs(candidate.durationMs - songDurationMs)
                        } else {
                            null
                        }
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(8.dp))
                                .background(if (selected) DarkSurface else Color.Transparent)
                                .clickable(enabled = !loadingPreview) { loadNeteaseLyric(candidate) }
                                .padding(horizontal = 8.dp, vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Icon(
                                imageVector = if (selected && loadingPreview) Icons.Default.Downloading else Icons.Default.MusicNote,
                                contentDescription = null,
                                tint = if (selected) GreenPrimary else Color.Gray,
                                modifier = Modifier.size(18.dp)
                            )
                            Spacer(modifier = Modifier.width(10.dp))
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    text = candidate.name,
                                    color = if (selected) GreenPrimary else Color.White,
                                    fontSize = 14.sp,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                                Text(
                                    text = listOf(candidate.artists, candidate.album)
                                        .filter { it.isNotBlank() }
                                        .joinToString(" • "),
                                    color = Color.Gray,
                                    fontSize = 11.sp,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                            }
                            Spacer(modifier = Modifier.width(8.dp))
                            Column(horizontalAlignment = Alignment.End) {
                                // 时长一律显示，未知时给「--:--」，避免候选看起来完全一样
                                Text(
                                    text = formatDurationMs(candidate.durationMs),
                                    color = if (isBest) GreenPrimary else Color(0xFFB0B0B0),
                                    fontSize = 12.sp,
                                    fontWeight = if (isBest) FontWeight.Bold else FontWeight.Normal
                                )
                                if (deltaMs != null) {
                                    Text(
                                        text = if (deltaMs < 500L) "时长一致" else "差 ${formatDurationDelta(deltaMs)}",
                                        color = if (isBest) GreenPrimary else Color.Gray,
                                        fontSize = 10.sp,
                                        maxLines = 1
                                    )
                                }
                            }
                        }
                    }
                }

                // —— 歌词预览 ——
                LyricsPreviewCard(
                    label = previewLabel,
                    lrc = previewLrc,
                    modifier = Modifier.padding(top = 8.dp)
                )

                Spacer(modifier = Modifier.height(10.dp))

                Button(
                    onClick = {
                        val content = previewLrc ?: return@Button
                        scope.launch {
                            saving = true
                            error = null
                            info = null
                            try {
                                val resp = withContext(Dispatchers.IO) {
                                    api.saveLyrics(
                                        song.id,
                                        SaveLyricsBody(
                                            content = content,
                                            source = "网易云音乐 (官方LRC)",
                                            version = "netease"
                                        )
                                    )
                                }
                                when {
                                    resp.isSuccessful -> {
                                        info = "已保存到歌词库"
                                        onSaved(content)
                                    }
                                    resp.code() == 403 -> error = "当前账号不是管理员，无法写入歌词库"
                                    else -> error = "保存失败：HTTP ${resp.code()}"
                                }
                            } catch (e: Exception) {
                                error = "保存失败：${e.message ?: "网络异常"}"
                            } finally {
                                saving = false
                            }
                        }
                    },
                    modifier = Modifier.fillMaxWidth(),
                    enabled = previewLrc != null && !saving,
                    colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary)
                ) {
                    if (saving) {
                        CircularProgressIndicator(
                            modifier = Modifier.size(16.dp),
                            color = Color.Black,
                            strokeWidth = 2.dp
                        )
                        Spacer(modifier = Modifier.width(8.dp))
                    }
                    Text("保存到歌词库", color = Color.Black, fontWeight = FontWeight.Bold)
                }
            } else {
                // —— AI 生成 ——
                Column(modifier = Modifier.fillMaxWidth()) {
                    when (aiAvailable) {
                        null -> Text("正在检测 AI 服务…", color = Color.Gray, fontSize = 12.sp)
                        false -> Text(
                            text = "服务端 AI 歌词服务不可用（OCRService 未运行），暂时只能用网易云匹配。",
                            color = Color(0xFFFFB74D),
                            fontSize = 12.sp
                        )
                        true -> Text(
                            text = "由服务端 AI 直接生成 LRC 并写入歌词库，需要管理员账号；生成通常需要 10~60 秒。",
                            color = Color.Gray,
                            fontSize = 12.sp
                        )
                    }

                    Spacer(modifier = Modifier.height(12.dp))

                    Text("歌词语言", color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                    Spacer(modifier = Modifier.height(6.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf("zh" to "中文", "en" to "英文", "ja" to "日文", "ko" to "韩文").forEach { (code, label) ->
                            FilterChip(
                                selected = aiLang == code,
                                onClick = { aiLang = code },
                                label = { Text(label, fontSize = 12.sp) },
                                colors = FilterChipDefaults.filterChipColors(
                                    selectedContainerColor = GreenPrimary,
                                    selectedLabelColor = Color.Black,
                                    labelColor = Color.LightGray
                                )
                            )
                        }
                    }

                    Spacer(modifier = Modifier.height(12.dp))

                    OutlinedTextField(
                        value = aiPrompt,
                        onValueChange = { aiPrompt = it },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("补充提示（可选）", fontSize = 12.sp) },
                        placeholder = { Text("例如：粤语演唱、偏摇滚风格、需要翻译", fontSize = 12.sp) },
                        minLines = 2,
                        maxLines = 4
                    )

                    Spacer(modifier = Modifier.height(12.dp))

                    if (error != null) {
                        Text(error!!, color = Color(0xFFFF6E6E), fontSize = 12.sp)
                        Spacer(modifier = Modifier.height(8.dp))
                    }
                    if (info != null) {
                        Text(info!!, color = GreenPrimary, fontSize = 12.sp)
                        Spacer(modifier = Modifier.height(8.dp))
                    }

                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Button(
                            onClick = {
                                scope.launch {
                                    generating = true
                                    error = null
                                    info = null
                                    previewLrc = null
                                    previewLabel = "AI 生成结果"
                                    try {
                                        val resp = withContext(Dispatchers.IO) {
                                            longTimeoutApi.generateLyrics(
                                                song.id,
                                                aiLang,
                                                aiPrompt.trim().ifEmpty { null }
                                            )
                                        }
                                        val content = resp.content
                                        if (content.isNullOrBlank()) {
                                            error = "生成结果为空，请稍后重试"
                                        } else {
                                            previewLrc = content
                                            info = "已写入歌词库"
                                            onSaved(content)
                                        }
                                    } catch (e: Exception) {
                                        error = "生成失败：${e.message ?: "服务未就绪"}"
                                    } finally {
                                        generating = false
                                    }
                                }
                            },
                            enabled = aiAvailable == true && !generating,
                            colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary)
                        ) {
                            if (generating) {
                                CircularProgressIndicator(
                                    modifier = Modifier.size(16.dp),
                                    color = Color.Black,
                                    strokeWidth = 2.dp
                                )
                                Spacer(modifier = Modifier.width(8.dp))
                            } else {
                                Icon(Icons.Default.AutoAwesome, contentDescription = null, tint = Color.Black, modifier = Modifier.size(18.dp))
                                Spacer(modifier = Modifier.width(6.dp))
                            }
                            Text(
                                text = if (generating) "生成中…" else "开始生成",
                                color = Color.Black,
                                fontWeight = FontWeight.Bold
                            )
                        }
                    }

                    Spacer(modifier = Modifier.height(12.dp))

                    LyricsPreviewCard(label = previewLabel, lrc = previewLrc)
                }
            }
        }
    }
}

@Composable
private fun LyricsPreviewCard(label: String, lrc: String?, modifier: Modifier = Modifier) {
    if (lrc.isNullOrBlank()) {
        Box(
            modifier = modifier
                .fillMaxWidth()
                .height(72.dp)
                .clip(RoundedCornerShape(10.dp))
                .background(DarkSurface),
            contentAlignment = Alignment.Center
        ) {
            Text("选中候选歌曲后，这里显示歌词预览", color = Color.Gray, fontSize = 12.sp)
        }
        return
    }

    val lines = remember(lrc) { LyricsParser.parse(lrc) }
    Column(
        modifier = modifier
            .fillMaxWidth()
            .heightIn(max = 160.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(DarkSurface)
            .padding(12.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text(
                text = label,
                color = Color.White,
                fontSize = 12.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f)
            )
            Text(
                text = "${lines.size} 行",
                color = GreenPrimary,
                fontSize = 11.sp
            )
        }
        Spacer(modifier = Modifier.height(6.dp))
        // 预览只展示前几行，整块面板由外层滚动承载，避免嵌套同向滚动
        val previewLines = if (lines.isEmpty()) {
            // 非 LRC（无时间轴）时直接展示前若干行文本，避免预览空白
            lrc.lines().filter { it.isNotBlank() }.take(6)
        } else {
            lines.take(6).map { it.text }
        }
        previewLines.forEach { line ->
            Text(line, color = Color.LightGray, fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Spacer(modifier = Modifier.height(2.dp))
        }
    }
}

// Gson 的 JsonObject 没有 org.json 那套 optXxx 方法，这里用安全取值扩展兜住 null / 类型不符
private fun JsonObject.objOrNull(name: String): JsonObject? = get(name) as? JsonObject
private fun JsonObject.arrayOrNull(name: String): com.google.gson.JsonArray? = get(name) as? com.google.gson.JsonArray
private fun JsonObject.stringOrNull(name: String): String? =
    get(name)?.takeIf { !it.isJsonNull }?.asString

private fun JsonObject.longOrNull(name: String): Long? =
    get(name)?.takeIf { !it.isJsonNull }?.let { runCatching { it.asLong }.getOrNull() }

private fun parseNeteaseCandidates(root: JsonObject): List<NeteaseCandidate> {
    val songs = root.objOrNull("result")?.arrayOrNull("songs") ?: return emptyList()
    val list = ArrayList<NeteaseCandidate>(songs.size())
    for (element in songs) {
        val obj = element as? JsonObject ?: continue
        val id = obj.longOrNull("id") ?: continue
        val name = obj.stringOrNull("name") ?: "未知歌曲"
        val artistsArray = obj.arrayOrNull("ar") ?: obj.arrayOrNull("artists")
        val artists = artistsArray?.mapNotNull { artist ->
            (artist as? JsonObject)?.stringOrNull("name")
        }?.joinToString(" / ").orEmpty()
        val album = (obj.objOrNull("al") ?: obj.objOrNull("album"))?.stringOrNull("name").orEmpty()
        val durationMs = obj.longOrNull("dt") ?: obj.longOrNull("duration") ?: 0L
        list.add(NeteaseCandidate(id, name, artists, album, durationMs))
    }
    return list
}
