# 数据库脚本与 EF Core 迁移说明

## 1. EF Core 离线设计时命令

由于 WebMusic 运行环境要求外部数据库连接并在启动时构建完整应用主机，普通执行 `dotnet ef migrations ...` 会停在应用启动阶段。

离线环境或 CI/CD 检查模型漂移时，**必须显式提供环境变量 `WEBMUSIC_EF_DESIGN_TIME=1`** 以启用 `AppDbContextFactory` 设计时工厂：

```bash
cd backend
WEBMUSIC_EF_DESIGN_TIME=1 dotnet ef migrations has-pending-model-changes \
  --project WebMusic.Backend.csproj --no-build
```

- 若无待应用变更，输出：`No changes have been made to the model since the last migration.`
- `dotnet-ef` 本地工具版本已与运行时对齐至 `8.0.10`（见 `backend/.config/dotnet-tools.json`）。

## 2. 数据库 Baseline 校验工具

- `verify_and_apply_baseline.sh`：用于生产环境数据库 schema baseline 校验与受控补丁应用。仅限维护窗口使用，使用前必须先执行独立备份。
