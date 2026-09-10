# AI 执行清单：LocalIdentityAutoScanService + Last.fm 公共评分

本清单可直接交给新的 AI。执行前必须先阅读：

- `docs/LOCAL_MUSICBRAINZ_LASTFM_ORCHESTRATION.md`
- `backend/Services/LocalMusicBrainzService.cs`
- `backend/Services/LocalMusicBrainzShadowRunner.cs`
- `backend/Services/IdentityImportService.cs`
- `scripts/musicbrainz/extract_shadow_run_candidates.py`

## 当前主线与边界（2026-09-10）

- Phase 1 的只读验证已完成；当前进入 Phase 2：本地 MB 高置信身份、规范元数据和 MB 社区评分的受控自动持久化。
- 单次事务最多 100 首，首次上线先执行小批生产验收；验收通过后允许按 continuation 自动续跑，无需每批人工确认。
- 自动写入必须显式同时指定 `--persist-state --apply-identities --mirror-version <version>`；缺少任一门禁均不得创建身份。
- 不得调用 CAA、LRCLIB、网易、Spotify、YouTube 或其他公网资源；不得实现资源补全。
- 不得扩展 Worker、租约、心跳、ProviderQuotaLedger 或个人播放/收藏评分。
- 不得修改已应用的 Batch 1 / Batch 2，也不得创建新的生产 Draft。
- 自动身份只接受 `LocalAutoEligibility:v3` 的 `Matched` 结果；低置信、版本冲突、时长不符、详情缺失、指纹漂移和既有身份均不得写入。

## 当前脚本结构与处理规则

```text
scripts/deploy/       # GHCR 拉取和 MEDIA 部署运维入口
scripts/database/     # schema baseline 验证
scripts/musicbrainz/  # DSM 镜像运维与 C# 规则对照工具
scripts/legacy/       # 冻结的旧 Worker / 旧富化入口，不得扩展或调用
```

- `scripts/musicbrainz/extract_shadow_run_candidates.py` 是 C# 策略迁移期间的规则基准，不能删除或放宽。
- `scripts/legacy/catalog_worker.py` 与 `scripts/legacy/run_enrichment_task.sh` 仅供历史恢复参考，禁止在本任务运行、修改或纳入新流程。
- 已删除旧 Docker Hub 发布脚本、一次性 Pilot SQL 生成器、旧导入 CLI 与 Batch 2 资源预检脚本；不得重新创建同类绕过 `IdentityImportService` 的写库工具。
- MEDIA PostgreSQL 备份已由 LiteAgent 的 `skills/ops_backup.py` 负责；WebMusic 仓库不再保留重复备份脚本。
- `backend.Tests/Fixtures/identity/round3-existing-identities-pre-batch2-20260909.json` 是历史测试证据，只有 29 条身份，不能当作当前生产身份快照；任何新扫描都必须在运行时读取当前数据库状态。

## 现有代码接入点与不可改变的行为

| 文件 | 可复用点 | 本任务约束 |
|---|---|---|
| `backend/Services/LocalMusicBrainzService.cs` | `ILocalMusicBrainzService`、私网 IP 校验、禁止重定向、`ScanMediaIdentityAsync` | 必须复用，不得另建 HTTP 客户端或绕过其私网门禁 |
| `backend/Services/LocalMusicBrainzShadowRunner.cs` | 只读扫描报告、候选查询和本地匹配调用 | 不能复用其中按个人播放/收藏排序的逻辑；自动扫描候选必须按本清单的独立策略筛选 |
| `backend/Services/IdentityImportService.cs` | 人工审核身份的 Draft/Approve/Apply 状态机 | 人工批次继续走该服务；LocalAuto:v3 自动身份使用扫描状态、策略/镜像版本和报告 SHA 形成独立证据链，不伪装为人工审批 |
| `backend/Data/AppDbContext.cs` | `DbSet` 注册与 Fluent 索引约定 | 所有新状态/外部信号实体都必须在此映射、建唯一索引并通过 EF migration 管理 |
| `backend/Services/ScannerService.cs` | 新媒体文件的 `AddedAt`、元数据和 `FileHash` | 第一版不允许在扫描入库路径自动触发本地 MB HTTP 请求；增量 CLI 依据 `AddedAt`、输入指纹和 `RetryAfter` 选取候选 |
| `backend/Program.cs` | `ILocalMusicBrainzService` DI 与私网 `ConnectCallback` | CLI 必须置于任何 `Migrate()`、账号自举、封面清理之前；Dry Run 执行后必须立即退出 |

### Dry Run 与持久化状态的边界

`Dry Run` 必须使用数据库级 `SET TRANSACTION READ ONLY`，因此 **不得** 写入 `MediaIdentityScanState`。它只能输出报告与可验证的 continuation/cursor 信息。

`MediaIdentityScanState` 只在显式持久化模式更新。自动应用时，扫描状态、`MusicBrainzLocal` 身份、规范元数据引用和 MB 社区信号必须在同一数据库事务中提交或整体回滚。不要为了实现“恢复”而让 Dry Run 产生任何数据库写入。

## 任务 1：扫描状态与迁移

- [x] 新增 `MediaIdentityScanState` 实体、`DbSet`、唯一索引 `MediaFileId` 与 EF Core migration。
- [x] 保存输入指纹、扫描结果、置信度、重试时间、错误、策略版本、镜像版本和扫描报告 SHA。
- [x] 不在状态表加入封面、歌词、Worker、个人行为或外部评分字段。
- [x] 在 PostgreSQL Testcontainer 基线上验证迁移应用和扫描状态表访问；迁移回滚沿用现有 migration down 演练模式。

验收：迁移可在现有 schema 基线顺利应用与回滚；同一 `MediaFileId` 不会有重复状态行。

## 任务 2：统一保守策略

- [x] 新增 `LocalIdentityAutoEligibilityPolicy`。
- [x] 映射 Python 工具的核心保守门禁：有效元数据、版本/衍生词、UUID、置信度、标题/主艺人、时长差和既有身份；v3 移除了批内 MBID 去重，放宽了专辑乱码（改为忽略）与专辑版本词。
- [x] 固定策略版本 `LocalAutoEligibility:v3`（严格与 Python 提取脚本规则双向对齐）。
- [x] 对版本词、时长差、UUID、置信度、已有身份、同 MBID 多文件与 Unknown 写了回归测试。
- [x] C# 与 Python 核心保守提取规则完全对齐（详见 test_extract_shadow_run_candidates.py）。

验收：相同输入样本在 Python/C# 的接受或拒绝结果一致；低置信、版本词、时长不符和已有身份均无法产生候选。

## 任务 3：LocalIdentityAutoScanService 与 CLI

- [x] 新增 `LocalIdentityAutoScanService`，复用 `ILocalMusicBrainzService`，不重复实现本地镜像 HTTP 安全边界。
- [x] 支持全库、增量、ID continuation、指定上限和 CLI。
- [x] 默认和硬上限均为每次 100 首。
- [x] 增量候选仅包括：从未扫描、输入指纹变化、MirrorVersion/PolicyVersion 变化或已到 `RetryAfter` 的媒体。
- [x] 实现状态幂等与可恢复；使用持久状态和 `MediaFileId` continuation，不使用数组下标。
- [x] 默认 Dry Run：不写 `MediaIdentities`、`IdentityImportBatches`、`IdentityImportItems`、Lyrics、封面或任何外部评分。
- [x] Dry Run 不写 `MediaIdentityScanState`，输出带输入/结果双 SHA-256 的报告和 continuation。
- [x] PostgreSQL Dry Run 使用 `SET TRANSACTION READ ONLY`；集成测试断言非法写入收到 SQLSTATE `25006`。
- [x] 输出 JSON 报告：扫描范围、PolicyVersion、MirrorVersion、输入/结果 SHA-256、各 Outcome 数、候选明细、错误与耗时。
- [x] Phase 1：Dry Run 审计、物理只读事务与 v3 规则验证。
- [x] Phase 2 代码：显式 `--persist-state --apply-identities` 时，在可序列化事务内重新核验元数据指纹和既有身份，随后原子写入扫描状态、身份、元数据和 MB 社区信号。
- [ ] Phase 2 生产：小批验收后以 continuation 执行全库；完成后启用定时增量扫描新入库文件。

验收：并发扫描不重复状态/候选；模拟中断后能继续；输入指纹变化会重新扫描；Dry Run 的关键业务表与文件系统零写入。

## 任务 4：本地 MB 详情与社区信号

- [x] 定义并使用本地详情接口：`ratings+isrcs+tags+genres+releases+release-groups+artist-credits`。
- [x] 使用通用 `MediaExternalReferences` 与 `MediaExternalSignals`，不创建平台专用评分表。
- [x] 自动身份事务保留 Recording/Release/Release Group、ISRC、canonical 元数据、rating、rating_count、来源 URL、抓取哈希和时间。
- [x] 实现 `MB_CommunityScore = rating_0_to_5 * ln(1 + rating_count)`；样本数 < 3 不提供排序归一分。
- [ ] 不自动覆盖 `MediaFiles` 的用户原始元数据。

验收：只对高置信身份设计/生成快照；无评分或小样本评分不会被误判为高口碑。

## 任务 5：Last.fm 评分（MB 全量身份完成后）

- [x] 复用 `MediaExternalReferences` / `MediaExternalSignals`，不新增 Last.fm 专用表。
- [x] 实现按 Recording MBID 的 `track.getInfo` 适配器接口，默认 `LastFm:Enabled=false`。
- [x] 固化 `LastFmGlobalPopularity:v1`：基于 `playcount` 与 `listeners` 的对数归一化，结果仅代表 Last.fm 公共热度。
- [x] 已实现刷新缓存、429 `Retry-After`、失败保留旧快照与来源归属策略。
- [ ] 获取并安全配置 API key 后，先执行 10 首真实只读探测；当前 `LastFm:Enabled=false`。

验收：实现设计、DTO、数据模型与单元测试即可；实际 API 调用须等独立授权。

## 任务 6：Library 展示设计

- [x] 扩展 `/api/media`，返回并支持按 `mbCommunityScore` 与 `lastFmPopularity` 服务端排序。
- [x] `/library` 已显示独立的 MB 社区分（含样本数）与 Last.fm 热度；页面只读本地快照。
- [ ] 不混入 `Favorites`、`PlayHistories` 或个人数据。
- [ ] 不在页面请求本地 MusicBrainz 或 Last.fm。

验收：先提交前端/后端设计和测试方案；实际 UI 代码可在身份/评分模型通过复核后再完成。

## 必需测试矩阵

- [x] PostgreSQL migration application and state-table access; full down/up drill remains part of the pre-release gate.
- [x] 进程内并发互斥和状态唯一键；跨进程竞争失败会明确报错而非产生重复状态。
- [x] 元数据指纹变化后重试。
- [x] 已有任意身份跳过。
- [x] 低置信、衍生版本、时长不符、Unknown 零候选。
- [x] Dry Run 物理只读事务拒绝写入（SQLSTATE `25006`）。
- [x] 崩溃/中断后以持久状态和 continuation 恢复；每批事务整体提交或回滚。
- [ ] C# / Python 保守规则一致性样本。
- [ ] 外部信号的来源隔离、样本数门槛与分数算法。

## 当前执行顺序

1. 完成 v3 + 名称治理后的 1,000 首只读基线复测；
2. 通过全部单元测试、PostgreSQL 原子写入/回滚测试和前端构建；
3. 正规 Git/CI/GHCR/MEDIA 发布；
4. 先执行最多 10 首自动身份小批验收，核对四张表及 `/library`；
5. 验收通过后自动续跑全库，每批最多 100 首；
6. 全库结束后，对历史已批准身份补跑 MB 社区信号；
7. 新媒体按增量模式定时扫描；Last.fm 另行配置 API key 后启用。
