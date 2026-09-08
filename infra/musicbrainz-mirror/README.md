# 本地 MusicBrainz 镜像与检索节点部署指南 (Phase 1 POC)

本文档是基于 [LOCAL_MUSICBRAINZ_LASTFM_ORCHESTRATION.md](../../docs/LOCAL_MUSICBRAINZ_LASTFM_ORCHESTRATION.md) 规范构建的**私网本地 MusicBrainz 镜像**部署指南。

---

## 一、核心原则与安全边界

1. **绝对隔离**：
   - 必须部署在独立 Linux 宿主机/虚拟机（如 PVE 独立虚拟机、专用 NAS 或本地服务器），**严禁与 MEDIA 生产环境共享主机或卷**；
   - **严禁公网暴露**：Web Service（5000 端口）必须仅监听局域网内网 IP 或 Tailscale 接口；PostgreSQL（5432）、Solr（8983）、Redis 严格禁止映射到宿主机端口；
   - **硬门禁**：当前 Worker 依赖 MusicBrainz Web Service 搜索接口，因此**必须包含官方 Web Service 与 Solr 搜索索引**，严禁使用只能查 SQL 的 `alt-db-only-mirror`。
2. **上游固定版本**：
   - 固定上游仓库：`https://github.com/metabrainz/musicbrainz-docker.git`
   - 固定 Release Tag：`v-2026-07-30.1`
   - 固定 Git Commit：`c8a225fab3ea3a4650d8b1e0fab41d98878adc29`
3. **数据许可证边界**：
   - `mbdump.tar.bz2`（核心数据）：采用 **CC0 1.0 (Public Domain)**；
   - `mbdump-derived.tar.bz2`（派生数据）：采用 **CC BY-NC-SA 3.0**；
   - 纯身份检索场景下，优先仅下载核心数据包，降低存储占用并满足开源合规。

---

## 二、宿主机硬件与环境建议

| 资源项 | 最低要求 | 推荐配置 | 说明 |
| :--- | :--- | :--- | :--- |
| **CPU** | 4 核心 | 8 核心以上 | Solr 文本索引与 PostgreSQL 导入高度消耗 CPU |
| **内存 (RAM)** | 8 GB | 16 GB 以上 | PostgreSQL `shared_buffers` 需 2GB，Solr Heap 需 2GB |
| **存储 (Disk)** | 100 GB 可用 | 200 GB+ NVMe SSD | 原始压缩包约 5~8GB，解压入库与建索后约 60~80GB |
| **网络** | 1000 Mbps 内网 | 局域网 + Tailscale | 仅服务 Mac/NAS Worker 与受控私网通信 |

---

## 三、部署流程（人工分步执行）

### 步骤 1：执行初始化脚本核验上游源码
在目标宿主机上执行：
```bash
bash scripts/bootstrap_musicbrainz_mirror.sh /opt/musicbrainz-docker
```
该脚本将完成：
- 克隆官方代码并检出固定 Tag `v-2026-07-30.1`；
- 核验 Commit Hash `c8a225fab3ea3a4650d8b1e0fab41d98878adc29`；
- 部署 `local/compose/override.yml`（绑定私网 IP 与持久化目录）；
- 准备 `.env` 文件。

### 步骤 2：配置监听地址（私网/Tailscale 限制）
编辑 `/opt/musicbrainz-docker/.env`：
```bash
# 获取本机 Tailscale IP 或 LAN IP
ip -br addr show tailscale0  # 或 eth0

# 设置绑定 IP (必须以冒号结尾！)
MUSICBRAINZ_DOCKER_HOST_IPADDRCOL=100.115.42.126:
MUSICBRAINZ_WEB_SERVER_PORT=5000
```
> [!CAUTION]
> 切勿将 `MUSICBRAINZ_DOCKER_HOST_IPADDRCOL` 设为空或 `0.0.0.0:`，否则容器端口将直接暴露给公网。

### 步骤 3：下载固定日期数据快照
进入目录并下载指定日期的快照（避免使用最新浮动地址，确保环境可复现）：
```bash
cd /opt/musicbrainz-docker

# 推荐快照日期示例：20260902-002507
DUMP_DATE="20260902-002507"
BASE_URL="https://data.metabrainz.org/pub/musicbrainz/data/fullexport/${DUMP_DATE}"

mkdir -p /opt/musicbrainz-data/dumps
cd /opt/musicbrainz-data/dumps

# 下载核心数据包及校验文件
curl -fSL -O "${BASE_URL}/MD5SUMS"
curl -fSL -O "${BASE_URL}/mbdump.tar.bz2"
curl -fSL -O "${BASE_URL}/mbdump-derived.tar.bz2"
curl -fSL -O "${BASE_URL}/mbdump-cdstubs.tar.bz2"
curl -fSL -O "${BASE_URL}/mbdump-cover-art-archive.tar.bz2"

# 严格校验散列值
md5sum -c MD5SUMS --ignore-missing
```

### 步骤 4：数据库初始化与数据导入
```bash
cd /opt/musicbrainz-docker

# 拉取并构建官方基础容器镜像
docker compose build

# 执行初始建库与数据导入 (预计耗时 1~3 小时，取决于 CPU/SSD 性能)
docker compose run --rm musicbrainz InitDb.pl --createdb --import /media/dbdump/mbdump*.tar.bz2
```

### 步骤 5：构建 Solr 搜索索引与启动
```bash
# 构建搜索索引 (依赖已导入的数据库数据)
docker compose run --rm indexer setup-sir install

# 启动全套服务 (Web Service + DB + Solr + SIR + Valkey)
docker compose up -d

# 检查容器运行状态
docker compose ps
```

### 步骤 6：运行验证与演练脚本
在宿主机或同一局域网下的测试机运行验收脚本：
```bash
bash scripts/verify_musicbrainz_mirror.sh --host 100.115.42.126 --port 5000
```

---

## 四、备份与灾难恢复（DR）演练

### 1. 备份数据卷与关键元数据
```bash
# 1. 导出 PostgreSQL 模式与核心表
docker compose exec -T db pg_dump -U musicbrainz -d musicbrainz_db -Fc > /opt/musicbrainz-data/backups/mb_backup_$(date +%Y%m%d).dump

# 2. 导出 Solr 索引快照
docker compose exec search curl -s "http://localhost:8983/solr/admin/collections?action=BACKUP&name=solr_backup_$(date +%Y%m%d)&collection=recording&location=/var/cache/musicbrainz/solr-backups"
```

### 2. 恢复演练验证
```bash
# 测试 pg_restore 结构与数据完整性 (Dry-run 校验)
docker compose exec -T db pg_restore --schema-only -d musicbrainz_db /backups/mb_backup_*.dump > /dev/null
```
