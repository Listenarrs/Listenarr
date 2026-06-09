# Google Bard/Gemini Instructions for Listenarr

## Overview
Listenarr is a C# .NET 10.0 audiobook management system with Vue.js 3 frontend. See [copilot-instructions.md](copilot-instructions.md) for complete details.

## Quick Start
- **Run**: `npm run dev` from repository root
- **Tech**: .NET 10.0, Vue 3, TypeScript, Pinia, EF Core, SQLite, SignalR
- **Development database**: `.env/development/config/database/listenarr.db`
- **Development logs**: `.env/development/config/logs/listenarr-YYYYMMDD.log`

## Critical Patterns

### Backend (.NET 10.0)
1. **Download Status**: Always set `Status = DownloadStatus.Moved` after import
2. **File Checks**: Verify `File.Exists(f.Path)` for wanted status (3 locations)
3. **Authentication**: Transmission (409/session-id retry), qBittorrent (cookies)
4. **Jobs**: Reset stuck jobs on startup (`ResetStuckJobsAsync`)
5. **Async**: All I/O operations must be async
6. **DI**: Constructor injection for all services

### Frontend (Vue 3 + TypeScript)
1. **Components**: Use `<script setup>` with TypeScript
2. **State**: Pinia stores, never mutate directly
3. **Performance**: Use `v-memo` for large lists
4. **Types**: TypeScript types for all API responses
5. **Downloads**: Filter terminal states from active downloads
6. **SignalR**: Handle reconnection gracefully

## Security
- No hardcoded secrets (use `IConfiguration`)
- Parameterized queries only (EF Core)
- Validate file paths (prevent traversal)
- See [AGENTS.md](AGENTS.md) for OWASP/CWE guidance

## Common Issues
- **Downloads not importing**: Check logs for auth errors, 30s stability window
- **Multiple databases**: Run from repo root, not `bin/Debug`
- **Hot reload fails**: Restart `npm run dev`

For detailed architecture, see [copilot-instructions.md](copilot-instructions.md).
