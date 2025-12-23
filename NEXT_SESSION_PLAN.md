# 🌙 Night Session Plan (Handover)

## ✅ Completed Today
1. **AI Lyrics Fix**: Resolved `STATUS_SHARING_VIOLATION` by relaxing SMB share capability flags.
2. **UX Improvement**: Implemented Global 401 Interceptor in Frontend to auto-redirect to login on token expiry.
3. **Scanner Upgrade**: Added `.ogg` and `.opus` support to Backend Scanner and File Manager.
4. **File Manager Fix**: Allowed file uploads to the root directory (fixed `path` required error).
5. **Plugin System (Infrastructure)**:
   - **Backend**: Created `PluginDefinition` model, `PluginsController` (with Reverse Proxy), and `v1.2` DB migration.
   - **Frontend**: Created `PluginsPage` (Dashboard) and `PluginViewPage` (IFrame Viewer).
   - **Navigation**: Added "Extensions" menu to the Sidebar.

## 🚧 To Do (Next Session)

### 1. Infrastructure (Docker)
- [ ] Edit `docker-compose.yml` to utilize the Plugin System.
- [ ] Add `netease-cloud-music-api` service container.

### 2. Deployment (Production)
- [ ] `git pull origin main`
- [ ] `docker-compose up -d --build` (Rebuild Backend & Frontend)
- [ ] Run Migration: `python3 Test/migrations/upgrade_db_v1.2.py`

### 3. Verification
- [ ] Login and verify "Extensions" menu appears.
- [ ] Go to Extensions -> Install Plugin -> Add Netease API (Internal Docker URL).
- [ ] Verify Proxy works (click "Open App").

### 4. Integration
- [ ] (Optional) Develop a specific "Metadata Fetcher" plugin logic or just use the UI for now.
