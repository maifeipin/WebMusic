#!/bin/bash

# Load environment variables from project root .env
# Go up two levels to find .env (Test/backup/ -> Test/ -> root)
ENV_FILE="../../.env"

if [ -f "$ENV_FILE" ]; then
    export $(grep -v '^#' $ENV_FILE | xargs)
else
    echo "Error: .env file not found at $ENV_FILE"
    exit 1
fi

# Configuration
BACKUP_DIR="./backups"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
FILENAME="${BACKUP_DIR}/webmusic_backup_${TIMESTAMP}.sql"

# Ensure backup directory exists
mkdir -p $BACKUP_DIR

echo "=== Starting Backup ==="
echo "Host: $POSTGRES_HOST"
echo "Database: $POSTGRES_DB"
echo "Output: $FILENAME"

# Execute Dump using Docker (if local) or PGPASSWORD (if remote tool installed)
# We assume running on a machine with docker access to 'webmusic-postgres' OR has pg_dump installed.
# Strategy: Try using the docker container 'webmusic-postgres' to dump itself (most reliable on NAS).

if docker ps | grep -q "webmusic-postgres"; then
    echo "Method: Docker Exec (Local Container)"
    docker exec -t webmusic-postgres pg_dump -U $POSTGRES_USER $POSTGRES_DB > $FILENAME
else
    echo "Method: pg_dump (Remote/Local Binary)"
    # Check if pg_dump exists locally
    if command -v pg_dump &> /dev/null; then
        PGPASSWORD=$POSTGRES_PASSWORD pg_dump -h $POSTGRES_HOST -U $POSTGRES_USER $POSTGRES_DB > $FILENAME
    else
        echo "Method: Docker Run (Ephemeral Container)"
        # Use a temporary postgres container to run pg_dump against the remote host
        # We mount the current directory to /backup to save the file
        
        # Determine current absolute directory for mounting (Handles relative paths)
        ABS_BACKUP_DIR=$(cd "$BACKUP_DIR" && pwd)
        BACKUP_FILE_NAME=$(basename "$FILENAME")

        docker run --rm \
            -v "$ABS_BACKUP_DIR:/backup_out" \
            -e PGPASSWORD=$POSTGRES_PASSWORD \
            postgres:15-alpine \
            pg_dump -h $POSTGRES_HOST -U $POSTGRES_USER $POSTGRES_DB -f "/backup_out/$BACKUP_FILE_NAME"
    fi
fi

if [ $? -eq 0 ]; then
    echo "✅ Backup Successful!"
    echo "Size: $(du -h $FILENAME | cut -f1)"
else
    echo "❌ Backup Failed!"
    rm -f $FILENAME
    exit 1
fi

# Optional: Retention Policy (Keep last 7 days)
# find $BACKUP_DIR -name "webmusic_backup_*.sql" -mtime +7 -delete
