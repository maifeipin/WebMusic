# NAS 媒体扫描与高质量标签方案

本文档用于后续会话复用，描述如何把 NAS 媒体文件扫描到数据库，并使用可追溯的外部证据生成高质量标签。

## 当前实现与问题

`MediaFile` 当前主要保存文件和 ID3 基础信息：`Title`、`Artist`、`Album`、`Genre`、`Year`、`Duration`、`FileHash`、`FilePath` 等。

当前扫描器读取音频自身标签；现有 Gemini `TagService` 主要用于元数据清洗和分类，不能作为“上榜”“评论过百万”“经典电影配乐”等事实的唯一来源。

现有 Genre 数据存在大小写不统一、未知值和来源水印，例如 `Unknown Genre`、`POP/pop`、`[60yp.com分享]`。正式 enrichment 前必须先清洗标准化。

## 目标

扫描 NAS 后，为每个媒体文件建立：

- 稳定的音乐身份：歌曲、艺人、专辑、发行版本的外部 ID；
- 可验证的语义标签：榜单、热门、电影配乐、经典等；
- 标签来源、证据链接、抓取时间和置信度；
- 异步处理、缓存、失败重试和人工审核能力。

AI 只能生成候选标签或整理证据，不能替代外部事实来源。

## 推荐数据模型

不要把所有信息继续写入 `MediaFile.Genre`。建议新增以下表。

### MediaIdentity

保存媒体的跨平台身份映射：

```text
MediaFileId
MusicBrainzRecordingId
MusicBrainzReleaseId
MusicBrainzArtistId
ISRC
AcoustId
SpotifyTrackId
TMDBId
IMDbId
MatchMethod
MatchConfidence
MatchedAt
```

### MediaTag

保存当前有效标签：

```text
MediaFileId
Namespace       # chart / popularity / soundtrack / cultural / genre
Key             # peak / source / role / classic 等
Value
NumericValue
Confidence
Status          # proposed / approved / rejected
CreatedAt
UpdatedAt
```

### TagEvidence

保存标签的来源证据：

```text
MediaTagId
Source          # musicbrainz / billboard / spotify / youtube / tmdb 等
SourceId
EvidenceUrl
EvidenceText
RetrievedAt
ExpiresAt
RawPayload      # JSON
```

同一个标签可以有多个来源，避免单一接口异常导致结论失真。

## 数据来源分层

### 1. 音乐身份与基础资料

- MusicBrainz：Recording、Release、Artist、发行日期和关联关系；
- AcoustID/Chromaprint：通过声纹识别歌曲，文件 Hash 不能识别不同编码的同一首歌；
- Cover Art Archive：专辑封面。

这一层用于先确认“它到底是哪首歌”，是后续所有标签的基础。

### 2. 榜单与热门程度

使用有明确来源的榜单或平台数据，例如 Billboard、Spotify 榜单、YouTube 官方数据、Last.fm 统计等。必须保存来源和时间。

示例：

```text
namespace = chart
key = peak
value = 1
source = billboard
retrieved_at = 2026-08-27
```

“热门”是时间相关属性，不应设计成永久真值。

### 3. 电影和影视配乐

使用 TMDB、IMDb、MusicBrainz、Wikidata 等交叉匹配，明确区分：

```text
soundtrack.film = Inception
soundtrack.role = featured   # 歌曲出现在影片中
soundtrack.role = score      # 配乐/原声音乐
```

不能仅凭专辑名称包含 `OST` 就认定为电影配乐。

### 4. 经典、红极一时、文化影响力

这类标签必须由多项证据综合得到，例如榜单峰值、榜单持续时间、发行年份、权威资料和平台参与度。建议保存评分依据，而不是只保存 `classic=true`。

```text
cultural.classic = true
cultural.score = 0.91
cultural.reason = "多来源榜单与权威资料一致"
```

## 处理流水线

```text
NAS 扫描
  → 原始 ID3 清洗与标准化
  → 标题/艺人匹配
  → 声纹识别（必要时）
  → 写入 MediaIdentity
  → 异步查询外部数据源
  → 生成带证据的候选标签
  → 置信度计算
  → 管理界面人工审核
  → 应用 approved 标签
```

扫描任务不应等待所有外部接口完成。建议使用后台 Job，并为每个媒体记录 `pending / processing / completed / failed` 状态。

## 置信度建议

- `0.95+`：唯一 ID 或声纹直接匹配；
- `0.80–0.95`：多个权威来源一致；
- `0.60–0.80`：单一来源或文本匹配；
- `<0.60`：只作为候选，不自动展示为事实。

外部请求必须具备 User-Agent、限流、超时、重试、缓存和原始响应保存。API 密钥放在环境变量或 GitHub Secrets，不能写入数据库和代码仓库。

## 与现有代码的衔接

- `backend/Services/ScannerService.cs`：继续负责扫描和读取 ID3，不在扫描过程中同步调用所有外部 API；
- `backend/Models/Entities.cs`：新增 Identity、Tag、Evidence 实体及迁移；
- `backend/Services/TagService.cs`：保留 Gemini，用于候选整理和元数据清洗；
- `backend/Controllers/TagsController.cs`：增加 enrichment 启动、状态查询和审核接口；
- `JobWorker`：增加批量 enrichment 队列、重试和限流；
- 前端 Tag Manager：展示标签、来源、证据 URL、置信度和审核状态。

## 分阶段实施

### Phase 1：基础治理

1. 清洗 Title、Artist、Album、Genre；
2. 合并大小写重复值；
3. 移除文件分享站水印；
4. 增加 `MediaIdentity`、`MediaTag`、`TagEvidence`。

### Phase 2：身份匹配

1. 先使用标题、艺人、专辑做确定性匹配；
2. 对疑难文件使用 AcoustID；
3. 保存匹配方法和置信度；
4. 提供人工纠错入口。

### Phase 3：外部标签

先实现 MusicBrainz 基础资料、电影配乐和榜单标签，再接入播放量、评论量等容易变化的数据。

### Phase 4：审核与展示

在媒体列表和详情页显示标签及来源；低置信度标签默认进入待审核，不直接作为正式筛选条件。

## 关键原则

1. 文件 Hash 用于判断文件重复，声纹或外部 ID 用于判断歌曲身份；
2. 外部数据必须带来源和抓取时间；
3. “热门”“评论过百万”必须是可更新的时间快照；
4. AI 产出只能作为建议；
5. 先建立身份，再做语义标签；
6. 所有外部接口都要遵守服务条款和速率限制。

