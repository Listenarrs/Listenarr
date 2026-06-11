# WARP.md

This file provides guidance to WARP (warp.dev) when working with code in this repository.

## Required Repository Context

Before making code, dependency, workflow, or documentation changes, review and follow `CONTRIBUTING.md`, `BACKEND_ARCHITECTURE.md`, `.github/RULES.md`, and the primary AI guidance files. Repository-specific guidance takes precedence over generic tool guidance.

## Project Overview

Listenarr is an automated audiobook collection management system built as a full-stack application with a C# .NET 10 backend API and Vue.js 3 frontend. The project follows a monorepo structure with integrated build processes.

**Core Purpose**: Search multiple APIs for audiobook torrents/NZBs, manage downloads via popular clients (qBittorrent, Transmission, SABnzbd, NZBGet), and process files with rich metadata integration.

## Development Commands

### Prerequisites
- .NET 10.0 SDK or later
- Node.js 24.x or later (specified in `fe/package.json` engines)

### Common Development Tasks

```powershell
# Start both API and Web servers (recommended)
npm run dev

# Start only backend API (runs on http://localhost:4545)
npm run dev:api

# Start only frontend web (runs on http://localhost:5173)  
npm run dev:web

# Install frontend dependencies
npm run install:all

# Build for production
npm run build

# Run frontend tests
npm test

# Run a single test file
cd fe
npm run test:unit -- src/__tests__/specific-test.spec.ts

# Lint and format frontend code
cd fe
npm run lint
npm run format
```

### Backend-Specific Commands

```powershell
# Run API with custom URL/port
cd listenarr.api
dotnet run --urls "http://localhost:5656"

# Build API in Release mode
cd listenarr.api
dotnet build -c Release

# Publish with integrated frontend build
dotnet publish listenarr.api/Listenarr.Api.csproj -c Release -o ./publish/local

# Publish API only (skip frontend build, useful when Node.js unavailable)
dotnet publish listenarr.api/Listenarr.Api.csproj -c Release -o ./publish/local /p:SkipFrontendBuild=true

# Database migrations
cd listenarr.api
dotnet ef migrations add NewMigrationName
dotnet ef database update
```

### Testing and Quality

```powershell
# Run frontend unit tests with Vitest
cd fe
npm run test:unit

# Run E2E tests with Cypress
cd fe  
npm run test:e2e

# Open Cypress interactively for debugging
cd fe
npm run test:e2e:dev
```

## Architecture Overview

### Monorepo Structure with Integrated Build

The project uses a **CI-first monorepo build approach** where the backend publish automatically builds and includes the frontend:

- **Root**: Contains orchestration scripts and Docker configuration
- **`listenarr.api/`**: C# backend with integrated MSBuild frontend targets
- **`fe/`**: Vue.js frontend that gets built into `listenarr.api/wwwroot/` during API publish

The API's `.csproj` contains MSBuild targets that automatically:
1. Run `npm ci` in the frontend directory
2. Run `npm run build` to create `fe/dist/`
3. Copy the built frontend to API's `wwwroot/` folder

This produces a single deployment artifact containing both backend and frontend.

### Backend Architecture (.NET 10 Web API)

**Service-Oriented Architecture** with dependency injection:

- **Controllers/**: API endpoints organized by feature (Search, Library, Configuration, etc.)
- **Services/**: Business logic with interface-based abstractions
  - `SearchService`: Multi-API search coordination across torrent/NZB indexers
  - `AudibleMetadataService`: Metadata fetching from Audible/Audnexus API
  - `ImageCacheService`: Automatic image downloading and caching with cleanup
  - `DownloadService`: Integration with download clients (qBittorrent, Transmission, etc.)
  - `AudioFileService`: Audio metadata extraction using FFprobe
- **Models/**: Entity Framework Core data models with SQLite
- **Hubs/**: SignalR hubs for real-time updates (download progress, etc.)
- **Background Services**: Multiple hosted services for async processing:
  - `DownloadMonitorService`: Real-time download status monitoring
  - `AutomaticSearchService`: Scheduled audiobook searching
  - `ScanBackgroundService`: File system scanning and metadata extraction
  - `ImageCacheCleanupService`: Daily cleanup of cached images

**Database**: SQLite with Entity Framework Core using `ListenArrDbContext`. Complex properties (lists, JSON) are converted to pipe-separated strings or JSON for storage.

### Frontend Architecture (Vue.js 3 + TypeScript)

**Modern Vue.js SPA** with composition API:

- **views/**: Page components (Dashboard, Search, Library, Settings, etc.)
- **components/**: Reusable UI components (modals, forms, browsers)
- **stores/**: Pinia state management (search, downloads, library, configuration)
- **services/**: API communication layer with type-safe HTTP client
- **types/**: TypeScript interfaces shared between frontend and API responses

**Key Frontend Patterns**:
- Composition API with `<script setup>` syntax
- Pinia stores for reactive state management
- SignalR integration for real-time updates
- Phosphor Icons for consistent iconography

### Data Flow and Integration

1. **Search Flow**: Multi-API search via `SearchService` → Results cached → Frontend displays with metadata enrichment
2. **Download Flow**: User initiates → `DownloadService` → External client API → Background monitoring → SignalR updates → Frontend updates
3. **Library Flow**: File scanning → Metadata extraction → Database persistence → Cache management
4. **Real-time Updates**: SignalR hubs push updates to connected frontend clients

## Key Design Patterns

### Critical Backend Patterns (Recent Fixes)

#### Download Status Lifecycle
- **ALWAYS set `Status = DownloadStatus.Moved`** after successful import (8 locations in CompletedDownloadProcessor.cs)
- Create history entries and notifications **BEFORE** cleanup operations
- For Transmission: Extract torrent hash using `torrentInfo.HashString` (not `download.ExternalId`)
- Status flow: Queued → Downloading → Completed → Moved (terminal state)

#### File Existence Validation
- **Check physical disk files**, not just database records
- Pattern: `a.Monitored && (a.Files == null || !a.Files.Any() || !a.Files.Any(f => !string.IsNullOrEmpty(f.Path) && System.IO.File.Exists(f.Path)))`
- Apply in 3 locations: LibraryController.GetAllAudiobooks, LibraryController.GetAudiobook, ScanBackgroundService.BroadcastLibraryUpdate
- Prevents false positives where DB records exist but files were deleted

#### Download Client Authentication
- **Transmission**: 409/session-id retry pattern for CSRF protection (PollTransmissionAsync, TransmissionAdapter)
- **qBittorrent**: Cookie-based session authentication
- **SABnzbd/NZBGet**: API key authentication

#### Background Jobs Processing
- `DownloadProcessingBackgroundService`: Implements `ResetStuckJobsAsync()` on startup
- Jobs can get stuck in "Processing" state if service crashes mid-operation
- Reset stuck jobs automatically on startup to prevent queue blockage
- Use 30-second stability window in `DownloadMonitorService` to prevent premature imports

### Critical Frontend Patterns

#### Pinia Store Best Practices
- **downloads.ts**: Filter terminal states ('Moved', 'Completed', 'Failed', 'Cancelled') from activeDownloads
- Use `queueItem.title` for title, NOT `contentPath` (property doesn't exist)
- Always use computed properties for derived state, never mutate store state directly

#### Performance Optimization
- Use `v-memo` directive for large lists (e.g., WantedView audiobook cards)
- Include all reactive dependencies: `v-memo="[audiobook, activeDownloads[audiobook.id]]"`
- Show download indicators with pulse/bounce animations using CSS keyframes

#### Type Safety
- All API responses must have TypeScript types in `types/index.ts`
- Download status: 'Queued' | 'Downloading' | 'Completed' | 'Paused' | 'Failed' | 'Cancelled' | 'Moved'
- Never reference non-existent properties (causes TS2339 errors)

### Configuration Management
- JSON-based configuration stored in `config/` directory
- `ConfigurationService` manages API endpoints, download clients, application settings
- Settings are hot-reloadable and persisted to SQLite database

### Background Processing
- Multiple `IHostedService` implementations for async work
- Queue-based processing for file scanning and metadata extraction
- Automatic retry logic and error handling

### API Integration
- Typed HttpClient services with automatic decompression
- Rate limiting and caching for external API calls
- Playwright integration for JavaScript-rendered pages when needed

### Error Handling and Resilience  
- Global exception handling in API controllers
- Frontend error boundaries and user notifications
- Download client connection testing and fallback mechanisms

### Security
- Cookie-based authentication with CSRF protection
- API key middleware for external integrations
- CORS configuration for development and production

## Development Workflow

### Local Development
1. Use `npm run dev` to start both services simultaneously with colored console output
2. Backend auto-restarts on C# changes (via `dotnet watch`)
3. Frontend hot-reloads on Vue/TS changes (via Vite HMR)
4. Database migrations apply automatically on startup
5. Images cached in `.env/development/config/cache/images/` (gitignored)

### Production Deployment
- Docker images are published to `ghcr.io/listenarrs/listenarr`; `docker.io/therobbiedavis/listenarr` is maintained as a compatibility mirror.
- Self-contained executables for Windows, Linux, macOS
- Single-container deployment includes both API and frontend
- Persistent volume at `/app/config` for database and configuration

### Version Management
- Version controlled in `listenarr.api/Listenarr.Api.csproj`
- CI automatically increments versions:
  - Canary builds (`canary` branch): patch increment (1.2.3 → 1.2.4), tagged `v1.2.4-canary`
  - Beta builds (`beta` branch): minor increment + patch reset (1.2.3 → 1.3.0), tagged `v1.3.0-beta`
  - Release builds (version tag): major increment + reset (1.2.3 → 2.0.0)

## Important Development Notes

### Directory Naming
- Backend directory is **lowercase**: `listenarr.api/` (not `ListenArr.Api/`)
- Frontend directory: `fe/` 
- Docker contexts and paths are case-sensitive

### Port Configuration
- Backend API: `http://localhost:4545` (development)
- Frontend Web: `http://localhost:5173` (development)  
- Production: `http://localhost:4545` (single container serves both) 
- macOS users: Port 5000 conflicts with Airplay, use `--urls` parameter to override

### Database Location
- Development: `.env/development/config/database/listenarr.db`
- Production: `/app/config/database/listenarr.db`
- Automatic migrations on startup

### External Dependencies
- FFprobe binary required for audio metadata extraction (auto-installed by `FfmpegInstallerService`)
- Playwright browsers for web scraping (downloaded on first use)

### CI/CD Integration  
- GitHub Actions for automated builds and deployment
- Multi-platform Docker images (AMD64/ARM64)
- Automated version bumping with branch protection persistence

## Common Troubleshooting Scenarios

### Downloads Not Importing
1. Check logs in `.env/development/config/logs/listenarr-YYYYMMDD.log`
2. Look for authentication errors (401, 409, Unauthorized)
3. Verify DownloadMonitorService is running and detecting candidates
4. Check stability window logs (30-second delay before import)
5. Ensure files exist on disk and are accessible

### Multiple Database Files
- Running from `bin/Debug` creates a second, empty database
- **Always run from repository root** (`npm run dev`)
- Development DB location: `.env/development/config/database/listenarr.db`

### Hot Reload Not Working
- Backend: Stop and restart `dotnet run` if changes aren't reflected
- Frontend: Vite HMR should work automatically; if not, restart `npm run dev`
- Sometimes file locks prevent hot reload; full restart resolves this

### Wanted Status Showing Incorrect Files
- Verify file existence checks are applied in 3 locations (LibraryController x2, ScanBackgroundService)
- Check that files actually exist on disk, not just in database

### Build Errors
- Ensure running .NET 10.0 SDK and Node.js 24.x or later
- Clear node_modules and run `npm install` in `fe/` directory
- Clean solution: `dotnet clean` then `dotnet build`
