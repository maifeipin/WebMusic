#!/usr/bin/env bash
set -euo pipefail

echo "=== 🚀 Deploying WebMusic to MEDIA host ==="
ssh media 'bash -s' << 'EOF'
set -euo pipefail
cd /root/WebMusic

echo "📥 Pulling latest images from GHCR..."
docker compose pull backend frontend

echo "🔄 Recreating backend and frontend containers..."
docker compose up -d --no-build backend frontend

echo "🧹 Cleaning up dangling images..."
docker image prune -f

echo "🩺 Verifying health endpoints (waiting for startup)..."
sleep 3
frontend_code=$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:8090/)
backend_code=$(curl -sS -o /dev/null -w '%{http_code}' 'http://127.0.0.1:5080/api/media?page=1&pageSize=1')

echo "Frontend HTTP Status: $frontend_code (Expected: 200)"
echo "Backend Unauthorized API HTTP Status: $backend_code (Expected: 401)"

test "$frontend_code" = "200"
test "$backend_code" = "401"

echo "✅ MEDIA deployment and health checks passed!"
EOF
