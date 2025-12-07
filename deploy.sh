#!/bin/bash

# Ensure we are in the script's directory
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$DIR"

echo "=== 🚀 Starting WebMusic Deployment ==="

# 1. Pull latest code
echo "📥 Pulling latest code..."
git pull

# 2. Build and Start Containers
echo "🏗️  Building and Starting Containers..."
docker-compose up -d --build

# 3. Cleanup Old Images to Save Space
echo "🧹 Cleaning up old/dangling images..."
# -f forces cleanup without prompt
# This removes "dangling" images (the ones replaced by the new build)
if command -v docker &> /dev/null; then
    docker image prune -f
else
    echo "⚠️ 'docker' command not found. Skipping image cleanup."
fi

echo "=== ✅ Deployment Complete! ==="
echo "Frontend: http://localhost:8090"
echo "Backend:  http://localhost:5098"
