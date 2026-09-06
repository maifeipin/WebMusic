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

---

## 全量媒体库推进计划（生产执行版）

本节把上述设计落实为可执行的全量计划。当前曲库约 11.5 万首，不能把所有文件一次性发给外部服务；必须按风险、价值和外部限流分层处理。

### 首批验证结论

2026-09 的收藏曲目验证范围为 70 首“缺封面或歌词”的收藏歌曲：

- 20 首成功补齐至少一项资源；
- 新增 11 张封面、18 条歌词；
- 26 首因匹配置信度不足而安全跳过；
- 6 首身份匹配成功，但开放源没有可补资源；
- 3 首基础标题/艺人缺失，未尝试外部查询；
- MusicBrainz 曾出现临时 `503`，因此任务必须支持 `Retry-After`/退避重试和仅失败项重跑。

这说明“高阈值自动写入 + 审计记录”可行，但全量任务的核心不是吞吐量，而是避免把错误元数据传播到 11 万条记录。

### 总体分层与优先级

| 队列 | 范围 | 自动写入 | 处理方式 | 目标 |
| --- | --- | --- | --- | --- |
| P0 | 收藏、播放历史高频、歌单歌曲 | 仅缺失封面/歌词 | 高置信度匹配后写入 | 最先改善实际体验 |
| P1 | 标题和艺人完整、缺封面或歌词的普通歌曲 | 仅缺失字段 | 后台分片执行 | 建立稳定覆盖率 |
| P2 | 标题/艺人有乱码、Unknown、文件名可解析的歌曲 | 不直接改正式字段 | 生成待审核候选 | 修复脏数据 |
| P3 | 无标签、无可靠文件名、纯音乐、现场版、翻唱版 | 不自动写入 | 声纹、STT 或人工处理 | 降低误匹配 |
| P4 | 全量榜单、影视配乐、文化标签 | 写独立 Tag/Evidence 表 | 离线快照和人工审核 | 形成高质量知识库 |

每轮只运行一个优先级队列。P0 通过人工抽检后才能升至 P1；P1 达到稳定成功率后再处理 P2/P3。

### 数据库演进

现有 `MusicEnrichments` 是一次处理审计表。全量实施前，新增下列表，并通过正式 EF Migration 管理，禁止继续依赖 `EnsureCreated` 的临时建表：

```text
MediaIdentity
  MediaFileId, Provider, RecordingId, ReleaseId, ArtistId, ISRC, AcoustId,
  MatchMethod, Confidence, Status, MatchedAt, LastVerifiedAt

EnrichmentJob
  Id, Scope, RequestedByUserId, Total, Processed, Updated, Unmatched,
  Skipped, Failed, Status, StartedAt, FinishedAt, Cursor

EnrichmentAttempt
  JobId, MediaFileId, Provider, RequestKey, HTTPStatus, Outcome,
  Confidence, RetryCount, Detail, CreatedAt

MediaTag / TagEvidence
  按本文前半部分模型保存正式标签、证据、来源和快照时间
```

关键约束：

1. `MediaIdentity` 对 `(Provider, RecordingId)` 加唯一索引；
2. `EnrichmentAttempt` 只追加、不覆盖，便于诊断与回滚；
3. `MediaTag` 正式生效前必须有 `Status=approved`；
4. 远端歌词、封面、原始响应要保存 Provider、URL、版本、抓取时间；
5. 不把第三方请求结果、访问令牌或用户 SMB 密码写入审计表。

### 匹配决策树

```text
读取嵌入标签 / 文件名
  ├─ Title + Artist + Duration 完整
  │    └─ MusicBrainz 搜索并计算综合置信度
  │         ├─ >= 0.90：可补缺失封面、歌词；写审计
  │         ├─ 0.75–0.89：保存候选，等待人工确认
  │         └─ < 0.75：标记 Unmatched，不修改媒体
  ├─ 仅文件名可解析
  │    └─ 解析出候选元数据，进入 P2 审核队列
  └─ 标题/艺人缺失或冲突
       └─ P3：Chromaprint/AcoustID；仍不确定时使用 STT 辅助或人工处理
```

综合置信度建议：标题相似度 55%、艺人相似度 35%、时长差异 10%。任一条件不满足（标题/艺人 < 0.85，或时长相差 > 10 秒）直接禁止自动写入。现场版、混音、翻唱、伴奏和多艺人合作默认提高阈值到 0.95。

### 外部服务策略

1. **MusicBrainz**：基础身份主源。请求间隔至少 1.5 秒；HTTP `429/503` 优先遵守 `Retry-After`，否则冷却 20 秒后仅重试一次。连续三次失败时暂停整个 Provider 队列，而不是继续冲击服务。
2. **Cover Art Archive**：只在已获得 MusicBrainz Release ID 后请求。下载封面须校验 MIME 类型、最大 5 MB，并保存到 `/app/data/covers`，数据库仅保存本地 API URL。
3. **LRCLIB**：只给已通过身份匹配的歌曲查询；优先同步歌词，普通歌词也可保存但须标记 `plain`。404 是“无结果”，不是失败。
4. **Chromaprint/AcoustID**：部署到能访问音频文件的 Mac/NAS worker。只处理 P3 和人工发起的疑难曲；音频指纹不依赖文件编码，适合去重与识别。
5. **STT**：只作兜底。截取首尾片段，得到文本后与候选歌词比较；不能仅凭转写直接覆盖歌名、艺人或歌词。
6. **Last.fm**：用于补充“热度、Top Tags、用户标签、历史周榜”等标签证据，不用于音乐身份主匹配、音频流或封面下载。个人/非商业曲库可免费申请 API Key；商业、研究或超出默认限额的用途必须事先联系 Last.fm 获得书面许可。需缓存响应、遵守其动态限流、公开展示时标注并链接 Last.fm，且缓存的 Last.fm 数据总量默认不超过 100 MB。将 `api_key` 存入 worker 的机密配置，绝不返回给前端或写入审计日志。
7. **网易/QQ 等插件**：只能作为人工候选和展示来源，不能作为无授权全量抓取的核心数据源。

### 批量调度与容量控制

全量任务应在 VPS/MEDIA 之外的 worker 运行，推荐 Mac（可访问 STT、Netease 和 NAS）或专用 VPS worker；MEDIA 仅保留 API、数据库和任务状态。

建议初始配置：

```text
每批：100 首
并发：1 个 MusicBrainz worker；封面/歌词最多 2 个并发
请求节流：MusicBrainz >= 1.5 秒/请求
Last.fm：独立低优先级队列；缓存命中优先，按其响应限额动态降速
失败退避：20 秒、60 秒，随后暂停 Provider
断点：每处理 1 首持久化 Job.Cursor
运行窗口：每日 01:00–07:00，避开用户使用高峰
每日上限：2,000 首基础匹配；P3 声纹最多 200 首
```

以 1.5 秒/首估算，单个 MusicBrainz worker 每日有效处理约 2,000–3,000 首；11.5 万首基础扫描需要约 6–8 周。该速度有意保守，以保护公共服务并保持可控质量。

### 每个批次的执行步骤

1. 创建 `EnrichmentJob`，冻结本批 MediaFile ID 列表，避免扫描期间新增文件混入。
2. 预检：去掉 `._*`、`.DS_Store`、不支持格式、无可访问源和重复 FileHash。
3. 执行身份匹配，并持久化每次请求的结果、状态码与置信度。
4. 仅对 `approved/auto-approved` 身份补齐封面、歌词；不改现有非空字段。
5. 生成批次报告：处理数、成功数、Provider 错误率、匹配置信度分布、Top 失败原因。
6. 若 Provider 503/429 比例超过 5%，自动暂停，次日或冷却后从 Cursor 恢复。
7. 每批随机抽检至少 20 首自动匹配结果；误匹配超过 1% 时停止下一批并提高阈值。

### 人工审核界面

Tag Manager / Admin 页需要增加：

- 待审核列表：本地标题/艺人/时长、候选条目、封面、外部链接、匹配分数；
- 三个动作：接受候选、拒绝候选、手工搜索/绑定；
- 批次报告与 Provider 健康状态；
- “仅重试临时失败”按钮，绝不默认重跑 Unmatched；
- 每首歌的 enrichment 历史和回滚到处理前字段；
- `dry-run` 模式：只写 Attempt，不写 CoverArt/Lyric/Identity。

### 标签与榜单的第二阶段

身份覆盖率稳定后再启动。每条热门、榜单、影视配乐、经典等标签都写入 `MediaTag + TagEvidence`，而不是 `Genre`：

```text
chart.lastfm.global.weekly.rank = 3
chart.lastfm.global.week = 2025-07-12
popularity.lastfm.listeners = 123456
tag.lastfm.user = indie
soundtrack.tmdb.movie = <tmdb-id>
cultural.classic.score = 0.91
```

榜单应按周做快照；“热门”需设置过期时间；“经典”必须保留评分规则及至少两个独立来源。Last.fm 条目必须保存来源 URL、抓取时间和 API 版本，并在前端展示 Last.fm 署名与链接；不要将 Last.fm 的图片、音频或大规模原始响应写入本地库。AI 仅可总结已有证据，不能生成无来源事实标签。

### 上线、备份和回滚

1. 每次 schema 变更先在本地备份恢复库验证，再上线到 MEDIA；
2. 每批开始前依赖现有每日 PostgreSQL dump，额外记录本批开始时间；
3. 不修改 NAS 原始音频标签或文件名，直到人工批准“写回文件”功能；
4. 回滚只删除本批新增的本地封面/歌词和 Identity/Tag 记录，保留审计历史；
5. 后台任务必须可在容器重启后通过 `EnrichmentJob.Cursor` 恢复，不能只存内存队列。

### 里程碑与验收标准

| 阶段 | 交付物 | 通过标准 |
| --- | --- | --- |
| A：稳定试点 | 收藏/P0 任务、审计、重试 | 自动写入误匹配 < 1%，Provider 失败可定向重试 |
| B：P1 扩容 | 可恢复 Job、队列限流、日报 | 连续 7 天无重复写入、无任务丢失 |
| C：身份库 | MediaIdentity、AcoustID worker、审核页 | 高置信度身份覆盖 >= 70% |
| D：质量标签 | Tag/Evidence、榜单快照、影视关联 | 每个正式标签均可追溯到证据 |
| E：全量治理 | P2/P3 审核、重复合并、质量报表 | Unknown/乱码元数据持续下降且可回滚 |

### 下一步建议

先完成 A 阶段的两个缺口：将 Job 状态从内存迁移到数据库，以及在重启后恢复未完成批次。随后以 P0/P1 每日 2,000 首的额度运行一周，审查误匹配和 Provider 健康指标，再决定是否部署 Chromaprint worker 与启动 P2/P3。
