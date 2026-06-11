# Claude AI Instructions for Listenarr

## Required Repository Context

Before making code, dependency, workflow, or documentation changes, review and follow `CONTRIBUTING.md`, `BACKEND_ARCHITECTURE.md`, `.github/RULES.md`, and the primary AI guidance files. Repository-specific guidance takes precedence over generic provider guidance.

## Quick Reference
This file contains Claude-specific guidance for the Listenarr audiobook management system. For comprehensive secure coding practices, see [AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md). For complete project details, see [copilot-instructions.md](copilot-instructions.md).

## Project Summary
**Listenarr** is a C# .NET 10.0 Web API backend with Vue.js 3 frontend for automated audiobook downloading and processing via torrent/NZB clients (qBittorrent, Transmission, SABnzbd, NZBGet).

## Key Technologies
- **Backend**: ASP.NET Core (.NET 10), Entity Framework Core, SQLite
- **Frontend**: Vue 3, TypeScript, Pinia, Vite, SignalR
- **Architecture**: Clean architecture (Domain, Application, Infrastructure layers)

## Critical Patterns to Follow

### Backend (.NET)
1. **Download Status Lifecycle**: Always set `Status = DownloadStatus.Moved` after successful import (8 locations in CompletedDownloadProcessor.cs)
2. **File Existence Checks**: Verify files exist on disk, not just in DB (pattern: `File.Exists(f.Path)`)
3. **Authentication**: Transmission uses 409/session-id retry pattern; qBittorrent uses cookie sessions
4. **Job Processing**: Reset stuck jobs on startup in `DownloadProcessingBackgroundService`
5. **Async/Await**: All I/O operations must be async
6. **Dependency Injection**: Constructor injection for all services
7. **Logging**: Use structured logging with placeholders: `_logger.LogInformation("Processing {Id}", id)`

### Frontend (Vue)
1. **Composition API**: Use `<script setup>` with TypeScript
2. **State Management**: Use Pinia stores, never mutate state directly
3. **Performance**: Use `v-memo` for large lists with all reactive dependencies
4. **Type Safety**: All API responses need TypeScript types in `types/index.ts`
5. **Download Status**: Filter terminal states ('Moved', 'Completed', 'Failed', 'Cancelled') from active downloads
6. **SignalR**: Handle connection drops with reconnection logic

## Development Workflow
1. **Run from repository root**: `npm run dev` (starts both API and frontend)
2. **Development database**: SQLite at `.env/development/config/database/listenarr.db`
3. **Development logs**: Check `.env/development/config/logs/listenarr-YYYYMMDD.log` for diagnostics
4. **Hot Reload**: Backend uses `dotnet watch`, frontend uses Vite HMR

## Common Issues & Solutions
- **Downloads not importing**: Check logs for auth errors (401, 409), verify 30-second stability window
- **Multiple databases**: Always run from repo root, not `bin/Debug`
- **Wanted status incorrect**: Verify file existence checks in 3 locations (LibraryController x2, ScanBackgroundService)

## Security (CRITICAL)
- **No hardcoded secrets**: Use `IConfiguration` with secure providers (Azure Key Vault, environment variables)
- **Parameterized queries**: EF Core handles this automatically; never concat SQL
- **Input validation**: Especially file paths (prevent path traversal with `Path.GetFileName()`)
- **Output encoding**: Razor automatically encodes; use `HtmlEncoder` for custom scenarios
- See [AGENTS.md](AGENTS.md) for comprehensive OWASP/CWE guidance

## File Locations
- **Backend**: `listenarr.api/`, `listenarr.application/`, `listenarr.domain/`, `listenarr.infrastructure/`
- **Frontend**: `fe/src/`
- **Tests**: `tests/`
- **Development config**: `.env/development/config/` (database, logs, cache)

## When Making Changes
1. Update tests when changing public APIs or DI constructors
2. Add INFO-level logs for flow transitions, DEBUG for verbose data
3. Verify TypeScript types match backend models
4. Check SignalR events are handled in frontend stores
5. Run `npm run dev` to test both services together

For detailed architecture, API endpoints, and comprehensive patterns, see [copilot-instructions.md](copilot-instructions.md).
