# Android 本机媒体导入离线库 —— 设计与落地定稿

状态：**定稿待编码（已通过两轮深度复核，修正 5 项设计缺陷与边界条件）**。  
基线代码：`406e5fa feat(android): add MV video tab, video player, and offline download support`。  
用途：指导 P0 阶段编码实现与端到端验收。

---

## 1. 需求与边界

让 App 能把**本机其它目录**（音乐、录音、SD 卡、以及用户选定的文档目录）里的音视频文件纳展现有的「离线音乐」数据库中：

1. **统一视界**：在「离线音乐」Tab 里与网络下载歌曲并存，展现、搜索、连播、播放历史一致。
2. **纯粹离线**：不依赖 WebMusic 后端服务，断网状态下完全可用。
3. **安全第一**：零数据拷贝（原地引用），**严禁误删用户物理文件**。
4. **非目标（本轮坚决不做）**：
   - 不上传文件至服务器曲库，不修改文件 ID3 / Vorbis 标签；
   - 不做跨服务端的跨库模糊同名歌曲合并（留待后续版本）；
   - 不常驻后台扫描全盘；
   - 不在 P0 做歌词/封面网络反查（P2 再通过本地标题+艺人匹配抓取）。

---

## 2. 核心架构决策与修正（定稿）

### D1. 存储策略：原地引用，严禁复制
- **策略**：仅在数据库中记录持久化 URI / 路径，不向 App 私有目录拷贝媒体文件。
- **依据**：无损曲库（FLAC/DSD/高码率 MP3）体积庞大，复制会导致手机存储双倍占用且耗费大量 IO 与电量。主流专业播放器（Poweramp、VLC、USB Audio Player PRO）均采用原地索引。
- **边界**：原文件被第三方文件管理器移动、重命名或 SD 卡拔出时，URI 将失效。P0 保证失效时不崩溃、不自动删记录、错误可解释；P1 补充「重新定位」与「目录失效提示」。

### D2. 入口体系：SAF 目录树为主，多选文档为补充
- **主入口**：`ACTION_OPEN_DOCUMENT_TREE` 授权选定目录，调用 `contentResolver.takePersistableUriPermission` 获取长期读取权限。
- **补充入口**：`ACTION_OPEN_DOCUMENT`（`EXTRA_ALLOW_MULTIPLE = true`）。
  - *原因*：Android 11+（API 30+）Scoped Storage 严格禁止 SAF 树形选择存储根目录、`Download` 根目录、以及 `Android/data` / `Android/obb`。当用户想导入位于 `Download` 目录中的单个或多个文件时，目录树无法选择，必须通过 `ACTION_OPEN_DOCUMENT` 多选文件作为兜底方案。
- **授权容错**：持久化授权必须处理权限被用户在系统设置中撤销、ROM 重启后失效或存储卷卸载等异常。

### D3. 唯一身份与单调递减 ID（修正版）
- **废弃**：不得使用 `URI.hashCode() % 2^31`（存在生日碰撞导致曲目互相覆盖的风险），亦不得使用简单的 `SELECT MIN(song_id)-1`（删除最小行后重新导入会发生 ID 复用，导致播放队列与状态混淆）。
- **序列表设计**：在数据库生命周期内保证**单调递减、永不复用**，且增加单行约束与下溢检查：
  ```sql
  CREATE TABLE IF NOT EXISTS local_id_seq (
      id INTEGER PRIMARY KEY CHECK (id = 1),
      next_val INTEGER NOT NULL CHECK (next_val < 0)
  );
  INSERT OR IGNORE INTO local_id_seq VALUES (1, -1);
  ```
- **分配逻辑**（必须在单个事务中完成）：
  1. `SELECT next_val FROM local_id_seq WHERE id = 1;`
  2. 校验 `next_val > Int.MIN_VALUE`（防止负整数溢出回正数；若耗尽抛出异常拒绝导入）；
  3. `val assignedId = next_val;`
  4. `UPDATE local_id_seq SET next_val = next_val - 1 WHERE id = 1;`
  5. 插入导入记录，使用 `assignedId`。
- *生命周期说明*：“永不复用”限定在当前 App 数据未被清除的生命周期内；清除数据/重装会重建数据库。

### D4. 归属空间：设备级命名空间与严格括号查询
- **空间定义**：导入曲目属于本机设备，切换 WebMusic 账号后依然可见。
  - 固定命名空间：`server_hash = user_hash = sha256("__device_local__")`。
  - 标识字段：`source = 'import'`。
- **查询范围严格约束（严禁无括号拼接 OR）**：
  ```sql
  -- 1. 列表合并查询（当前账号下载 + 本机导入）
  SELECT * FROM downloaded_songs
  WHERE (server_hash = :serverHash AND user_hash = :userHash)
     OR (server_hash = :deviceScope AND user_hash = :deviceScope AND source = 'import')
  ORDER BY downloaded_at DESC;

  -- 2. 单曲/路径查询（song_id 必须约束两翼，防止误匹配所有导入行）
  SELECT * FROM downloaded_songs
  WHERE (
      (server_hash = :serverHash AND user_hash = :userHash)
      OR (server_hash = :deviceScope AND user_hash = :deviceScope AND source = 'import')
  ) AND song_id = :songId;
  ```
- **写操作约束**：删除、更新歌词、更新不可用状态等写操作，**必须使用查询到的实体完整复合主键 `(server_hash, user_hash, song_id)`**，严禁使用列表合并的 `OR` 范围执行写操作。

### D5. 去重机制：SAF 文档标识与规范化键
- **去重依据**：不能直接把完整的树形 URI 当作强唯一键。同一个文件通过父目录授权与子目录授权分别导入时，URI 中的 `tree` 路径可能不同，但指向同一个文档。
- **规范化 `uri_key` 格式**：
  - SAF 文档统一以 `authority + ":" + documentId` 提取规范化键（例如 `com.android.externalstorage.documents:primary:Music/song.flac`）。
  - 直接文件路径以绝对路径规范化字符串为键。
  - 建立唯一索引：`CREATE UNIQUE INDEX IF NOT EXISTS idx_local_uri ON downloaded_songs (uri_key);`
  - 存储分离：`uri_key` 用于去重匹配；`local_file_path` 存当前可用的访问 URI（若二次重叠导入同一文件，更新 `local_file_path` 为最新可访问 URI，但不分配新 ID）。
- *边界*：跨不同 Provider（如 SAF 与 MediaStore、第三方 ContentProvider）不保证能识别为同一物理文件。

### D6. 删除语义（P0 彻底收敛，剔除物理删除）
- **P0 唯一行为**：**「从离线库移除」**。
  - 仅删除 `downloaded_songs` 表中对应的数据行。
  - **绝不调用** `File.delete()` 或 `DocumentsContract.deleteDocument()`，绝对保留用户物理文件。
  - 弹窗与 Toast 明确提示：“已从离线库移除，本地文件保留在原位置”。
- **生命周期澄清**：用户卸载 App 或在系统设置中「清除数据」时，外部原文件仍然保留在手机存储中；但 App 私有数据库记录被清空，持久化 URI 授权会被 Android 系统收回，重装后需重新选择目录导入。

---

## 3. 播放与数据源链路设计（定稿）

### 3.1 现状与根本问题
在基线版本中：
- 音频：`Media3PlayerAdapter` 将 ExoPlayer 的上游死锁为 `CacheDataSource(upstream = OkHttpDataSource)`。
  - `OkHttpDataSource` 遇到 `file://` 或 `content://` 必抛 `IllegalArgumentException`。
  - `CacheDataSource` 无法通过将 `cacheKeyFactory` 设为返回 `null` 来绕过缓存（Media3 1.3.1 会抛断言错误与 NPE）。
- 视频：`VideoPlayerDialog` 直接使用裸 `ExoPlayer.Builder(context).build()`（默认 `DefaultDataSource`），因此视频原生支持 `file://`。

### 3.2 解决方案：最外层 `DefaultDataSource` 路由架构
将 `DefaultDataSource` 置于最外层，充当协议分发路由器；将原有的 `CacheDataSource`（包装了 OkHttp）仅作为 HTTP/HTTPS 协议的上游委托：

```kotlin
// MediaCacheManager.kt
fun createRoutedDataSourceFactory(
    context: Context,
    okHttpClient: OkHttpClient
): DataSource.Factory {
    // 1. 网络请求专用的边播边存 LRU 缓存数据源
    val httpCacheFactory = createCacheDataSourceFactory(context, okHttpClient)
    
    // 2. 最外层 DefaultDataSource：
    //    - file:// -> FileDataSource (零开销，原生 seek)
    //    - content:// -> ContentDataSource (原生 AssetFileDescriptor)
    //    - asset:// -> AssetDataSource
    //    - http(s):// -> httpCacheFactory (进入 SimpleCache -> OkHttpDataSource)
    return DefaultDataSource.Factory(context.applicationContext, httpCacheFactory)
}
```

### 3.3 MediaItem 组装约定
- 在 `MainActivity.kt:buildQueueMediaItem` 中：
  - 本地导入/下载文件（`localPath != null`）：
    - 必须使用 `toPlayableUri()` 生成 `content://` 或 `file://` URI；
    - **严禁调用 `.setCustomCacheKey(...)`**（本地媒体不参与任何流式缓存命名）；
  - 在线流媒体（`localPath == null`）：
    - 维持现有逻辑，设置 `generateSafeCacheKey(...)`。

---

## 4. 数据模型变更（SQLite v3 → v4）

### 4.1 升级原则
- **坚决不重建表**：采用平滑且幂等的 `ALTER TABLE` 语句。
- **向后兼容**：
  - 新安装：直接在 `onCreate` 中创建包含新列的完整 v4 表及序列表；
  - v3 → v4：仅执行 5 句 `ALTER`、2 句索引、1 句创建序列表；
  - v2 → v4：先经由原有的 v2→v3 逻辑将表结构升级为复合主键，再执行 v3→v4 的字段追加；
  - v1（oldVersion < 2）：保留原破坏性清理未隔离旧文件的老逻辑，不影响现有用户。

### 4.2 迁移 SQL（v3 → v4）
```sql
-- 字段扩充
ALTER TABLE downloaded_songs ADD COLUMN source TEXT NOT NULL DEFAULT 'download';
ALTER TABLE downloaded_songs ADD COLUMN uri_key TEXT;
ALTER TABLE downloaded_songs ADD COLUMN duration_ms INTEGER;
ALTER TABLE downloaded_songs ADD COLUMN mime_type TEXT;
ALTER TABLE downloaded_songs ADD COLUMN unavailable INTEGER NOT NULL DEFAULT 0;

-- 索引构建
CREATE UNIQUE INDEX IF NOT EXISTS idx_local_uri ON downloaded_songs (uri_key);
CREATE INDEX IF NOT EXISTS idx_source ON downloaded_songs (source);

-- 负数 ID 独立单行序列表（严格限制单行，初值 -1，限制负数）
CREATE TABLE IF NOT EXISTS local_id_seq (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    next_val INTEGER NOT NULL CHECK (next_val < 0)
);
INSERT OR IGNORE INTO local_id_seq VALUES (1, -1);
```

---

## 5. 导入流水线与性能优化细节

### 5.1 目录遍历（避开 `DocumentFile.listFiles()` 陷阱）
- **性能陷阱**：千万不要使用 AndroidX `DocumentFile.fromTreeUri(...).listFiles()` 递归大目录。`DocumentFile` 对每个文件都要做多次 IPC 查询，几百个文件就会卡顿数十秒。
- **高效批量遍历**：直接使用 `ContentResolver.query()` 结合 `DocumentsContract.buildChildDocumentsUriUsingTree`：
  ```kotlin
  val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(
      treeUri,
      parentDocumentId
  )
  val projection = arrayOf(
      DocumentsContract.Document.COLUMN_DOCUMENT_ID,
      DocumentsContract.Document.COLUMN_DISPLAY_NAME,
      DocumentsContract.Document.COLUMN_MIME_TYPE,
      DocumentsContract.Document.COLUMN_SIZE,
      DocumentsContract.Document.COLUMN_LAST_MODIFIED
  )
  context.contentResolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
      // 批量读取 Cursor 游标，性能提高数十倍
  }
  ```
  *(注：具体响应速度仍受系统底层 Provider 与存储介质性能制约，必须跑在 `Dispatchers.IO`)*。

### 5.2 流水线控制
1. **白名单校验**：扩展名（`.mp3`, `.flac`, `.wav`, `.m4a`, `.aac`, `.ogg`, `.opus`, `.mp4`, `.mkv` 等）；MIME 排除图片、文档及 `.nomedia`；单文件上限 2GB。
2. **规范化查重**：提取 `authority:documentId`，若已存在于 `uri_key` 唯一索引中，计入 `skippedDuplicate`，跳过写入。
3. **轻量元数据抽取**：
   - 优先通过 `MediaMetadataRetriever` 读取 title/artist/album/duration。
   - 若抽取异常（格式损坏或系统解码器不支持），降级回退：文件名去后缀作为 title，目录名作为 album，artist 标记为 "未知艺术家"，时长置为 0。
4. **单事务分批入库**：每扫描 20 首或结束时，在 SQLite 事务中完成 ID 分配与批量 `INSERT`，通过 UI 回调刷新导入进度。
5. **可取消机制**：用户返回或切换页面时，导入 Coroutine 立即取消；已入库的曲目保留，下次导入同一目录时天然命中去重逻辑，秒级跳过。

---

## 6. 列表性能与失效检测（P0 边界收敛）

### 6.1 Compose 列表稳定性
- 在 `MainActivity.kt:1840` 渲染离线列表时，**必须指定稳定的 key**：
  ```kotlin
  itemsIndexed(
      items = filteredOffline,
      key = { _, song -> "${song.serverHash}_${song.userHash}_${song.id}" }
  ) { index, song -> ... }
  ```
- 搜索框输入增加 300ms 协程防抖，避免高频字符变更导致连续触发大列表过滤。
- *实事求是*：稳定 key 与防抖能显著避免画面跳动与卡顿，但无法消除超大列表的映射成本。P0 不引入复杂的 Paging 3，真机实测 1000~3000 首确保流畅。

### 6.2 失效检测的分期落地
- **P0 目标（可解释、不崩溃）**：
  - 播放时捕获本地文件读取异常；
  - 若 `FileNotFoundException` 或 SAF 权限失效，弹出友好提示（“文件已被移动或删除”），**绝对不自动删除该条目**，播放器停止并安全切回就绪状态。
- **P1 目标（视觉感知与治理）**：
  - 离线列表展示时异步检测并标记「失效」角标；
  - 提供「重新定位文件」或「批量清理无效记录」的操作入口。

---

## 7. 逐文件改动清单（11 个文件）

| 文件路径 | 改动核心与关键安全点 |
| --- | --- |
| `core/storage/LocalSongDatabase.kt` | 库版本升至 4；实现 v3→v4 ALTER 迁移与序列表初始化；`DownloadedSong` 扩展 5 个字段；新增带括号约束的 `getAllWithImports`、`getByIdWithImports`、`allocateMonotonicLocalId`、`insertImportSong`。 |
| `core/cache/MediaCacheManager.kt` | 实现最外层 `createRoutedDataSourceFactory`（DefaultDataSource 包装 CacheDataSource），解除 local scheme 异常。 |
| `core/player/Media3PlayerAdapter.kt` | 接入 `createRoutedDataSourceFactory`。 |
| `core/storage/LocalMediaImporter.kt`（新） | 核心导入器：SAF 目录树与多选文档遍历、ContentResolver 批量查询、元数据提取、事务写入、进度回调与取消。 |
| `core/storage/MediaImportFilter.kt`（新） | 纯逻辑函数：白名单判断、`authority:documentId` 规范化、汇总文案格式化（纯 JVM 单测覆盖）。 |
| `core/storage/OfflineDownloadManager.kt` | `deleteDownload` 按 `source` 隔离，导入曲目绝对不调物理删除；`getAllDownloaded` 接入导入合并查询；`toMediaFileDto` 识别 `mimeType` 与负数 ID。 |
| `ui/MainActivity.kt` | ① 离线 Tab 顶部增加「导入本机音乐」入口与进度浮层；② `buildQueueMediaItem` 本地文件去除 customCacheKey；③ `playQueue` 适配本地 URI；④ `fetchLyricsSafely` 与 `recordHistory` 对 `id < 0` 提前 return；⑤ 列表 `itemsIndexed` 补充唯一 key 与搜索防抖。 |
| `ui/components/SongDownloadAction.kt` | 对 `source == "import"` 的曲目隐藏下载图标（已经是本地媒体）。 |
| `ui/components/OfflineSongActionDialog.kt`（新/改） | 针对导入曲目，操作项为「从离线库移除」，文案明确标注不会删除手机文件。 |
| `core/storage/AutoOfflineCache.kt` | 确认保留 `if (song.id <= 0) return` 保护逻辑，无需改动。 |
| `docs/ANDROID_APP_DESIGN_AND_ROADMAP.md` | 同步记录 Local Media Import P0 架构设计。 |

---

## 8. 端到端验证矩阵

### 8.1 自动化 JVM 单元测试（CI 自动运行）
1. `MediaImportFilterTest`：扩展名判定（支持 flac/mp3/m4a/mp4/mkv，拒绝 txt/pdf/apk/nomedia）、文件大小门限。
2. `LocalMediaIdTest`：单调递减验证（连续调用产出 -1, -2, -3）、边界下溢拒绝验证（`Int.MIN_VALUE` 保护）。
3. `PlayableUriTest`：`toPlayableUri()` 对 `content://`, `file://`, `/data/...` 及网络相对路径的解析准确性。
4. `ImportSummaryTest`：导入结果汇总文案格式化验证（新增、跳过、不支持、失败）。

### 8.2 真机真实验收矩阵（必须在真实手机上执行）
| 序号 | 场景 | 操作步骤 | 预期结果 |
| :--- | :--- | :--- | :--- |
| **T01** | **本地文件音频播放验证（核心疑点）** | 手机断网（飞行模式）+ 确保无网络缓存，导入一首本地无损 FLAC 并点击播放。 | 正常出声、进度条拖动与 seek 正常，Logcat 无 scheme 异常。 |
| **T02** | **数据库升级验证** | 手机保留现有 v3 数据库，覆盖安装 v4 APK。 | 升级顺畅，原有已下载音乐 100% 完好，`local_id_seq` 表成功创建。 |
| **T03** | **SAF 目录导入与性能** | 选取包含 100~300 首歌曲的本地目录进行导入。 | 进度条平滑推进，结束后离线列表数量正确增加，不卡死界面。 |
| **T04** | **重叠/重复导入去重** | 对已导入的目录再次执行导入，或通过上级目录重新导入同一批文件。 | 全部计入「已存在」，数据库总行数不增加，不分配新 ID。 |
| **T05** | **移除曲目安全验证（第一号风险）** | 在离线列表中长按或点击菜单移除一首导入的音乐。 | 数据库记录删除，列表中消失；**打开手机系统文件管理器，该物理文件毫发无损**。 |
| **T06** | **多账号隔离与共享** | 退出当前登录账号，切换到另一个账号（或清空登录态）。 | 原账号下载歌曲隔离，**导入的本机歌曲依然完整可见并可正常播放**。 |
| **T07** | **负数 ID 请求隔离** | 播放导入曲目（ID < 0），观察 Logcat。 | 不向后端触发 `history/-1` 或 `lyrics/-1` 请求，无静默噪音。 |
| **T08** | **中断与取消** | 在导入大目录过程中点击取消或返回键。 | 导入协程立即安全退出，已写入的部分歌曲保留可用，下次导入秒级跳过已入库项。 |
| **T09** | **多选文档兜底导入** | 在受限目录（如 Download）通过 `ACTION_OPEN_DOCUMENT` 多选 3 个音频文件导入。 | 文件正常导入离线列表，播放与移除行为一致。 |
| **T10** | **文件失效鲁棒性** | 在文件管理器中将原文件重命名或移动，回到 App 播放该曲目。 | 提示“文件已被移动或删除”，App 界面不崩溃，离线记录不被误删。 |

---

## 9. 复核定稿结论

本方案已经全面融合第二轮复核要求，彻底消除了 SQL 约束漏洞、括号作用域漏洞、跨目录重复去重缺陷、数据源缓存副作用以及误删原文件的风险。

**方案定稿完成，可以正式进入 P0 阶段的实施编码。**
