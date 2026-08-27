#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

: "${GHCR_USERNAME:?GHCR_USERNAME is required}"
: "${GHCR_READ_TOKEN:?GHCR_READ_TOKEN is required}"

echo "$GHCR_READ_TOKEN" | docker login ghcr.io -u "$GHCR_USERNAME" --password-stdin
docker compose pull backend frontend
docker compose up -d --no-build backend frontend
docker image prune -f

test "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:8090/)" = "200"
test "$(curl -sS -o /dev/null -w '%{http_code}' 'http://127.0.0.1:5080/api/media?page=1&pageSize=1')" = "401"
echo "Deployment health checks passed."
