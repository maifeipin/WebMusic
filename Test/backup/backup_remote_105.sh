#!/bin/bash

# ==========================================
# WebMusic Remote Backup Script (Mac Client -> 105 Server)
# ==========================================

# 1. Configuration
# ----------------
REMOTE_USER="root"               # SSH Username for 105
REMOTE_HOST="192.168.2.105"      # SSH Host IP
CONTAINER_NAME="webmusic-postgres" # Docker Container Name on Remote
DB_USER="postgres"               # Database User
DB_NAME="webmusic"               # Database Name
BACKUP_DIR="./backups"           # Local Backup Directory

# 2. Preparation
# ----------------
mkdir -p "${BACKUP_DIR}"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
BACKUP_FILE="${BACKUP_DIR}/webmusic_105_${TIMESTAMP}.sql"

echo "=== 🚀 Starting Remote Backup ==="
echo "From: ${REMOTE_USER}@${REMOTE_HOST} (Container: ${CONTAINER_NAME})"
echo "To:   ${BACKUP_FILE}"

# 3. Execution (SSH Tunneling)
# ----------------
# We use 'ssh' to execute 'docker exec pg_dump' remotely and stream output to local file.
# This avoids version mismatch issues and doesn't require pg_dump on Mac.
ssh "${REMOTE_USER}@${REMOTE_HOST}" "docker exec ${CONTAINER_NAME} pg_dump -U ${DB_USER} ${DB_NAME}" > "${BACKUP_FILE}"

EXIT_CODE=$?

# 4. Validation
# ----------------
if [ $EXIT_CODE -eq 0 ] && [ -s "${BACKUP_FILE}" ]; then
    SIZE=$(du -h "${BACKUP_FILE}" | cut -f1)
    echo ""
    echo "✅ Backup Successful!"
    echo "📂 Saved to: ${BACKUP_FILE}"
    echo "📦 Size: ${SIZE}"
else
    echo ""
    echo "❌ Backup Failed!"
    echo "Check if:"
    echo "  1. SSH connection is working (ssh ${REMOTE_USER}@${REMOTE_HOST})"
    echo "  2. Docker container '${CONTAINER_NAME}' is running on remote"
    # Remove empty file if failed
    if [ -f "${BACKUP_FILE}" ]; then
        rm "${BACKUP_FILE}"
    fi
fi
