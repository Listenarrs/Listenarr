# OpenAI Instructions for Listenarr

## Required Repository Context

Before making code, dependency, workflow, or documentation changes, review and follow:

- `CONTRIBUTING.md`
- `BACKEND_ARCHITECTURE.md`
- `.github/RULES.md`
- `.github/copilot-instructions.md`
- `.github/AGENTS.md`
- `.github/.cursorrules`

Repository-specific guidance takes precedence over general examples in this file. Keep infrastructure-shaped dependencies out of `listenarr.application`; define application-owned ports there and implement adapters in infrastructure/API.

## Overview
Listenarr is a C# .NET 10.0 audiobook management system with Vue.js 3 frontend. See [copilot-instructions.md](copilot-instructions.md) for complete details.

## Quick Start
- **Run**: `npm run dev` from repository root
- **Tech Stack**: .NET 10.0, Vue 3, TypeScript, Pinia, EF Core, SQLite, SignalR
- **Development database**: `.env/development/config/database/listenarr.db`
- **Development logs**: `.env/development/config/logs/listenarr-YYYYMMDD.log`

## Critical Backend Patterns (.NET 10.0)
1. **Download Status Lifecycle**: Always set `Status = DownloadStatus.Moved` after import (8 locations in CompletedDownloadProcessor.cs)
2. **File Existence Validation**: Check `File.Exists(f.Path)` in wanted queries (3 locations)
3. **Download Client Auth**: Transmission uses 409/session-id retry; qBittorrent uses cookies
4. **Background Jobs**: Reset stuck jobs on startup in `DownloadProcessingBackgroundService`
5. **Async/Await**: All I/O operations must be async
6. **Dependency Injection**: Constructor injection for all services

## Critical Frontend Patterns (Vue 3 + TypeScript)
1. **Composition API**: Use `<script setup>` with TypeScript
2. **State Management**: Pinia stores with computed properties (never mutate state)
3. **Performance**: Use `v-memo` for large lists with reactive dependencies
4. **Type Safety**: All API responses need TypeScript types in `types/index.ts`
5. **Download Filtering**: Exclude terminal states ('Moved', 'Completed', 'Failed', 'Cancelled') from activeDownloads
6. **SignalR**: Handle connection drops with reconnection logic

## Security (OWASP)
- No hardcoded secrets (use `IConfiguration` with Azure Key Vault / env vars)
- Parameterized queries only (EF Core handles automatically)
- Validate file paths (prevent path traversal with `Path.GetFileName()`)
- See [AGENTS.md](AGENTS.md) for comprehensive OWASP/CWE guidance

## Common Issues
- **Downloads not importing**: Check logs for auth errors (401, 409), verify 30s stability window
- **Multiple databases**: Always run from repo root, not `bin/Debug`
- **Wanted status wrong**: Verify file existence checks in LibraryController (2x) and ScanBackgroundService

## Development Workflow
1. Run `npm run dev` from repository root
2. Backend auto-restarts with `dotnet watch`
3. Frontend hot-reloads with Vite HMR
4. Check logs when debugging import/download issues

For detailed architecture and API endpoints, see [copilot-instructions.md](copilot-instructions.md).
