#!/bin/bash
set -e

# Configuration
# TODO: Change this to your Docker Hub username
DOCKER_USER="maifeipin"
APP_NAME="webmusic"
VERSION=$(git describe --tags --always --dirty)
DATE=$(date +%Y%m%d)

echo "=== Starting Release Build for $APP_NAME version $VERSION ==="

# 1. Build Frontend
echo ">>> Building Frontend Image..."
docker build -t $DOCKER_USER/$APP_NAME-frontend:$VERSION -f frontend/Dockerfile ./frontend
docker tag $DOCKER_USER/$APP_NAME-frontend:$VERSION $DOCKER_USER/$APP_NAME-frontend:latest

# 2. Build Backend
echo ">>> Building Backend Image..."
docker build -t $DOCKER_USER/$APP_NAME-backend:$VERSION -f backend/Dockerfile ./backend
docker tag $DOCKER_USER/$APP_NAME-backend:$VERSION $DOCKER_USER/$APP_NAME-backend:latest

# 3. Push to Docker Hub
# Note: You must be logged in (docker login)
echo ">>> Pushing Images to Docker Hub..."
# Uncomment to enable push
echo "Pushing frontend..."
docker push $DOCKER_USER/$APP_NAME-frontend:$VERSION
docker push $DOCKER_USER/$APP_NAME-frontend:latest

echo "Pushing backend..."
docker push $DOCKER_USER/$APP_NAME-backend:$VERSION
docker push $DOCKER_USER/$APP_NAME-backend:latest

echo "=== Release Complete! ==="
echo "Images:"
echo "  - $DOCKER_USER/$APP_NAME-frontend:$VERSION"
echo "  - $DOCKER_USER/$APP_NAME-backend:$VERSION"
