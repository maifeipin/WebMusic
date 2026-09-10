# Local MusicBrainz 身份与外部公共评分主线

最后更新：2026-09-10

本文件是 WebMusic 的 MusicBrainz / Last.fm 主线规范。它覆盖旧方案中仍有效的身份匹配部分，并明确取代任何将资源 Worker、个人播放行为或多个外部平台混入同一流程的规划。

## 产品目标与边界

WebMusic 的核心是私有媒体库与播放器。本主线的目标是为媒体建立可复用的外部身份与公共信号基础：

```text
本地 MusicBrainz 高置信身份
  -> 标准化元数据、ISRC、MusicBrainz 社区信号
  -> Last.fm 独立公共热度
  -> /library 展示、筛选和排序
```

公共评分与本库个人行为严格分离：

- 不读取、不混入 `Favorites`、`PlayHistories`、最近播放或用户歌单；
- MusicBrainz 社区口碑与 Last.fm 热度分别保存、分别展示；
- 不在前端页面实时请求任何第三方 API，页面只读取 WebMusic 数据库快照；
- 不把来源分数伪装为统一的“官方评分”。

以下工作不属于本主线：

- Worker、租约、心跳、Provider 配额或多节点调度的扩展；
- Cover Art Archive、LRCLIB、网易、Spotify、YouTube 或其他公网资源请求；
- 自动下载或写入封面、歌词；
- 将个人播放和收藏用作公共评分；
- 对已有 Batch 1 / Batch 2 的再次处理；
- 封面、歌词和旧 Worker 与本主线的身份/评分事务混跑。

## 当前生产基线

- 本地 MusicBrainz 镜像：DSM 私网 `http://192.168.2.18:5050`；仅允许私网 IP 字面量访问，禁止 DNS 主机名、重定向和公网回退。
- 生产媒体：`MediaFiles = 114,875`。
- 已有身份：`MediaIdentities = 49`：`MusicBrainz = 19`，`MusicBrainzLocal = 30`。
- Batch 1：`LegacyPilotRound2_20260908`，10 首，状态 `LegacyApplied`，不可回滚。
- Batch 2：`Batch-20260909-44c89fc6`，20 首，状态 `Applied`，20 个 `MusicBrainzLocal` 身份已写入；创建、审批和应用操作人为 `audit_operator`。
- Batch 2 没有触发资源补全；`Lyrics = 70`、`WorkerSubmissions = 22`、`EnrichmentJobs = 6` 均不应因本主线改变。
- 已有能力：本地 MusicBrainz 检索、只读 Shadow Run、IdentityImport 人工流程、`LocalAutoEligibility:v3`、扫描状态、通用外部引用/评分表和 Library 展示。
- 名称治理：Unknown Album 前 35 页的 190 条明确音轨前缀已事务化清洗并逐条核验；1 条多空格候选继续保留人工复核。

## 为什么保留 MusicBrainz

MusicBrainz 不是热度/销量数据库，但它提供稳定的 Recording MBID、Release/Release Group、ISRC、标准化标题/艺人/时长、标签与社区评分锚点。它使 Last.fm 等后续来源可按 MBID 匹配，而不是反复进行歌名/歌手模糊搜索。

MusicBrainz 评分的含义是社区口碑，而不是流行度。Recording、Release Group 等对象可有聚合 `rating` 与 `rating_count`；原始用户评分明细不在公开 dump 中。建议计算：

```text
MB_CommunityScore = rating_0_to_5 * ln(1 + rating_count)
```

展示时必须同时显示 `rating_count`：

- `0`：没有评分，不显示数值；
- `1-2`：标记“样本极少”，不可参与默认排序；
- `>=3`：可作为 MusicBrainz 社区口碑排序依据。

Last.fm 的全站 `listeners`、`playcount` 形成独立的公共热度，不与 MusicBrainz 社区口碑合并。Last.fm 的 `track.getInfo` 支持按 Recording MBID 查询，因此只有经过高置信身份确认的媒体才可进入 Last.fm 抓取队列。

## 可扩展的数据模型

不要为每一个外部平台新增一套表。保留现有 `MediaIdentities` 作为权威身份层，并新增通用外部映射与信号层。

```text
MediaIdentities
  现有权威身份层：MusicBrainzLocal Recording MBID、置信度、审核状态。

MediaExternalReferences
  MediaFileId
  Provider              # LastFm / YouTube / Spotify / Billboard / ...
  SubjectType           # Recording / ReleaseGroup / Artist
  ExternalId
  CanonicalUrl
  MatchMethod
  MatchConfidence
  Status
  VerifiedAt
  MetadataFingerprint
  UNIQUE(MediaFileId, Provider, SubjectType)

MediaExternalSignals
  ExternalReferenceId
  SignalKey             # CommunityScore / GlobalPopularity / Playcount /
                        # Listeners / ChartRank / Rating
  RawValue
  NormalizedScore       # 同来源内标准化为 0..100，供 Library 排序
  SampleSize            # rating_count / listeners 等
  RawMetricsJson        # 记录来源原始字段，不能替代 RawValue
  AlgorithmVersion
  SourceUrl
  SourcePayloadHash
  ObservedAt
  RefreshAfter
  Status
  LastError
  UNIQUE(ExternalReferenceId, SignalKey)
```

如将来确有趋势图需求，再添加追加型 `MediaExternalSignalHistory`；不要在第一个版本提前实现。

对于 MusicBrainzLocal，可以复用 `MediaIdentities` 中的 MBID，不必重复造身份表；社区信号也可以先通过一个内部 `MediaExternalReference` 关联到现有身份。原始标准化元数据（ISRC、canonical title、artist、duration、release 信息、tags/genres）保存在该引用的元数据 JSON 或独立快照字段中，绝不自动覆盖用户的 `MediaFiles` 元数据。

## LocalIdentityAutoScanService

### 目标

实现一个边界明确的后台/CLI 扫描能力：

- 扫描没有任何已确认身份的媒体；
- 仅在本地 MusicBrainz 镜像上检索；
- 只接受严格高置信候选；
- 保存扫描状态和候选证据；
- 显式自动应用模式只接受 v3 高置信结果，并原子写入身份、规范元数据和 MB 社区信号；
- 后续新入库文件可增量扫描；输入元数据变化后可重新扫描。

### 最小状态表

新增 `MediaIdentityScanState`，唯一键为 `MediaFileId`。建议字段：

```text
MediaFileId
InputFingerprint
LastScannedAt
LastOutcome          # Matched / Unmatched / LowConfidence / Skipped / Failed
LastConfidence
RetryAfter
AttemptCount
LastError
PolicyVersion
MirrorVersion
LastReportId
```

它只负责扫描进度、冷却和可恢复性；不能存放封面、歌词、Worker 或用户行为数据。

### 保守准入策略

新增 C# `LocalIdentityAutoEligibilityPolicy`，与现有 Python 候选提取规则保持同等语义：

- 排除已有任何 `MediaIdentity` 的媒体；
- 标题/艺人必须有效，且不能是 `Unknown`；
- 候选必须包含合法 UUID MBID；
- 置信度默认 `>= 0.995`；
- 标题、艺人规范化后必须严格一致；
- 时长差默认 `<= 3 秒`；
- 排除 remix、mix、live、demo、instrumental、karaoke、cover、edit、remaster、preview、clip、ringtone 等衍生版本；
- 短音频或明显剪辑直接跳过；
- 同一 MBID 可对应媒体库中的多个合法文件，不做批内去重；
- 输入指纹变化可绕过冷却，未匹配/低置信/失败均必须有明确 `RetryAfter`。

任何与 Python 规则的差异必须写入报告，不能自行放宽。

### 命中后的本地详情快照

对于高置信候选，在同一次扫描中只请求本地镜像详情，不访问公网：

```text
/ws/2/recording/{MBID}?inc=ratings+isrcs+tags+genres+releases+release-groups+artist-credits
```

记录：Recording/Release/Release Group MBID、ISRC、canonical title/artist/duration、发行信息、tags/genres、Recording rating/rating_count、Release Group rating/rating_count、计算的社区分、镜像版本与拉取时间。

不自动覆盖 `MediaFiles` 原始标题、艺人、专辑、时长。

## Last.fm 独立公共评分

仅在 MusicBrainz 身份已确认后抓取。第一版使用 `track.getInfo` 的全站 `listeners` 与 `playcount`，按 MBID 优先匹配。保存 Last.fm 原始数值和来源 URL，并计算可解释的 0..100 分：

```text
LastFmGlobalPopularity V1 =
  100 * (0.55 * normalized_log(playcount)
       + 0.45 * normalized_log(listeners))
```

分数仅在 Last.fm 来源内部可比较。首次抓取和刷新必须低频、可缓存、可失败；不要因 Last.fm 故障阻塞身份扫描。刷新周期建议为每周，首次实现前要以实际 API 429 响应确定最终节流。

## Library 展示

`/library` 后端分页查询应仅从本库读取外部快照，并返回：

- `mbCommunityScore`、`mbRating`、`mbRatingCount`、`mbObservedAt`；
- `lastFmPopularityScore`、`lastFmListeners`、`lastFmPlaycount`、`lastFmObservedAt`；
- 评分来源和数据更新时间。

前端应：

- 分别显示“MB 社区口碑”和“Last.fm 热度”；
- 支持按任一分数排序、筛选“有评分/未评分”；
- 对小样本 MB 分显示低样本提示；
- 不显示假精确的综合分。综合公共分是未来独立且可审计的 Provider，不在第一版实施。

## 当前实施顺序

1. 完成 v3 + 名称治理后的 1,000 首只读基线复测。
2. 发布自动持久化能力；显式命令为 `local-identity-auto-scan --persist-state --apply-identities --mirror-version <version>`。
3. 先执行最多 10 首生产验收；确认身份、状态、引用和评分四层一致后，按 continuation 自动续跑全库。
4. 对历史已批准的 MB 身份运行 `external-signal-refresh --provider musicbrainz`，补齐同一通用评分模型。
5. 新入库媒体使用 `--incremental` 定时扫描；镜像或策略版本变化时自动重新评估未命中项。
6. Last.fm 保持关闭，直到 API key 安全配置并完成 10 首真实探测。

## 估算

- 自动扫描与本地 Dry Run：4-5 个工作日；
- MusicBrainz 详情/社区快照：1-2 个工作日；
- Last.fm 公共评分：2-3 个工作日；
- Library API/UI：1-2 个工作日；
- 集成测试、发布、首轮受控运行：1-2 个工作日。

核心开发已完成。按当前实测每 1,000 首约 11 分钟，114,875 首单实例理论约 21 小时；考虑重试、详情查询和数据库提交，生产预算为 24-36 小时。先保持单实例，避免无收益地扩展 Worker/配额系统。Last.fm 仅对已确认身份执行，不扫描全部媒体。

## 运行边界

- Dry Run 使用 PostgreSQL `SET TRANSACTION READ ONLY`，绝不写库。
- 自动应用每次最多 100 首，使用 Serializable 事务并锁定身份写入窗口；写前重新核验输入指纹和任何既有身份。
- 本地详情不可用时不写身份；低置信与跳过项只保存扫描状态和重试时间。
- 自动身份的 `MatchMethod` 固定为策略版本，`MirrorVersion` 与报告 SHA 保存于扫描状态，规范元数据及来源 payload hash 保存于外部引用。
- 不自动覆盖 `MediaFiles` 的标题、艺人、专辑和时长。
