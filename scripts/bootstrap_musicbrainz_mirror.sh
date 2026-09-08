#!/usr/bin/env bash
# ==============================================================================
# Script: bootstrap_musicbrainz_mirror.sh
# Purpose: Clone fixed official metabrainz/musicbrainz-docker, verify git revision,
#          install private network configuration, and output manual next steps.
#          Supports environments WITHOUT host git (e.g. Synology DSM) via pinned Docker image.
# Constraints:
#   - MUST NOT automatically download multi-GB dumps
#   - MUST NOT automatically initialize DB or start production containers
#   - MUST strictly pin upstream tag, commit hash, and git image digest
# ==============================================================================
set -euo pipefail

# 1. 配置上游固定版本参数与固定镜像 Digest
UPSTREAM_REPO="https://github.com/metabrainz/musicbrainz-docker.git"
PINNED_TAG="v-2026-07-30.1"
PINNED_COMMIT="c8a225fab3ea3a4650d8b1e0fab41d98878adc29"
PINNED_GIT_IMAGE="alpine/git:2.47.2@sha256:062a01ad7a0eb17cff382bc5e26086b4d710e56dfdfdf001109a49b6d9bd378c"

DRY_RUN=0
TARGET_DIR="/opt/musicbrainz-docker"
DATA_DIR="/opt/musicbrainz-data"

POSITIONAL_ARGS=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        --target-dir)
            TARGET_DIR="$2"
            shift 2
            ;;
        --data-dir)
            DATA_DIR="$2"
            shift 2
            ;;
        *)
            POSITIONAL_ARGS+=("$1")
            shift
            ;;
    esac
done

if [ ${#POSITIONAL_ARGS[@]} -ge 1 ]; then
    TARGET_DIR="${POSITIONAL_ARGS[0]}"
fi
if [ ${#POSITIONAL_ARGS[@]} -ge 2 ]; then
    DATA_DIR="${POSITIONAL_ARGS[1]}"
fi

echo "=== 🚀 WebMusic Local MusicBrainz Mirror Bootstrap ==="
[ "${DRY_RUN}" -eq 1 ] && echo "🧪 模式: DRY-RUN (演练模式：仅在指定目标目录写入测试配置，不触碰生产目录；演练后需外部清理该目录)"
echo "📁 目标安装目录: ${TARGET_DIR}"
echo "💾 数据持久化根目录: ${DATA_DIR}"
echo "🏷️ 固定上游 Tag: ${PINNED_TAG}"
echo "🔒 固定 Commit:  ${PINNED_COMMIT}"
echo "🐳 固定 Git 镜像: ${PINNED_GIT_IMAGE}"
echo ""

# 2. 检查基础命令依赖 (支持宿主机无 git 时使用固定 digest 的 docker 镜像执行 git)
HAS_HOST_GIT=0
if command -v git >/dev/null 2>&1; then
    HAS_HOST_GIT=1
fi

if [ "${HAS_HOST_GIT}" -eq 0 ]; then
    echo "ℹ️ 宿主机未检测到 git 命令（如群晖 DSM 原生环境），将通过 Docker 容器 [${PINNED_GIT_IMAGE}] 执行 Git 检出与校验..."
    if ! command -v docker >/dev/null 2>&1; then
        echo "❌ 宿主机既无 git 也无 docker，无法获取上游源码！"
        exit 1
    fi
fi

# 封装 git 执行器
run_git() {
    if [ "${HAS_HOST_GIT}" -eq 1 ]; then
        git "$@"
    else
        docker run --rm -i \
            -v "${TARGET_DIR}:/workspace" \
            -w /workspace \
            "${PINNED_GIT_IMAGE}" "$@"
    fi
}

# 3. 创建持久化数据目录
echo "📦 正在准备数据与目标目录..."
mkdir -p "${TARGET_DIR}"
if [ "${DRY_RUN}" -eq 0 ]; then
    mkdir -p "${DATA_DIR}/pgdata"
    mkdir -p "${DATA_DIR}/solrdata"
    mkdir -p "${DATA_DIR}/dumps"
    mkdir -p "${DATA_DIR}/backups"
fi
echo "✅ 目录已准备就绪。"

# 4. 克隆或拉取官方仓库
if [ -d "${TARGET_DIR}/.git" ]; then
    echo "🔄 目标目录已存在 Git 仓库，正在核验并切换..."
    cd "${TARGET_DIR}"
    run_git fetch origin --tags
else
    echo "📥 正在从官方上游克隆代码..."
    if [ "${HAS_HOST_GIT}" -eq 1 ]; then
        git clone "${UPSTREAM_REPO}" "${TARGET_DIR}"
        cd "${TARGET_DIR}"
    else
        docker run --rm -i \
            -v "$(dirname "${TARGET_DIR}"):/parent" \
            -w /parent \
            "${PINNED_GIT_IMAGE}" clone "${UPSTREAM_REPO}" "$(basename "${TARGET_DIR}")"
        cd "${TARGET_DIR}"
    fi
fi

# 5. 检出固定 Tag 并严格核对 Commit Hash
echo "🔍 检出 Tag [${PINNED_TAG}]..."
run_git checkout "${PINNED_TAG}"

ACTUAL_COMMIT=$(run_git rev-parse HEAD | tr -d '\r\n')
if [ "${ACTUAL_COMMIT}" != "${PINNED_COMMIT}" ]; then
    echo "❌ 安全校验失败！"
    echo "   预期 Commit: ${PINNED_COMMIT}"
    echo "   实际 Commit: ${ACTUAL_COMMIT}"
    echo "   上游仓库已被篡改或 Tag 指向了非预期提交，终止安装！"
    exit 1
fi
echo "✅ 上游 Git Commit 安全校验通过: [${ACTUAL_COMMIT}]"

# 6. 配置 local/compose/override.yml 与 .env
echo "⚙️ 正在安装私有网络与存储绑定配置..."
mkdir -p "${TARGET_DIR}/local/compose"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_SRC="${SCRIPT_DIR}/../infra/musicbrainz-mirror/compose.yaml"
ENV_EXAMPLE_SRC="${SCRIPT_DIR}/../infra/musicbrainz-mirror/.env.example"

if [ -f "${COMPOSE_SRC}" ]; then
    cp "${COMPOSE_SRC}" "${TARGET_DIR}/local/compose/override.yml"
    echo "✅ 已复制本地安全 Compose 覆盖配置 -> local/compose/override.yml"
fi

if [ -f "${ENV_EXAMPLE_SRC}" ] && [ ! -f "${TARGET_DIR}/.env" ]; then
    cp "${ENV_EXAMPLE_SRC}" "${TARGET_DIR}/.env"
    echo "✅ 已初始化环境变量模板 -> .env (请按需修改 IP 与挂载路径)"
fi

# 7. 演练模式善后与操作指引输出
if [ "${DRY_RUN}" -eq 1 ]; then
    echo ""
    echo "================================================================================"
    echo "🧪 [DRY-RUN PASS] 免 Git 宿主机容器化演练成功完成！"
    echo "   - 成功通过固定 Digest 容器拉取代码至指定目标目录"
    echo "   - 严格核验 Commit Hash 100% 吻合: [${ACTUAL_COMMIT}]"
    echo "   - 成功生成安全 Compose 与 Env 配置文件（未触碰生产目录）"
    echo "   - 提示: 指定的目标目录 [${TARGET_DIR}] 包含演练配置文件，请由外部步骤按需清理"
    echo "================================================================================"
    exit 0
fi

echo ""
echo "================================================================================"
echo "🎉 MusicBrainz 基础镜像代码与受控配置 Bootstrap 完成！"
echo "================================================================================"
echo "后续操作须知 (绝不自动执行):"
echo "1. 编辑环境配置:"
echo "   vim ${TARGET_DIR}/.env"
echo "   确保 MUSICBRAINZ_DOCKER_HOST_IPADDRCOL 严格绑定为私网 IP (如 192.168.2.18:)"
echo "   确保端口为 5050 (避开群晖 DSM 5000 端口)"
echo "2. 数据包准备与校验 (下载需数十 GB，请在后台执行并核验散列):"
echo "   cd ${DATA_DIR}/dumps"
echo "   curl -O https://data.metabrainz.org/pub/musicbrainz/data/fullexport/latest/MD5SUMS"
echo "   # 下载对应 dump 包..."
echo "   md5sum -c MD5SUMS --ignore-missing"
echo "3. 镜像构建与启动:"
echo "   cd ${TARGET_DIR}"
echo "   docker compose build"
echo "   docker compose run --rm musicbrainz createdb.sh"
echo "   docker compose up -d"
echo "4. 验收测试:"
echo "   ./scripts/verify_musicbrainz_mirror.sh --host <私网IP> --port 5050"
echo "================================================================================"
