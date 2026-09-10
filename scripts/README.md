# Scripts 运维边界

本目录只保留可复现的运维和验证入口。业务规则、数据库状态机、身份扫描和外部评分逻辑必须进入 C# 服务与测试，不能靠临时脚本绕过。

## 目录

| 目录 | 内容 | 使用规则 |
|---|---|---|
| `deploy/` | GHCR 拉取、MEDIA 容器重建和探活 | 仅在 CI 构建成功且已获部署授权后执行 |
| `database/` | schema fingerprint / baseline 工具与 EF 离线命令说明（详见 `database/README.md`） | 仅维护窗口使用；必须先备份 |
| `musicbrainz/` | DSM 镜像、规则基准与本地身份扫描编排 | 本地镜像只可使用私网 IP；生产持久化必须显式开启 |
| `legacy/` | 旧 Worker 与旧富化脚本 | 冻结，不得作为新功能入口或常规任务运行 |

## 已移除的脚本

- `publish_release.sh`：旧 Docker Hub 发布流程，已被 GitHub Actions + GHCR 替代。
- `apply_approved_identities_pilot.py`：已完成的 10 首试点写入工具，现由 `IdentityImportService` 审计链路替代。
- `identity_importer_cli.py`：旧导入 payload 工具，避免与服务端导入状态机产生规则漂移。
- `preflight_batch2_assets.py`：一次性 Batch 2 资源预检支线，当前主线不包含资源补全。
- `backup_webmusic_media.sh`：已迁由 LiteAgent 的 `skills/ops_backup.py` 负责。LiteAgent 已实现 MEDIA PostgreSQL 的 SSH 流式备份、独立目录和百度网盘归档，WebMusic 不再保留重复入口。

## 本地身份扫描

`musicbrainz/run_local_identity_scan.sh` 是全库/增量的标准编排入口，强制指定镜像版本。默认只读；只有 `APPLY_IDENTITIES=true` 才会追加 `--persist-state --apply-identities`。单批数据库事务始终不超过 100 首。

```bash
# 五批只读复测
MIRROR_VERSION=v-2026-07-30.1:20260902-002507 MAX_BATCHES=5 \
  scripts/musicbrainz/run_local_identity_scan.sh

# 高置信身份、MB 元数据与社区评分自动持久化
MIRROR_VERSION=v-2026-07-30.1:20260902-002507 APPLY_IDENTITIES=true \
  scripts/musicbrainz/run_local_identity_scan.sh

# 中断后从上一个报告的 continuation 恢复
MIRROR_VERSION=v-2026-07-30.1:20260902-002507 APPLY_IDENTITIES=true START_AFTER_ID=1028 \
  scripts/musicbrainz/run_local_identity_scan.sh
```
