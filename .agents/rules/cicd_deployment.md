# WebMusic CI/CD 与部署验证规范 (Workspace Rule)

本规范用于跨会话复用，明确 WebMusic 的构建、推送、发布、部署与在线验证标准流程。

## 1. 核心架构与域名映射

- **线上生产域名**：`https://music.maifeipin.com`
- **目标运行主机**：通过 `ssh media` 直连（运行 Docker 容器）。
- **容器与端口映射**：
  - 前端容器：`webmusic-frontend`，主机监听端口 `8090`
  - 后端容器：`webmusic-backend`，主机监听端口 `5080`（容器内 `8080`）
  - 数据库容器：`webmusic-postgres`，Postgres 15
- **外部流量流向**：`用户` -> `https://music.maifeipin.com` -> `Gateway/Nginx 反向代理` -> `MEDIA 主机 (8090/5080)`。
- **浏览器验证**：如需验证 Web UI、用户登录、管理员权限与在线功能，统一访问 `https://music.maifeipin.com`（或通过 browser subagent 访问）。

## 2. CI/CD 标准交付流程

### 第一步：代码提交与 CI 构建（推送 GHCR）
1. 在本地完成代码修改并验证本地编译通过：
   ```bash
   dotnet build backend/WebMusic.Backend.csproj
   npm run build --prefix frontend
   ```
2. 提交并推送到 GitHub `main` 分支：
   ```bash
   git add -A && git commit -m "..." && git push origin main
   ```
3. GitHub Actions 自动触发 `.github/workflows/docker-publish.yml`，构建并推送：
   - `ghcr.io/maifeipin/webmusic-frontend:latest`
   - `ghcr.io/maifeipin/webmusic-backend:latest`
   - `ghcr.io/maifeipin/webmusic-ai-lyrics:latest`
4. 本地可通过 `gh run list -L 1` 和 `gh run watch <run_id>` 检查镜像构建状态。

### 第二步：CD 部署到 MEDIA 主机
镜像推送到 GHCR 后，在本地或自动化脚本中执行以下标准部署指令：
```bash
ssh media "cd /root/WebMusic && docker compose pull backend frontend && docker compose up -d --no-build backend frontend && docker image prune -f"
```

### 第三步：健康检查与鉴权验证
部署完成后，必须在 `media` 上验证容器状态与鉴权正常：
1. **基础 HTTP 状态码**：
   - 前端首页：`curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:8090/` -> 预期 `200`
   - 后端受保护接口未登录：`curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/media?page=1&pageSize=1` -> 预期 `401`
2. **数据库与角色迁移**：
   - 查看 `webmusic-backend` 日志确认数据库 DDL 幂等建表及管理员角色同步：
     `docker logs webmusic-backend --tail 50`
   - 确认无启动异常，`JobWorker started`。
3. **在线 Web 页面验证**：
   - 打开 `https://music.maifeipin.com` 验证管理员登录、设置页与曲库功能正常。
