# WebMusic

A modern, web-based music player and library manager designed for NAS (Network Attached Storage) and SMB shares. Built with .NET 8 (Backend) and React + Vite (Frontend).

**Live Demo**: [https://music.maifeipin.com/sources](https://music.maifeipin.com/sources)

## Features

### Core Integration
- **SMB Integration**: Directly connect to Windows Shares / SMB servers to scan and stream music.
  
  ![Connection Manager](docs/screenshots/sources.png)
- **Background Scanning**: Asynchronous scanning pipeline with real-time status updates, capable of handling large libraries without timeout.
- **Deduplication**: Intelligent handling of physical files to prevent duplicate library entries across multiple shares.

### Playback & Audio
- **Global Player**: Persistent playback bar with minimize/maximize support, play queue, and improved seek controls.
- **Transcoding**: Automatic transcoding (via FFmpeg) for unsupported formats (FLAC/ALAC -> MP3/AAC) with seeking support.
- **Smart Queue**: Add songs, folders, or entire groups to queue.

### User Experience
- **Modern Dashboard**: Auto-responsive layout featuring "Recently Played", "Favorites", and Library Stats.
  
  ![Dashboard](docs/screenshots/dashboard.png)
- **Directory Browser**: Interactive file browser to easily select SMB shares and folders.
- **Library Views**:
    - **Flat View**: Sortable list of all songs with Path column.
    - **Group View**: Browse by Artist, Album, Genre, or Year.
    - **Directory View**: Navigate your physical folder structure with breadcrumbs.
  
  ![Library Browser](docs/screenshots/library.png)
- **User Profile**:
    - Listening History and Favorites Management.
    - Secure "Change Password" functionality.

## Tech Stack

### Backend
- **Framework**: ASP.NET Core 8 Web API
- **Database**: SQLite with Entity Framework Core
- **Architecture**:
  - `BackgroundService` for async scanning.
  - `ISmbService` for file operations.
  - `JWT` Authentication with flexible claim mapping.

### Frontend
- **Framework**: React 18 + Vite
- **Styling**: Tailwind CSS v4 + Lucide Icons
- **State**: Context API (Auth, Player)
- **HTTP**: Axios with centralized API service.

## Deployment (Docker)

Recommended for production usage.

1. **Clone the repository**:
   ```bash
   git clone https://github.com/maifeipin/WebMusic.git
   cd WebMusic
   ```

2. **Start with Docker Compose**:
   ```bash
   docker-compose up -d --build
   ```
   - **Frontend**: `http://localhost:8090`
   - **Backend**: `http://localhost:5080` (Internal use)

3. **Login**:
   - Default credentials: `admin` / `admin`
   - **IMPORTANT**: Go to **Profile** (bottom left) and change your password immediately.

## Local Development

For core contributors.

1. **Backend**:
   ```bash
   cd backend
   dotnet restore
   dotnet run
   ```
   Runs on `http://localhost:5098`.

2. **Frontend**:
   ```bash
   cd frontend
   npm install
   npm run dev
   ```
   Runs on `http://localhost:5173`.
   
   *Note: Frontend is configured to proxy `/api` requests to the local backend port 5098.*

## CI/CD Workflow

Before pushing code, ensure local validation passes:
1. Verify Backend Build: `cd backend && dotnet build`
2. Verify Frontend Build: `cd frontend && npm run build`
3. Push changes.

---
*Created by [maifeipin](https://github.com/maifeipin)*
