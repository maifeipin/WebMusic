# 本地 MusicBrainz + Last.fm 评分编排实施规范

## 1. 目标与边界

目标是将全量媒体富化拆为三条独立队列：

```text
本地 MusicBrainz 身份匹配（全库、只读、快速）
  → 本地播放/收藏评分 + Last.fm 热度快照（受限、缓存）
  → 高分歌曲的 CAA / LRCLIB 资源补全（严格配额）
```

这样优先改善用户实际会听的歌曲，不为零播放长尾歌曲过早消耗封面和歌词 Provider 配额。

本项目不做以下事情：

- 不将 Last.fm 做成全量镜像、爬虫或多账号限流规避；
- 不把网易/QQ 插件接入自动批处理；网易继续是人工预览、确认与来源记录；
- 不在 MEDIA 暴露 MusicBrainz 的公网端口，也不与 WebMusic 业务 PostgreSQL 共用数据库卷；
- 不让低置信度身份匹配自动覆盖既有媒体字段、封面或歌词。

现有 `catalog_worker.py`、Worker 租约、审计、资产暂存和人工覆盖规则必须保留。

## 2. 目标架构

```text
                 Tailscale / 私网
Mac / NAS Worker ─────────────────────┐
                                     ▼
                              Local MusicBrainz
                                     │
MEDIA API / PostgreSQL ◄──────────────┘
        │
        ├─ EnrichmentCandidateScore / PopularitySnapshot
        │
        └─ 高分资源队列 ── Mac / NAS Worker ──► CAA / LRCLIB / Last.fm
```

### 2.1 Provider 职责

| Provider | 用途 | 是否可批量自动写入 |
| --- | --- | --- |
| LocalMusicBrainz | Recording / Release / Artist 身份候选和置信度 | 仅高置信 `MediaIdentity` |
| Last.fm | 热度、听众数、播放量、Top Tags 的时间快照 | 仅独立快照/Tag Evidence |
| CAA | 已确认 Release 的封面 | 仅补空封面 |
| LRCLIB | 已确认身份的歌词 | 仅补空歌词 |
| 网易插件 | 人工搜索、预览、确认 | 人工确认才写入 |

## 3. 交付物与私有镜像前置条件

Gemini 应新增但不自动部署以下交付物：

```text
infra/musicbrainz-mirror/.env.example
infra/musicbrainz-mirror/README.md
scripts/bootstrap_musicbrainz_mirror.sh
scripts/verify_musicbrainz_mirror.sh
```

必须复用并固定官方 `metabrainz/musicbrainz-docker` 的 tag 或 commit；不得自行拼装不受维护的 MusicBrainz / PostgreSQL / Solr Compose 文件。`bootstrap_musicbrainz_mirror.sh` 只可克隆指定上游版本、核验 revision、写入本机私有配置并输出下一步人工命令，不能自行下载、导入或启动服务。

部署目标为独立 Linux 主机（优先 VPS1、NAS 或专用 VPS），仅监听局域网/Tailscale 地址。不得占用 MEDIA 的业务 PostgreSQL、不得发布公网端口。

**硬门禁：** 当前 Worker 依赖 MusicBrainz Web Service 的搜索接口，因此 POC 必须部署官方 Web Service 与搜索索引。官方 `alt-db-only-mirror` 配置不包含网站和 Web API，只适合直接 SQL 使用，禁止作为本项目的应用接入方案。

上线镜像前需完成：

1. 持久化 PostgreSQL 卷、下载临时目录、备份目录、日志轮转与磁盘告警；
2. 一次“固定快照下载 → 校验 → 导入 → 建索引 → 查询 → 备份 → 恢复”演练；
3. 记录数据集版本、快照日期、导入耗时、库大小与查询基准；
4. 初期仅使用固定快照。Live Data Feed / 增量复制必须作为后续独立变更，不与首个 POC 混合；
5. 核验 MusicBrainz 数据许可证：核心 `mbdump` 与派生 `mbdump-derived` 的使用边界不同。仅需要身份匹配时优先最小化使用核心数据。
6. 在目标硬件完成索引基准。官方默认 PostgreSQL shared buffers 与 Solr heap 都是 2 GB；索引重建对 CPU、内存和磁盘 I/O 敏感，未完成实测前不得承诺全库扫描吞吐。

官方参考：<https://github.com/metabrainz/musicbrainz-docker>、<https://musicbrainz.org/doc/MusicBrainz_Server/Setup>、<https://musicbrainz.org/doc/MusicBrainz_Database/Download>。

## 4. 数据模型与迁移

所有改动使用 EF Core Migration。不得修改已上线迁移，新增迁移必须具备可逆 `Down`。

### 4.1 `MediaPopularitySnapshots`

一首歌、一个 Provider、一份当前热度快照：

```text
MediaFileId + Provider                         # 唯一索引
MusicBrainzRecordingId                         # 已确认身份锚点
Listeners, PlayCount                           # nullable，非永久事实
TopTagsJson                                    # 最多保存前 10 项
SourceUrl
RetrievedAt, ExpiresAt
PayloadBytes                                   # 用于 100 MB 缓存账本
```

Last.fm 快照只保存归一化的必要字段，不保存完整原始响应。全库 Last.fm 缓存总量默认硬限制为 100 MB；达到上限时只允许替换已过期的高分条目，不得继续扩大。

### 4.2 `EnrichmentCandidateScores`

作为可追溯的资源队列投影，而非覆盖 `MediaFile`：

```text
MediaFileId                                    # 主键
IdentityProvider, IdentityConfidence
InternalScore, LastFmScore, TotalScore
ScoreVersion, CalculatedAt
Eligibility                                    # Eligible / LowConfidence / Complete / Cooldown / ManualOnly
LastFmSnapshotAt
```

评分版本必须持久化；调整公式后递增 `ScoreVersion` 并可重算，不覆盖历史 Provider 审计。

### 4.3 Score V1（固定公式）

仅当有效 Title/Artist 且 LocalMusicBrainz 置信度 `>= 0.90` 时允许进入自动资源队列。

```text
收藏                         +1000
近 30 天有播放               +200
累计播放                     +min(PlayCount * 10, 500)
MusicBrainz 置信度 >= 0.95   +100
Last.fm 热度                 0..300（对 listeners / playcount 做对数归一化）
```

`MatchedWithoutAssets`、`Unmatched`、`Failed` 是不同状态：前者可在 14 天后重入资源队列；后两者必须遵守既有冷却和指纹变更规则。人工确认的网易资源优先级最高，自动 Worker 只能补空。

## 5. 队列和接口行为

### A. 本地身份扫描（`Catalog:IdentityLocal`）

- 通过私网 `LocalMusicBrainz` 查询，不访问公网 MusicBrainz；
- 不下载封面、不请求歌词、不调用 Last.fm；
- 写入 `MediaIdentity.Provider = LocalMusicBrainz`、匹配方法、置信度、数据集版本和审计；
- 高置信身份可创建/更新评分投影；低置信、未知标题、版本冲突仅记录候选，不写正式资源；
- 支持游标、断点、dry-run、每批快照和可重复运行。

### B. Last.fm 热度快照（`Catalog:PopularityLastFm`）

- 只处理 A 阶段已高置信匹配且本地评分排名靠前的候选；首轮上限 5,000 首；
- 优先用 Recording 对应的规范化 Artist/Title 查询，结果必须回写 `SourceUrl`、时间、过期时间；
- 使用一个 API key、可识别 User-Agent、顺序限速、`429` / `Retry-After` 退避；
- 不调用写接口、不抓取网页、不查询用户 Scrobble 数据；
- 过期快照才刷新，Last.fm 异常时沿用旧分数并标记 `stale`，不阻塞资源队列。

Last.fm 只作为评分证据。其 API 条款要求受限缓存、来源归属和限流合规：<https://www.last.fm/api/tos>。

### C. 高分资源补全（`Catalog:AssetsScored`）

- 从 `Eligibility=Eligible` 的评分投影按 `TotalScore DESC, MediaFileId ASC` 取歌；
- 只处理缺封面或缺歌词的歌曲；
- CAA/LRCLIB 继续由 Mac/NAS Worker 实际访问，MEDIA 只协调租约、审计和入库；
- 保持现有封面暂存、文件魔数校验、事务补偿、ProviderQuotaLedger、提交幂等和冷却机制；
- 仅在身份已确认时访问 CAA/LRCLIB，绝不把网易作为自动 fallback。

## 6. 必须修改的现有限速逻辑

部署镜像本身不会提升吞吐。Gemini 必须实现下列隔离，否则全库仍被旧公网账本卡住：

1. `MusicBrainz:Mode=local` 时，本地身份查询不预留或消耗公网 MusicBrainz 的每日 2,000 请求单位；
2. `catalog_worker.py` 的 1.5 秒节流仅适用于公网 MusicBrainz，不适用于私网镜像；本地模式改为可配置的连接并发和数据库超时；
3. CAA 与 LRCLIB 配额必须在候选选择后按实际 `needsCover` / `needsLyrics` 数量预留。不得在选歌前按 CAA 剩余额度把所有歌曲一律截断；
4. LocalMusicBrainz、Last.fm、CAA、LRCLIB 分别记录 Provider、请求数、错误率和延迟，不得混入同一个 `MusicBrainz` 账本；
5. 未配置本地镜像或健康检查失败时，`Catalog:IdentityLocal` 只能失败并告警；不得静默对 11 万首回退公网 MusicBrainz。人工单曲查询可显式选择公网模式。

## 7. 测试与验收门禁

Gemini 每阶段交付必须提供代码、Migration、测试、执行命令和 `walkthrough.md`，但不得自行提交、推送或部署。Codex 复核后才进入下一阶段。

### 必须新增的自动化测试

1. Local 模式不产生公网 MusicBrainz HTTP 请求，也不消耗其 ProviderQuotaLedger；
2. Local 模式健康检查失败时全库身份任务失败且无公网回退；
3. Score V1：收藏/近期播放/累计播放/置信度/Last.fm 分数排序准确且封顶有效；
4. 低置信、人工确认、冷却、资源完整歌曲不能进入自动资源队列；
5. Last.fm 缓存命中不发请求、过期才刷新、超过 100 MB 拒绝新增低优先级快照；
6. CAA/LRCLIB 按实际缺失资源预留额度；无封面需求的歌曲不消耗 CAA 额度；
7. PostgreSQL 集成测试覆盖本地模式、并发租约、迁移升级与回滚；
8. 前端显示评分来源、Last.fm 数据时间和四类结果：`Updated`、`MatchedWithoutAssets`、`Unmatched`、`Failed`。

### 分阶段准出

| 阶段 | 允许动作 | 准出条件 |
| --- | --- | --- |
| 0 | 当前 3×10 公网稳定性试跑 | 不改生产架构 |
| 1 | 私有镜像 POC | 私网访问、固定快照、导入/恢复演练、无生产接入 |
| 2 | 应用本地模式与数据模型 | 测试通过，feature flag 默认关闭 |
| 3 | 100 首 shadow-run | 仅记录本地与公网候选差异，不写资源，人工抽检 |
| 4 | 5,000 首评分快照 | Last.fm 缓存/限流/归属合规，无自动资源写入 |
| 5 | 高分资源小批量 | 10 → 50 → 100，逐批验收后再扩容 |

任一阶段出现误匹配、Provider `429/503`、迁移差异或缓存上限异常，立即停止该阶段，不影响已上线的手工网易与现有生产服务。

## 8. Gemini 与 Codex 协作规则

1. Gemini 只实施当前获准阶段，不提前创建生产资源、不修改 MEDIA、不提交或推送；
2. Gemini 在每阶段结束提供：`git diff`、Migration SQL、测试输出、风险说明、回滚步骤和 `walkthrough.md`；
3. Codex 复核源代码、Migration 可逆性、真实 PostgreSQL 测试和部署边界；
4. 只有用户明确授权后，才执行 Git 提交、CI、MEDIA 部署或启动 Worker；
5. 任何 API key、密码、Tailscale 凭据仅放本机/部署机 secret，禁止进入仓库、日志或验收文档。
