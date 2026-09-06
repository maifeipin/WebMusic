#!/usr/bin/env bash
set -euo pipefail

BASE_DIR="/home/liteagent/lite_agent"
BACKUP_DIR="$BASE_DIR/backup/webmusic_media"
LOG_FILE="$BASE_DIR/data/webmusic_media_backup.log"
TIMESTAMP="$(date +%Y%m%d_%H%M%S)"
FILE="$BACKUP_DIR/webmusic_postgres_${TIMESTAMP}.dump"

mkdir -p "$BACKUP_DIR" "$(dirname "$LOG_FILE")"
exec >>"$LOG_FILE" 2>&1
echo "[$(date '+%F %T')] starting WebMusic PostgreSQL backup"

# pg_dump streams a transaction-consistent snapshot without stopping WebMusic.
ssh -o BatchMode=yes -o ConnectTimeout=15 media \
  'docker exec webmusic-postgres pg_dump -U postgres -d webmusic -Fc' > "$FILE"

test -s "$FILE"
echo "[$(date '+%F %T')] dump created: $FILE ($(du -h "$FILE" | awk '{print $1}'))"

/usr/local/bin/bdpan mkdir lite-agent/webmusic_media >/dev/null 2>&1 || true
/usr/local/bin/bdpan upload "$FILE" "lite-agent/webmusic_media/$(basename "$FILE")"
echo "[$(date '+%F %T')] uploaded to Baidu Netdisk: lite-agent/webmusic_media/$(basename "$FILE")"

# Keep 14 local copies; cloud copies remain independently managed by bdpan.
find "$BACKUP_DIR" -type f -name 'webmusic_postgres_*.dump' -printf '%T@ %p\n' \
  | sort -nr | awk 'NR > 14 { sub(/^[^ ]+ /, ""); print }' \
  | xargs -r rm -f
echo "[$(date '+%F %T')] backup completed"
