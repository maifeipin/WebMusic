# AI 执行清单：LocalIdentityAutoScanService + Last.fm 公共评分

本清单可直接交给新的 AI。执行前必须先阅读：

- `docs/LOCAL_MUSICBRAINZ_LASTFM_ORCHESTRATION.md`
- `backend/Services/LocalMusicBrainzService.cs`
- `backend/Services/LocalMusicBrainzShadowRunner.cs`
- `backend/Services/IdentityImportService.cs`
- `scripts/musicbrainz/extract_shadow_run_candidates.py`

## 不可突破的边界

- 仅在本地工作区实现与测试；不得 `git commit`、`git push`、触发 CI、构建镜像、部署或写入 MEDIA。
- 不得调用 CAA、LRCLIB、网易、Spotify、YouTube 或其他公网资源；不得实现资源补全。
- 不得扩展 Worker、租约、心跳、ProviderQuotaLedger 或个人播放/收藏评分。
- 不得修改已应用的 Batch 1 / Batch 2，也不得创建新的生产 Draft。
- 不得把当前未提交的 Batch 2 资源预检代码纳入本任务的暂存或提交。

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
| `backend/Services/IdentityImportService.cs` | 已审核身份的 Draft/Approve/Apply 状态机 | 本阶段不得调用、修改或绕过该状态机；只生成 Dry Run 候选证据 |
| `backend/Data/AppDbContext.cs` | `DbSet` 注册与 Fluent 索引约定 | 所有新状态/外部信号实体都必须在此映射、建唯一索引并通过 EF migration 管理 |
| `backend/Services/ScannerService.cs` | 新媒体文件的 `AddedAt`、元数据和 `FileHash` | 第一版不允许在扫描入库路径自动触发本地 MB HTTP 请求；增量 CLI 依据 `AddedAt`、输入指纹和 `RetryAfter` 选取候选 |
| `backend/Program.cs` | `ILocalMusicBrainzService` DI 与私网 `ConnectCallback` | CLI 必须置于任何 `Migrate()`、账号自举、封面清理之前；Dry Run 执行后必须立即退出 |

### Dry Run 与持久化状态的边界

`Dry Run` 必须使用数据库级 `SET TRANSACTION READ ONLY`，因此 **不得** 写入 `MediaIdentityScanState`。它只能输出报告与可验证的 continuation/cursor 信息。

`MediaIdentityScanState` 的持久化、暂停和恢复能力属于后续的本地非 Dry Run 模式；该模式即使只写扫描状态，也必须在本地开发数据库中完成演练并单独报告，不能默认指向 MEDIA。不要为了实现“恢复”而让 Dry Run 产生任何数据库写入。

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
- [!] **受控阶段边界**：当前阶段（Phase 1）限定为“受控验证与报告”（Dry Run 审计与 Shadow 演练）。生产环境持久化扫描状态、自动创建批次、自动写入高置信 MediaIdentity 及守护进程/定时增量扫描均属于 Phase 2（需经生产发布独立审批与授权）。

验收：并发扫描不重复状态/候选；模拟中断后能继续；输入指纹变化会重新扫描；Dry Run 的关键业务表与文件系统零写入。

## 任务 4：本地 MB 详情与社区信号（仅设计/本地实现）

- [x] 定义本地详情接口，使用 `ratings+isrcs+tags+genres+releases+release-groups+artist-credits`；尚未在任何环境执行。
- [x] 使用通用 `MediaExternalReferences` 与 `MediaExternalSignals`，不创建平台专用评分表。
- [x] 保留 Recording/Release/Release Group、ISRC、canonical 元数据、rating、rating_count、来源 URL、抓取哈希和时间。
- [x] 实现 `MB_CommunityScore = rating_0_to_5 * ln(1 + rating_count)`；样本数 < 3 不提供排序归一分。
- [ ] 不自动覆盖 `MediaFiles` 的用户原始元数据。

验收：只对高置信身份设计/生成快照；无评分或小样本评分不会被误判为高口碑。

## 任务 5：Last.fm 评分设计（不要联网执行）

- [x] 复用 `MediaExternalReferences` / `MediaExternalSignals`，不新增 Last.fm 专用表。
- [x] 实现按 Recording MBID 的 `track.getInfo` 适配器接口，默认 `LastFm:Enabled=false`。
- [x] 固化 `LastFmGlobalPopularity:v1`：基于 `playcount` 与 `listeners` 的对数归一化，结果仅代表 Last.fm 公共热度。
- [ ] 定义周度刷新、缓存、429、失败保留旧快照与来源归属策略。
- [ ] 不能使用、记录或返回 API key；不能调用 API。

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
- [ ] 崩溃/中断后可恢复。
- [ ] C# / Python 保守规则一致性样本。
- [ ] 外部信号的来源隔离、样本数门槛与分数算法。

## 交付与停止

当前本地实现完成后只报告，不做任何外部动作：

1. 文件改动清单；
2. 数据库 migration 与回滚说明；
3. C# / Python 规则差异或一致性证据；
4. 全量测试输出；
5. 本地 1,000 首 Dry Run 命令与预期报告格式；
6. 预计全库扫描耗时、命中率和风险；
7. 明确声明：未提交、未推送、未部署、未访问或写入 MEDIA、未请求公网服务。

在用户单独授权之前，停止在上述报告阶段。
