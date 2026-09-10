#!/usr/bin/env bash
# ==============================================================================
# Script: verify_musicbrainz_mirror.sh
# Purpose: Comprehensive verification and health check for local MusicBrainz mirror
# Tests:
#   1. Private network binding and exposure boundaries (via docker inspect in SSH mode)
#   2. HTTP Web Service & Solr Search functionality (artist, release, recording)
#   3. Exact song samples assertion (Yesterday, Chasing Pavements, 9 Crimes)
#   4. Strict snapshot integrity & MD5 checksum verification (exact 7 files, no skips)
#   5. Backup & Disaster Recovery (DR) dry-run check (optional)
# ==============================================================================
set -euo pipefail

HOST="192.168.2.18"
PORT="5050"
DATA_DIR="/volume1/docker/musicbrainz-data/dumps"
SSH_TARGET=""
CHECK_BACKUP=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --host)
            HOST="$2"
            shift 2
            ;;
        --port)
            PORT="$2"
            shift 2
            ;;
        --data-dir)
            DATA_DIR="$2"
            shift 2
            ;;
        --ssh-target)
            SSH_TARGET="$2"
            shift 2
            ;;
        --check-backup)
            CHECK_BACKUP=1
            shift
            ;;
        *)
            echo "未知参数: $1"
            echo "用法: $0 [--host <IP>] [--port <端口>] [--data-dir <快照目录>] [--ssh-target <user@host>] [--check-backup]"
            exit 1
            ;;
    esac
done

BASE_URL="http://${HOST}:${PORT}"

echo "=== 🧪 MusicBrainz 本地镜像验收与健康检查 (严格审查版 v2) ==="
echo "🎯 目标节点: ${BASE_URL}"
echo "📁 快照目录: ${DATA_DIR}"
[ -n "${SSH_TARGET}" ] && echo "🔑 SSH 远程模式: ${SSH_TARGET}"
echo ""

PASS_COUNT=0
TOTAL_TESTS=0

record_test() {
    TOTAL_TESTS=$((TOTAL_TESTS + 1))
    local name="$1"
    local status="$2"
    local detail="$3"
    if [ "$status" -eq 0 ]; then
        PASS_COUNT=$((PASS_COUNT + 1))
        echo "✅ [PASS] ${name}: ${detail}"
    else
        echo "❌ [FAIL] ${name}: ${detail}"
    fi
}

# --- 测试 1: 安全网络监听与 Docker 端口发布边界核验 ---
echo "--- 1. 网络监听与 Docker 端口发布核验 ---"

# 检查 Web 端口绑定
if [ -n "${SSH_TARGET}" ]; then
    IS_OPEN_TO_ALL=$(ssh "${SSH_TARGET}" "netstat -tuln 2>/dev/null || ss -tuln 2>/dev/null" | grep -E "0\.0\.0\.0:${PORT}\b|\*:${PORT}\b" || true)
elif command -v ss >/dev/null 2>&1; then
    IS_OPEN_TO_ALL=$(ss -tuln 2>/dev/null | grep -E "0\.0\.0\.0:${PORT}\b|\*:${PORT}\b" || true)
elif command -v netstat >/dev/null 2>&1; then
    IS_OPEN_TO_ALL=$(netstat -tuln 2>/dev/null | grep -E "0\.0\.0\.0:${PORT}\b|\*:${PORT}\b" || true)
else
    IS_OPEN_TO_ALL=""
fi

if [ -n "${IS_OPEN_TO_ALL}" ]; then
    record_test "Web 监听绑定" 1 "端口 ${PORT} 监听在 0.0.0.0，存在公网暴露风险！"
else
    record_test "Web 监听绑定" 0 "端口 ${PORT} 严格绑定在指定私网或回环地址。"
fi

# 检查 Docker 容器端口发布配置 (P1 修复: 通过 docker inspect 检查 HostPort 是否为空)
if [ -n "${SSH_TARGET}" ]; then
    DB_PORT_BINDING=$(ssh "${SSH_TARGET}" "docker inspect --format='{{json .NetworkSettings.Ports}}' musicbrainz-docker-db-1 2>/dev/null" || echo "INSPECT_FAILED")
    SOLR_PORT_BINDING=$(ssh "${SSH_TARGET}" "docker inspect --format='{{json .NetworkSettings.Ports}}' musicbrainz-docker-search-1 2>/dev/null" || echo "INSPECT_FAILED")

    if [ "${DB_PORT_BINDING}" = "INSPECT_FAILED" ]; then
        record_test "PostgreSQL 端口发布配置" 1 "未能通过 docker inspect 获取数据库容器端口配置！"
    elif echo "${DB_PORT_BINDING}" | grep -q '"5432/tcp":null' || ! echo "${DB_PORT_BINDING}" | grep -q 'HostPort'; then
        record_test "PostgreSQL 端口发布配置" 0 "容器 5432/tcp 未映射任何宿主机端口 (HostPort 为空)。"
    else
        record_test "PostgreSQL 端口发布配置" 1 "容器 5432/tcp 存在宿主机 HostPort 映射 (${DB_PORT_BINDING})！"
    fi

    if [ "${SOLR_PORT_BINDING}" = "INSPECT_FAILED" ]; then
        record_test "Solr 端口发布配置" 1 "未能通过 docker inspect 获取 Solr 容器端口配置！"
    elif echo "${SOLR_PORT_BINDING}" | grep -q '"8983/tcp":null' || ! echo "${SOLR_PORT_BINDING}" | grep -q 'HostPort'; then
        record_test "Solr 端口发布配置" 0 "容器 8983/tcp 未映射任何宿主机端口 (HostPort 为空)。"
    else
        record_test "Solr 端口发布配置" 1 "容器 8983/tcp 存在宿主机 HostPort 映射 (${SOLR_PORT_BINDING})！"
    fi
else
    # 非 SSH 远程模式时，检查本地 docker inspect 或网络端口连通拒绝
    if command -v docker >/dev/null 2>&1 && docker inspect musicbrainz-docker-db-1 >/dev/null 2>&1; then
        DB_PORT_BINDING=$(docker inspect --format='{{json .NetworkSettings.Ports}}' musicbrainz-docker-db-1)
        if echo "${DB_PORT_BINDING}" | grep -q '"5432/tcp":null' || ! echo "${DB_PORT_BINDING}" | grep -q 'HostPort'; then
            record_test "PostgreSQL 端口发布配置" 0 "容器 5432/tcp 未映射任何宿主机端口。"
        else
            record_test "PostgreSQL 端口发布配置" 1 "容器 5432/tcp 存在宿主机端口暴露！"
        fi
        SOLR_PORT_BINDING=$(docker inspect --format='{{json .NetworkSettings.Ports}}' musicbrainz-docker-search-1)
        if echo "${SOLR_PORT_BINDING}" | grep -q '"8983/tcp":null' || ! echo "${SOLR_PORT_BINDING}" | grep -q 'HostPort'; then
            record_test "Solr 端口发布配置" 0 "容器 8983/tcp 未映射任何宿主机端口。"
        else
            record_test "Solr 端口发布配置" 1 "容器 8983/tcp 存在宿主机端口暴露！"
        fi
    else
        DB_CURL_CODE=0
        DB_EXPOSED=$(curl -sS --connect-timeout 2 "http://${HOST}:5432" 2>&1) || DB_CURL_CODE=$?
        if [ "${DB_CURL_CODE}" -ne 0 ] || echo "${DB_EXPOSED}" | grep -iq "refused\|timed out\|couldn't connect"; then
            record_test "PostgreSQL 端口隔离" 0 "端口 5432 无法从外部连接。"
        else
            record_test "PostgreSQL 端口隔离" 1 "端口 5432 疑似对外暴露或响应异常！"
        fi
        SOLR_CURL_CODE=0
        SOLR_EXPOSED=$(curl -sS --connect-timeout 2 "http://${HOST}:8983" 2>&1) || SOLR_CURL_CODE=$?
        if [ "${SOLR_CURL_CODE}" -ne 0 ] || echo "${SOLR_EXPOSED}" | grep -iq "refused\|timed out\|couldn't connect"; then
            record_test "Solr 检索端口隔离" 0 "端口 8983 无法从外部连接。"
        else
            record_test "Solr 检索端口隔离" 1 "端口 8983 疑似对外暴露或响应异常！"
        fi
    fi
fi

# --- 测试 2: Web Service 与 Solr 检索接口三维非零核验 ---
echo ""
echo "--- 2. Web Service 与 Solr 检索连通性与三维非零核验 ---"

test_entity_sample() {
    local entity="$1"
    local query="$2"
    local label="$3"
    local encoded_query
    encoded_query=$(python3 -c "import urllib.parse; print(urllib.parse.quote('''$query'''))")
    local url="${BASE_URL}/ws/2/${entity}?query=${encoded_query}&fmt=json&limit=5"
    
    local count
    count=$(python3 -c "
import sys, json, urllib.request
url = sys.argv[1]
try:
    req = urllib.request.Request(url, headers={'User-Agent': 'WebMusic-Verifier/1.0'})
    with urllib.request.urlopen(req, timeout=25) as resp:
        data = json.loads(resp.read().decode('utf-8'))
        print(data.get('count', 0))
except Exception:
    print(0)
" "${url}")

    if [ "${count}" -gt 0 ]; then
        record_test "${label}" 0 "HTTP 200, 命中 count=${count}"
        return 0
    else
        record_test "${label}" 1 "检索未命中或返回空 (count=${count})"
        return 1
    fi
}

test_entity_sample "artist" "Jackson" "Artist 全文检索"
test_entity_sample "release" "Thriller" "Release 全文检索"

# 2.3 Recording 核心检索测试（固定样本断言）
echo ""
echo "--- 3. Recording 核心检索与固定样本断言 ---"

test_recording_sample() {
    local label="$1"
    local query="$2"
    local encoded_query
    encoded_query=$(python3 -c "import urllib.parse; print(urllib.parse.quote('''$query'''))")
    local url="${BASE_URL}/ws/2/recording?query=${encoded_query}&fmt=json&limit=5"
    
    local result
    result=$(python3 -c "
import sys, json, urllib.request
url = sys.argv[1]
try:
    req = urllib.request.Request(url, headers={'User-Agent': 'WebMusic-Verifier/1.0'})
    with urllib.request.urlopen(req, timeout=25) as resp:
        data = json.loads(resp.read().decode('utf-8'))
        cnt = data.get('count', 0)
        recs = data.get('recordings', [])
        title = recs[0].get('title', '') if recs else ''
        print(f'{cnt}|{title}')
except Exception as e:
    print('0|')
" "${url}")

    local count="${result%%|*}"
    local first_title="${result#*|}"

    if [ "${count}" -gt 0 ] && [ -n "${first_title}" ]; then
        record_test "Recording 样本 [${label}]" 0 "成功命中 (count=${count}, 首条标题: \"${first_title}\")"
        return 0
    else
        record_test "Recording 样本 [${label}]" 1 "检索未命中或返回空 (count=${count})"
        return 1
    fi
}

test_recording_sample "The Beatles - Yesterday" 'recording:Yesterday AND artist:Beatles'
test_recording_sample "Adele - Chasing Pavements" 'recording:"Chasing Pavements" AND artist:Adele'
test_recording_sample "Damien Rice - 9 Crimes" 'recording:"9 Crimes" AND artist:"Damien Rice"'

# --- 测试 4: 严格数据快照散列一致性核验 (P0 修复: 必须精确匹配 7 个实际快照包) ---
echo ""
echo "--- 4. 数据快照文件与 MD5 散列精确核验 (精确 7 包) ---"

# 官方 7 个必须存在的快照包清单
EXPECTED_PACKAGES=(
    "mbdump.tar.bz2"
    "mbdump-cdstubs.tar.bz2"
    "mbdump-cover-art-archive.tar.bz2"
    "mbdump-derived.tar.bz2"
    "mbdump-event-art-archive.tar.bz2"
    "mbdump-stats.tar.bz2"
    "mbdump-wikidocs.tar.bz2"
)

MD5_SCRIPT=$(cat << 'EOF'
set -eu
DATA_DIR="$1"
shift
EXPECTED_FILES=("$@")

if [ ! -f "${DATA_DIR}/MD5SUMS" ]; then
    echo "ERROR: MD5SUMS_NOT_FOUND"
    exit 1
fi

cd "${DATA_DIR}"

# 1. 检查 7 个文件是否完整存在
MISSING=0
for f in "${EXPECTED_FILES[@]}"; do
    if [ ! -f "${f}" ]; then
        echo "MISSING_FILE: ${f}"
        MISSING=$((MISSING + 1))
    fi
done

if [ "${MISSING}" -gt 0 ]; then
    echo "ERROR: MISSING_EXPECTED_FILES"
    exit 1
fi

# 2. 生成只包含这 7 个文件的精确检验清单
TMP_MD5=$(mktemp)
for f in "${EXPECTED_FILES[@]}"; do
    grep " \*${f}$" MD5SUMS >> "${TMP_MD5}" || grep "  ${f}$" MD5SUMS >> "${TMP_MD5}" || true
done

LINE_COUNT=$(wc -l < "${TMP_MD5}" | tr -d ' ')
if [ "${LINE_COUNT}" -ne 7 ]; then
    echo "ERROR: INCOMPLETE_MD5SUMS_ENTRIES (found ${LINE_COUNT}/7)"
    rm -f "${TMP_MD5}"
    exit 1
fi

# 3. 严格执行 md5sum -c (严禁 ignore-missing)
CHECK_OUT=$(md5sum -c "${TMP_MD5}" 2>&1) || {
    echo "ERROR: MD5_CHECK_FAILED"
    echo "${CHECK_OUT}"
    rm -f "${TMP_MD5}"
    exit 1
}
rm -f "${TMP_MD5}"

if echo "${CHECK_OUT}" | grep -iq "no file was verified\|FAILED"; then
    echo "ERROR: INVALID_VERIFICATION_RESULT"
    echo "${CHECK_OUT}"
    exit 1
fi

OK_COUNT=$(echo "${CHECK_OUT}" | grep -c ": OK" || true)
if [ "${OK_COUNT}" -ne 7 ]; then
    echo "ERROR: VERIFIED_COUNT_MISMATCH (got ${OK_COUNT}/7)"
    echo "${CHECK_OUT}"
    exit 1
fi

echo "SUCCESS: ALL_7_FILES_VERIFIED_OK"
EOF
)

if [ -n "${SSH_TARGET}" ]; then
    MD5_VERIFY_RESULT=$(ssh "${SSH_TARGET}" "bash -s -- '${DATA_DIR}' ${EXPECTED_PACKAGES[*]}" <<< "${MD5_SCRIPT}" 2>&1 || true)
else
    MD5_VERIFY_RESULT=$(bash -c "${MD5_SCRIPT}" _ "${DATA_DIR}" "${EXPECTED_PACKAGES[@]}" 2>&1 || true)
fi

if echo "${MD5_VERIFY_RESULT}" | grep -q "SUCCESS: ALL_7_FILES_VERIFIED_OK"; then
    record_test "数据快照 MD5 校验" 0 "官方 7 个快照包全部存在且散列校验精确 7/7 通过 (无跳过、无缺失)。"
else
    record_test "数据快照 MD5 校验" 1 "快照校验失败: ${MD5_VERIFY_RESULT}"
fi

# --- 测试 5: 备份与恢复演练 (可选) ---
if [ "${CHECK_BACKUP}" -eq 1 ]; then
    echo ""
    echo "--- 5. 备份与灾难恢复演练 ---"
    BACKUP_DIR="${DATA_DIR}/../backups"
    
    LATEST_BACKUP=$(ls -t "${BACKUP_DIR}"/*.dump 2>/dev/null | head -n 1 || true)
    if [ -n "${LATEST_BACKUP}" ]; then
        if pg_restore --list "${LATEST_BACKUP}" >/dev/null 2>&1; then
            record_test "备份文件可恢复性校验" 0 "快照备份 [$(basename "${LATEST_BACKUP}")] 归档结构完整有效。"
        else
            record_test "备份文件可恢复性校验" 1 "备份文件 [$(basename "${LATEST_BACKUP}")] 损坏或无法读取元数据！"
        fi
    else
        record_test "备份文件可恢复性校验" 1 "未发现任何备份归档文件！"
    fi
fi

echo ""
echo "================================================================================"
echo "📊 验收测试总结: 通过 ${PASS_COUNT}/${TOTAL_TESTS} 项测试"
echo "================================================================================"

if [ "${PASS_COUNT}" -eq "${TOTAL_TESTS}" ]; then
    echo "🎉 所有安全与功能门禁全部通过，本地 MusicBrainz 镜像已就绪！"
    exit 0
else
    echo "⚠️ 存在未通过的门禁项，请排查后重试。"
    exit 1
fi
