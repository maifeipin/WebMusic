#!/usr/bin/env bash
set -euo pipefail

# Script to verify database schema fingerprint and optionally apply baseline migration
# Usage:
#   ./scripts/verify_and_apply_baseline.sh --check-only
#   ./scripts/verify_and_apply_baseline.sh --apply
# Or inside docker on MEDIA:
#   docker exec -it webmusic-backend-1 dotnet WebMusic.Backend.dll verify-baseline [--apply]

MODE="${1:---check-only}"

echo "=== 🔍 WebMusic Database Baseline Schema Verification ==="

if [ "$MODE" == "--apply" ]; then
    echo "Mode: Verification + Apply Baseline"
    dotnet run --project backend/WebMusic.Backend.csproj -- verify-baseline --apply
else
    echo "Mode: Check Only (Safe Dry-Run)"
    dotnet run --project backend/WebMusic.Backend.csproj -- verify-baseline
fi
