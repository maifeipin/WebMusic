#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

MODE="${MODE:-full}"
MIRROR_VERSION="${MIRROR_VERSION:-}"
APPLY_IDENTITIES="${APPLY_IDENTITIES:-false}"
START_AFTER_ID="${START_AFTER_ID:-0}"
MAX_BATCHES="${MAX_BATCHES:-0}"
BATCH_SIZE="${BATCH_SIZE:-100}"
REPORT_ROOT="${REPORT_ROOT:-${PROJECT_ROOT}/reports/local-identity-runs}"

if [[ "${MODE}" != "full" && "${MODE}" != "incremental" ]]; then
  echo "MODE must be 'full' or 'incremental'." >&2
  exit 2
fi
if [[ -z "${MIRROR_VERSION}" ]]; then
  echo "MIRROR_VERSION is required (for example v-2026-07-30.1:20260902-002507)." >&2
  exit 2
fi
if ! [[ "${BATCH_SIZE}" =~ ^[0-9]+$ ]] || (( BATCH_SIZE < 1 || BATCH_SIZE > 100 )); then
  echo "BATCH_SIZE must be between 1 and 100." >&2
  exit 2
fi
if ! [[ "${START_AFTER_ID}" =~ ^[0-9]+$ ]] || ! [[ "${MAX_BATCHES}" =~ ^[0-9]+$ ]]; then
  echo "START_AFTER_ID and MAX_BATCHES must be non-negative integers." >&2
  exit 2
fi

mkdir -p "${REPORT_ROOT}"
LOCK_DIR="${REPORT_ROOT}/.scan.lock"
if ! mkdir "${LOCK_DIR}" 2>/dev/null; then
  echo "Another local identity scan orchestrator is active: ${LOCK_DIR}" >&2
  exit 3
fi
trap 'rmdir "${LOCK_DIR}" 2>/dev/null || true' EXIT INT TERM

RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_DIR="${REPORT_ROOT}/${RUN_ID}-${MODE}"
mkdir -p "${RUN_DIR}"

cursor="${START_AFTER_ID}"
batch=1
total_evaluated=0
total_matched=0
total_created=0

cd "${PROJECT_ROOT}"
echo "Run directory: ${RUN_DIR}"
echo "Mode=${MODE} ApplyIdentities=${APPLY_IDENTITIES} MirrorVersion=${MIRROR_VERSION} StartAfterId=${cursor}"

while :; do
  if (( MAX_BATCHES > 0 && batch > MAX_BATCHES )); then
    echo "Reached MAX_BATCHES=${MAX_BATCHES}; resume with START_AFTER_ID=${cursor}."
    break
  fi

  report_name="batch_$(printf '%06d' "${batch}")_after_${cursor}.json"
  host_report="${RUN_DIR}/${report_name}"
  container_report="/reports/local-identity-runs/${RUN_ID}-${MODE}/${report_name}"
  args=(local-identity-auto-scan --count "${BATCH_SIZE}" --after-id "${cursor}" --mirror-version "${MIRROR_VERSION}" --allow-partial --out "${container_report}")
  if [[ "${MODE}" == "incremental" ]]; then
    args+=(--incremental)
  fi
  if [[ "${APPLY_IDENTITIES}" == "true" ]]; then
    args+=(--persist-state --apply-identities)
  elif [[ "${APPLY_IDENTITIES}" != "false" ]]; then
    echo "APPLY_IDENTITIES must be 'true' or 'false'." >&2
    exit 2
  fi

  docker compose run --rm backend "${args[@]}"

  read -r evaluated matched created next_cursor < <(python3 -c '
import json, sys
with open(sys.argv[1], encoding="utf-8") as handle:
    report = json.load(handle)
cursor = report.get("ContinuationAfterMediaFileId")
print(report["Evaluated"], report["Matched"], report.get("IdentitiesCreated", 0), "null" if cursor is None else cursor)
' "${host_report}")

  total_evaluated=$((total_evaluated + evaluated))
  total_matched=$((total_matched + matched))
  total_created=$((total_created + created))
  echo "Batch ${batch}: evaluated=${evaluated} matched=${matched} identitiesCreated=${created} continuation=${next_cursor}"

  if (( evaluated == 0 )) || [[ "${next_cursor}" == "null" ]]; then
    echo "Catalog scan reached the end."
    break
  fi
  if (( next_cursor <= cursor )); then
    echo "Continuation failed to advance (${cursor} -> ${next_cursor}); aborting." >&2
    exit 4
  fi

  cursor="${next_cursor}"
  batch=$((batch + 1))
done

echo "Completed: evaluated=${total_evaluated} matched=${total_matched} identitiesCreated=${total_created} continuation=${cursor}"
