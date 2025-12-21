# WebMusic

A modern, **Multi-User** web-based music player and library manager designed for NAS (Network Attached Storage) and SMB shares. Built with .NET 8 (Backend) and React + Vite (Frontend).

**Live Demo**: [https://music.maifeipin.com/sources](https://music.maifeipin.com/sources)
> **Public Demo Account**:  
> Username: `demo`  
> Password: `demo123`  
> *Note: This account is isolated in a private environment and cannot access other users' data.*

## Features

### Core Integration
### Core Integration
- **Database Agnostic**: Support for both **PostgreSQL** (Production) and **SQLite** (Development/Portable). Zero-config setup.
- **SMB Integration**: Directly connect to Windows Shares / SMB servers to scan and stream music.
  
  ![Connection Manager](docs/screenshots/sources.png)
- **Background Scanning**: Asynchronous scanning pipeline with real-time status updates, capable of handling large libraries without timeout.
- **Deduplication**: Intelligent handling of physical files to prevent duplicate library entries across multiple shares.
- **Multi-Tenant Architecture** (New in v2.7.0):
    - **Physical Data Isolation**: Each user can configure their own private SMB Sources and Credentials.
    - **Shared vs Private**: Admins can expose Public Libraries, while users keep their personal NAS shares private.
    - **Secure Credentials**: Storage credentials are encrypted and owned strictly by the creator.

### AI Tag Manager 🤖
- **Intelligent Metadata Cleanup**: Use **Google Gemini AI** models (Flash 2.0/1.5) to automatically analyze filenames and suggest accurate Artist/Title tags.
- **Background Batch Processing**: Select whole folders or playlists to process in the background. The server handles rate-limiting and updates automatically.
- **Diff View**: Preview changes side-by-side before applying them to your database.
- **Smart Prompts**: Built-in templates for common tasks like "Fix Encoding" or "Genre Classification".

### Library Management
- **Shared Playlists**: Create public, shareable links for your playlists with optional passwords and expiration dates.
- **Backup & Restore**: Full JSON export/import of library metadata and Favorites lists. Supports appending or overwriting data for migration.
- **Cover Art**: Upload custom cover images or fetch from SMB automatically.

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
- **Database**: PostgreSQL (Containerized) / SQLite
- **AI Integration**: Google Gemini API for intelligent tagging.
- **Architecture**:
  - `BackgroundService` for async scanning.
  - `ISmbService` for file operations.
  - `JWT` Authentication with flexible claim mapping.

### Frontend
- **Framework**: React 18 + Vite
- **Styling**: Tailwind CSS v4 + Lucide Icons
- **State**: Context API (Auth, Player)
- **HTTP**: Axios with centralized API service.

## Installation

### Option A: Docker Deployment (Recommended)
You don't need to clone the code. Just create a `docker-compose.yml` file:

```yaml
version: '3.8'

services:
  backend:
    image: maifeipin/webmusic-backend:latest
    container_name: webmusic-backend
    restart: unless-stopped
    ports:
      - "5080:8080"
    environment:
      - ASPNETCORE_URLS=http://+:8080
      - DatabaseProvider=Postgres
      - ConnectionStrings__Postgres=Host=postgres;Database=webmusic;Username=postgres;Password=password
      # Change this!
      - Jwt__Key=ReplaceWithSuperSecureKey1234567890
      - Gemini__ApiKeys__0=YOUR_GEMINI_API_KEY
    depends_on:
      - postgres
    volumes:
      - ./data:/app/data

  frontend:
    image: maifeipin/webmusic-frontend:latest
    container_name: webmusic-frontend
    restart: unless-stopped
    ports:
      - "8090:80"
    depends_on:
      - backend

  postgres:
    image: postgres:15-alpine
    container_name: webmusic-postgres
    restart: unless-stopped
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: password
      POSTGRES_DB: webmusic
    volumes:
      - postgres-data:/var/lib/postgresql/data

volumes:
  postgres-data:
```

2. **Start the server**:
   ```bash
   docker-compose up -d
   ```
3. **Login**:
   - URL: `http://localhost:8090`
   - Default: `admin` / `admin`

### Option B: Build from Source
Recommended for developers.

1. **Clone the repository**:
   ```bash
   git clone https://github.com/maifeipin/WebMusic.git
   cd WebMusic
   ```

2. **Configure Secrets**:
   Ensure `docker-compose.yml` environment variables are set correctly.

3. **Start with Docker Compose**:
   ```bash
   docker-compose up -d --build
   ```

## Migration (SQLite to PostgreSQL)

If you are upgrading from an older version using SQLite, you can use the provided script to migrate your data (Users, Playlists, Play History, etc.) to the new PostgreSQL database.

1. **Ensure Prerequisites**:
   - Python 3 installed.
   - `webmusic-postgres` container is running.
   - Your existing SQLite database is at `data/webmusic.db` (or updated path in script).

2. **Run Migration**:
   ```bash
   # Install dependency
   pip install psycopg2-binary

   # Run the script
   python3 Test/migration/migrate_sqlite_to_pg.py
   ```
   *Note: This script will automatically create the necessary table schema in PostgreSQL if it doesn't exist.*

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
