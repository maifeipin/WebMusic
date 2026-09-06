#!/usr/bin/env bash
set -euo pipefail

# Configuration
API_URL="${WEBMUSIC_URL:-https://music.maifeipin.com}"
USERNAME="${BOT_USERNAME:-enrichment-bot}"
PASSWORD="${BOT_PASSWORD:-}"
TARGET_USER_ID="${TARGET_USER_ID:-1}"
BATCH_SIZE="${BATCH_SIZE:-100}"

if [ -z "$PASSWORD" ]; then
  echo "❌ Error: BOT_PASSWORD environment variable must be set (no default password permitted)."
  echo "Usage: BOT_PASSWORD='...' ./scripts/run_enrichment_task.sh"
  exit 1
fi

echo "=== 🤖 WebMusic Dedicated Enrichment Task Runner ==="
echo "Endpoint:        $API_URL"
echo "Bot Username:    $USERNAME"
echo "Target User ID:  $TARGET_USER_ID"
echo "Batch Size:      $BATCH_SIZE (Enforced max: 100)"
echo ""

# 1. Login to retrieve JWT
echo "🔑 Logging in as $USERNAME..."
LOGIN_PAYLOAD=$(printf '{"username":"%s","password":"%s"}' "$USERNAME" "$PASSWORD")
LOGIN_RESP=$(curl -sS -X POST "$API_URL/api/auth/login" \
  -H "Content-Type: application/json" \
  -d "$LOGIN_PAYLOAD")

TOKEN=$(echo "$LOGIN_RESP" | grep -o '"token":"[^"]*' | cut -d'"' -f4 || true)

if [ -z "$TOKEN" ]; then
  echo "❌ Login failed! Server response:"
  echo "$LOGIN_RESP"
  exit 1
fi
echo "✅ Authenticated successfully."

AUTH_HEADER="Authorization: Bearer $TOKEN"

# 2. Check preview of eligible tracks
echo "🔍 Checking preview for user $TARGET_USER_ID..."
PREVIEW_RESP=$(curl -sS -X GET "$API_URL/api/enrichment/favorites/preview?targetUserId=$TARGET_USER_ID" \
  -H "$AUTH_HEADER")

TOTAL_ELIGIBLE=$(echo "$PREVIEW_RESP" | grep -o '"total":[0-9]*' | cut -d':' -f2 || echo "0")
echo "📊 Eligible favorite tracks missing cover or lyrics: $TOTAL_ELIGIBLE"

if [ "$TOTAL_ELIGIBLE" -le 0 ]; then
  echo "🎉 All favorite tracks already have covers and lyrics. Nothing to enrich."
  exit 0
fi

# 3. Start enrichment batch
echo "🚀 Dispatching enrichment batch (batchSize=$BATCH_SIZE, targetUserId=$TARGET_USER_ID)..."
START_RESP=$(curl -sS -X POST "$API_URL/api/enrichment/favorites/start?batchSize=$BATCH_SIZE&targetUserId=$TARGET_USER_ID" \
  -H "$AUTH_HEADER")

BATCH_ID=$(echo "$START_RESP" | grep -o '"batchId":"[^"]*' | cut -d'"' -f4 || true)
TOTAL_IN_BATCH=$(echo "$START_RESP" | grep -o '"total":[0-9]*' | cut -d':' -f2 || echo "0")

if [ -z "$BATCH_ID" ] || [ "$BATCH_ID" = "null" ]; then
  echo "⚠️ Could not start batch. Response: $START_RESP"
  exit 0
fi

echo "✅ Batch dispatched successfully!"
echo "   Batch ID: $BATCH_ID"
echo "   Songs in batch: $TOTAL_IN_BATCH"
echo ""

# 4. Monitor batch progress
echo "⏳ Monitoring progress..."
while true; do
  STATUS_RESP=$(curl -sS -X GET "$API_URL/api/enrichment/$BATCH_ID" \
    -H "$AUTH_HEADER")

  STATUS=$(echo "$STATUS_RESP" | grep -o '"status":"[^"]*' | cut -d'"' -f4 || echo "Unknown")
  PROCESSED=$(echo "$STATUS_RESP" | grep -o '"processed":[0-9]*' | cut -d':' -f2 || echo "0")
  TOTAL=$(echo "$STATUS_RESP" | grep -o '"total":[0-9]*' | cut -d':' -f2 || echo "$TOTAL_IN_BATCH")
  UPDATED=$(echo "$STATUS_RESP" | grep -o '"updated":[0-9]*' | cut -d':' -f2 || echo "0")
  UNMATCHED=$(echo "$STATUS_RESP" | grep -o '"unmatched":[0-9]*' | cut -d':' -f2 || echo "0")
  SKIPPED=$(echo "$STATUS_RESP" | grep -o '"skipped":[0-9]*' | cut -d':' -f2 || echo "0")
  FAILED=$(echo "$STATUS_RESP" | grep -o '"failed":[0-9]*' | cut -d':' -f2 || echo "0")

  echo "[$(date +'%H:%M:%S')] Status: $STATUS | Progress: $PROCESSED/$TOTAL | Updated: $UPDATED | Unmatched: $UNMATCHED | Skipped: $SKIPPED | Failed: $FAILED"

  if [ "$STATUS" = "Completed" ] || [ "$STATUS" = "Failed" ] || [ "$STATUS" = "Cancelled" ]; then
    break
  fi

  sleep 5
done

echo ""
echo "=== 🏁 Batch $BATCH_ID Finished with status: $STATUS ==="

# 5. Fetch audit attempts summary
echo "📋 Fetching recent attempt audit records..."
ATTEMPTS_RESP=$(curl -sS -X GET "$API_URL/api/enrichment/attempts/$BATCH_ID?limit=20" \
  -H "$AUTH_HEADER")

echo "Attempts Audit Data:"
echo "$ATTEMPTS_RESP"
echo ""
echo "✅ Automation task completed."
