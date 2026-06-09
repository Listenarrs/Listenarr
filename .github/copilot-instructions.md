# Listenarr Project Instructions

This is a complete C# .NET Web API backend with Vue.js frontend for automated audiobook downloading and processing.

## Project Overview
- **Backend**: ASP.NET Core Web API (.NET 10.0+ / net10.0) with modular service architecture
- **Frontend**: Vue.js 3 + TypeScript + Pinia + Vue Router + Vite
- **Purpose**: Search multiple APIs for audiobook torrents/NZBs, manage downloads via clients (qBittorrent, Transmission, SABnzbd, NZBGet), and process files with metadata using Audnexus API
- **Database**: SQLite with Entity Framework Core (ListenArrDbContext)

## Project Structure
```
Listenarr/
├── listenarr.api/                 # Backend API (Note: lowercase directory name!)
│   ├── Controllers/               # API endpoints
│   ├── Models/                    # Data models (Audiobook, SearchResult, Download, etc.)
│   ├── Services/                  # Business logic (Search, Metadata, DownloadMonitor, adapters)
│   └── Program.cs                # Application entry point
├── .env/development/config/       # Development runtime data (database, logs, cache)
├── fe/                           # Frontend Vue application
│   ├── src/
│   │   ├── components/           # Vue components (AudiobookModal, FolderBrowser, etc.)
│   │   ├── views/                # Pages (Dashboard, Search, Downloads, Settings)
│   │   ├── stores/               # Pinia stores for state management
│   │   ├── services/             # API client services
│   │   ├── types/                # TypeScript type definitions
│   │   └── router/               # Vue Router configuration
│   └── public/                   # Static assets (icon.png, logo.png)
│   └── package.json
├── .github/                      # GitHub configuration and assets
│   ├── copilot-instructions.md  # This file
│   ├── BRANDING.md              # Logo and branding guidelines
│   ├── logo-icon.png            # Brand icon (square format)
│   └── logo-full.png            # Full logo with text (horizontal)
├── package.json                  # Root package with concurrently scripts
├── docker-compose.yml            # Docker orchestration
├── listenarr.application/        # Application layer (services, interfaces)
├── listenarr.domain/             # Domain models and enums
├── listenarr.infrastructure/     # Persistence, adapters, EF Core configs
├── tests/                        # Unit and integration tests
├── tools/                        # Development utilities, including discord-bot
└── listenarr.slnx                # Visual Studio solution file
```

## Branding
The Listenarr logo combines headphones and a book to represent audiobook listening:
- **Primary Color**: `#2196F3` (Blue)
- **Icon**: `icon.png` - Square format for favicons and app icons
- **Full Logo**: `logo.png` - Horizontal format with text for headers
- **Format**: PNG with transparency for universal compatibility
- See `.github/BRANDING.md` for complete guidelines

## Key Features Implemented
- 🔍 **Multi-API Search**: Search across multiple torrent/NZB APIs simultaneously
- 📥 **Download Management**: Support for qBittorrent, Transmission, SABnzbd, NZBGet
- 🎵 **Metadata Integration**: Audible metadata via AudibleMetadataService and Audnexus API
- 🖼️ **Image Caching**: Automatic image caching with cleanup service
- 📁 **File Browser**: FolderBrowser component for path selection
- 📚 **Library Management**: AudiobookRepository with SQLite persistence
- ⚙️ **Configuration Management**: APIs, download clients, and settings via JSON
- 🖥️ **Modern Dashboard**: Statistics and quick actions
- 📱 **Responsive Design**: Mobile and desktop support

## Architecture Details

### Backend Services
- **SearchService**: Multi-API search coordination
- **AudibleMetadataService**: Fetch metadata from Audible/Audnexus
- **AmazonAsinService**: ASIN extraction from Amazon URLs
- **ImageCacheService**: Download and cache book cover images
- **ConfigurationService**: JSON-based settings management
- **AudiobookRepository**: Database operations (EF Core)

### Frontend State Management
- **Pinia Stores**: search, downloads, configuration, library
- **API Communication**: Type-safe HTTP client with Axios-style error handling
- **Reactive Updates**: Automatic refresh for active downloads

### Database
- **SQLite** via Entity Framework Core
- **Models**: Audiobook, SearchResult, Download, Configuration
- **Context**: ListenArrDbContext with automatic migrations

## How to Run This Project

### Prerequisites
- **.NET 10.0 SDK or later (net10.0)** - [Download](https://dotnet.microsoft.com/download)  

**Note:** Build/test environments in this repo target net10.0. Running with a different SDK may create build/run inconsistencies.
- **Node.js 24.x or later** - [Download](https://nodejs.org/)
- **npm** (comes with Node.js)

### Recommended: Single Command Start

Use the root npm scripts to run the development stack:

```bash
npm install          # First time only: installs concurrently
npm run dev          # Starts both API and Web with colored output
```

The scripts will:
1. ✅ Check prerequisites (Node.js, .NET SDK)
2. ✅ Install frontend dependencies if needed
3. ✅ Restore .NET dependencies
4. ✅ Start both servers with concurrently
5. ✅ Display colored console output (blue=API, green=WEB)

**Notes & troubleshooting:**
- Run the commands from the repository root (not the compiled `bin` folder) to ensure the development content root and database file are used (`.env/development/config/database/listenarr.db`). Running from `bin/Debug` can create a second, empty DB and cause confusing behavior.
- If code changes do not appear in a running instance, restart the API (`dotnet run` or stop/restart `npm run dev`) — hot-reload may not always pick up every change when files are locked.
- Development logs are written to `.env/development/config/logs/listenarr-YYYYMMDD.log`; check them when diagnosing background services or importer behavior.

**Default local URLs** (may vary if ports are in use):
- **Backend API**: http://localhost:4545 (override with `--urls` on `dotnet run`)
- **Frontend Web**: http://localhost:5173 (Vite will auto-select a different port if 5173 is in use)

### Manual Setup (Alternative)

If you prefer to start services separately:

**Terminal 1 - Backend:**
```bash
cd listenarr.api
dotnet restore       # First time only
dotnet run --urls http://localhost:4545
```

**Terminal 2 - Frontend:**
```bash
cd fe
npm install          # First time only
npm run dev
```

### Available npm Scripts (Root Directory)
```bash
npm run dev          # Start both API and Web (uses concurrently)
npm start            # Alias for 'npm run dev'
npm run dev:api      # Start only backend API
npm run dev:web      # Start only frontend web
npm run build        # Build both for production
npm run build:api    # Build only API (Release configuration)
npm run build:web    # Build only Web (production bundle)
npm run install:all  # Install frontend dependencies
npm test             # Run backend and frontend tests
```

### Docker Deployment
```bash
docker-compose up --build
```

## Troubleshooting & Debugging
- **Database duplication**: If you see different databases (empty vs populated) verify you started the app from the repo root (run `npm run dev` from project root). The working development DB is `.env/development/config/database/listenarr.db`.
- **Development logs**: Runtime logs are under `.env/development/config/logs/` (files like `listenarr-YYYYMMDD.log`). Tail them when diagnosing failures in background services (DownloadMonitorService, adapters).
- **Import issues**: Transmission/qBittorrent import problems often surface in `DownloadMonitorService` logs. Check for candidate detection messages and stability window logs.
- **Port conflicts**: Frontend (Vite) will auto-select another port if the default is in use; the console prints the chosen port.
- **Build/Run**: If `dotnet build` complains about locked files, stop the running API, then rebuild and restart.

## Important Directory Names
⚠️ **Note**: The backend directory is **lowercase** `listenarr.api`, not `ListenArr.Api`
- Backend: `listenarr.api/`
- Frontend: `fe/`
- Solution file references: Uses proper casing in `listenarr.slnx`

## API Endpoints

### Search
- `GET /api/search?query={query}` - Search configured APIs
- `POST /api/search/audible?query={query}` - Search Audible specifically

### Library
- `GET /api/library` - Get all audiobooks
- `GET /api/library/{id}` - Get specific audiobook
- `POST /api/library` - Add audiobook
- `PUT /api/library/{id}` - Update audiobook
- `DELETE /api/library/{id}` - Remove audiobook

### Configuration
- `GET /api/configuration` - Get all settings
- `POST /api/configuration` - Save settings

### Metadata
- `GET /api/audible/metadata?asin={asin}` - Get Audible metadata
- `POST /api/amazon/extract-asin` - Extract ASIN from URL

### File System
- `GET /api/filesystem/browse?path={path}` - Browse directories
- `GET /api/filesystem/drives` - Get available drives (Windows)

### Images
- `GET /api/v1/images/{filename}` - Get cached cover image

## Current Status
- **Development**: The project is actively developed and can be run locally. Many features are implemented and the dev environment supports rapid iteration.
- **Caveats**: There are known local-development pitfalls (e.g., multiple database files when running from different directories, hot-reload inconsistencies, and intermittent background import issues). When encountering problems, check logs and ensure you're running from the repository root.
- **Backend API**: Typically available at `http://localhost:4545` when running locally
- **Frontend Web**: Typically available at `http://localhost:5173` (Vite may use alternate port if default is busy)
- **Development database**: SQLite at `.env/development/config/database/listenarr.db`
- **Docker**: Ready for containerized deployment
- **Root npm scripts**: Automated development environment setup (use `npm run dev` to start both services)

## Development Workflow
1. Use `npm run dev` to run both services
2. Backend auto-restarts on C# file changes (with `dotnet watch`)
3. Frontend hot-reloads on Vue/TS file changes (Vite HMR)
4. Database migrations apply automatically on startup
5. Development image cache stored in `.env/development/config/cache/images/` (gitignored)

## Future Enhancements
- [ ] WebSocket for real-time download progress updates
- [ ] Enhanced error handling and validation
- [ ] User authentication system
- [ ] Advanced search filters and sorting
- [ ] Notification system integration (email, webhooks)
- [ ] Download queue management
- [ ] Automatic metadata tagging of downloaded files

---

## AI / Copilot Guidance 💡

### Core Development Principles
- **Primary edit targets:** Make feature and bug fixes in `listenarr.api/` (controllers), `listenarr.application/` (orchestration services), `listenarr.domain/` (domain models), and `listenarr.infrastructure/` (persistence/adapters).
- **Tests:** Update or add unit/integration tests under `tests/` when you change public behavior or DI signatures.
- **Discord bot:** The dev/test Discord stub lives at `tools/discord-bot` — prefer updating the `README.md` there or the stub only when necessary; tests expect this stub to exist.
- **Dev tools & scripts:** Misc scripts that are not API-specific may live under `tools/`, but major tooling is colocated with the API as shown above.
- **Runtime notes:** Always run from the **repository root** (e.g., `npm run dev`) so the app uses the development DB at `.env/development/config/database/listenarr.db` and log paths under `.env/development/config/logs/`.
- **Environment:** Project targets **.NET 10 (net10.0)** and Node.js **24.x+**. Use those versions for local dev and CI to avoid build/test inconsistencies.
- **Logging & debugging:** When adding diagnostics, prefer INFO-level logs for flow transitions and DEBUG for verbose data. Add clear early-return logs to background services (e.g., `DownloadMonitorService`) to make runtime behavior observable.

> Quick tip: if a change affects DI constructors, update tests to include any newly required parameters (or adjust constructors to provide backward-compatible defaults) so builds stay green.

### Critical Backend Architecture Patterns

#### Download Status Lifecycle
When processing completed downloads in `CompletedDownloadProcessor.cs`:
- **ALWAYS set `Status = DownloadStatus.Moved`** after successful file processing (8 locations in the code)
- Create history entries and notifications **BEFORE** cleanup operations
- For Transmission downloads: Extract torrent hash using `torrentInfo.HashString` (not `download.ExternalId`) for proper cleanup
- The status flow is: Queued → Downloading → Completed → **Moved** (terminal state)

#### File Existence Validation
When checking if an audiobook is "wanted" (missing files):
- **Check physical disk files**, not just database records
- Pattern: `a.Monitored && (a.Files == null || !a.Files.Any() || !a.Files.Any(f => !string.IsNullOrEmpty(f.Path) && System.IO.File.Exists(f.Path)))`
- Apply in 3 locations: `LibraryController.GetAllAudiobooks`, `LibraryController.GetAudiobook`, `ScanBackgroundService.BroadcastLibraryUpdate`
- This prevents false positives where DB records exist but files were deleted

#### Download Client Authentication
- **Transmission**: Implements 409/session-id retry pattern for CSRF protection
  - First request may return 409 with `X-Transmission-Session-Id` header
  - Retry with that header value for authentication
  - Pattern implemented in `PollTransmissionAsync` and `TransmissionAdapter`
- **qBittorrent**: Uses cookie-based session authentication
- **SABnzbd/NZBGet**: API key authentication

#### Background Services & Job Processing
- `DownloadProcessingBackgroundService`: Processes download queue, implements `ResetStuckJobsAsync()` on startup
- Jobs can get stuck in "Processing" state if service crashes mid-operation
- Reset stuck jobs automatically on service startup to prevent queue blockage
- Use `IsStopping` stability window (30 seconds) in `DownloadMonitorService` to prevent premature imports during shutdown

### Critical Frontend Architecture Patterns

#### Pinia Store Patterns
- **downloads.ts**: Manages download state, filters terminal states from active downloads
  - `activeDownloadsByAudiobook`: Exclude 'Completed', 'Moved', 'Failed', 'Cancelled' statuses
  - Use `queueItem.title` for title, NOT `contentPath` (property doesn't exist)
- **library.ts**: Manages audiobook library with SignalR real-time updates
- Always use computed properties for derived state, never mutate store state directly

#### Vue Component Best Practices
- Use `<script setup>` with TypeScript for all new components
- Use `v-memo` directive to optimize rendering of large lists (e.g., WantedView audiobook cards)
- Dependencies for v-memo should include all reactive values that affect the rendering
- Example: `v-memo="[audiobook, activeDownloads[audiobook.id]]"`

#### Visual Feedback for Downloads
When showing download status in views:
- Show icon indicator when downloads exist for an audiobook
- Add pulse/bounce animations for active downloads using CSS keyframes
- Filter downloads by audiobook using `activeDownloadsByAudiobook` computed property
- Example implementation in `WantedView.vue` (lines 362-372, 887-907)

#### Type Safety
- All API responses must have corresponding TypeScript types in `types/index.ts`
- Download status type includes: 'Queued' | 'Downloading' | 'Completed' | 'Paused' | 'Failed' | 'Cancelled' | 'Moved'
- Never reference non-existent properties on types (causes TS2339 errors)

### SignalR Real-Time Updates
- Backend broadcasts library changes via `AudiobookHub`
- Frontend listens in stores using `@microsoft/signalr` with connection management
- Key events: `AudiobookAdded`, `AudiobookUpdated`, `AudiobookDeleted`
- Always handle connection drops gracefully with reconnection logic

### Database Patterns (Entity Framework Core)
- `ListenArrDbContext` is the main database context
- Migrations in `listenarr.infrastructure/Migrations/`
- Apply migrations automatically on startup (no manual `dotnet ef database update` needed)
- Use async methods for all database operations
- Prefer `AsNoTracking()` for read-only queries to improve performance

### Common Troubleshooting Scenarios

#### Downloads Not Importing
1. Check logs in `.env/development/config/logs/listenarr-YYYYMMDD.log`
2. Look for authentication errors (401, 409, Unauthorized)
3. Verify DownloadMonitorService is running and detecting candidates
4. Check stability window logs (30-second delay before import)
5. Ensure files exist on disk and are accessible

#### Multiple Database Files
- Running from `bin/Debug` creates a second, empty database
- **Always run from repository root** (`npm run dev`)
- Development DB location: `.env/development/config/database/listenarr.db`

#### Hot Reload Not Working
- Backend: Stop and restart `dotnet run` if changes aren't reflected
- Frontend: Vite HMR should work automatically; if not, restart `npm run dev`
- Sometimes file locks prevent hot reload; full restart resolves this

### Security Considerations
- Never hardcode API keys or credentials (use `IConfiguration` with secure providers)
- Always use parameterized queries (EF Core does this automatically)
- Validate all user input, especially file paths (prevent path traversal)
- Image cache cleanup service runs automatically to prevent disk fill
- See `.github/AGENTS.md` for comprehensive secure coding guidelines
