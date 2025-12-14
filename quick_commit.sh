#!/bin/bash
set -e

echo "=== Staging Changes ==="
git add .

echo "=== Committing ==="
git commit -m "feat(ai): Implement Zero-Mount Architecture for AI Lyrics" \
           -m "- Backend: Refactor LyricsService to send SMB credentials instead of file streams." \
           -m "- AI: Implement direct SMB file fetching using smbprotocol." \
           -m "- AI: Add STATUS_END_OF_FILE handling and configurable SMB connection." \
           -m "- Frontend: Add language selection and prompt input for lyrics generation." \
           -m "- Config: Remove all Docker volume mounts (Zero-Mount)." \
           -m "- Docs: Update .env.example with 4 deployment scenarios."

echo "=== Pushing to Remote ==="
git push

echo "=== Done! ==="
