# Anthropic/Claude Instructions for Listenarr

## Required Repository Context

Before making code, dependency, workflow, or documentation changes, review and follow `CONTRIBUTING.md`, `BACKEND_ARCHITECTURE.md`, `.github/RULES.md`, and the primary AI guidance files. Repository-specific guidance takes precedence over generic provider guidance.

## Overview
Listenarr is a C# .NET 10.0 audiobook management system with Vue.js 3 frontend. See [copilot-instructions.md](copilot-instructions.md) for complete details.

## Quick Start
- **Run**: `npm run dev` from repository root
- **Development database**: `.env/development/config/database/listenarr.db`
- **Development logs**: `.env/development/config/logs/listenarr-YYYYMMDD.log`
- **Backend**: http://localhost:4545
- **Frontend**: http://localhost:5173

## Critical Patterns

### Backend (.NET 10.0)
1. **Download Status**: Always set `Status = DownloadStatus.Moved` after successful import
2. **File Checks**: Verify `File.Exists(f.Path)` in wanted/library queries (3 locations)
3. **Auth**: Transmission uses 409/session-id retry; qBittorrent uses cookies
4. **Jobs**: Reset stuck jobs on startup (`ResetStuckJobsAsync`)
5. **Async**: All I/O operations must be async
6. **DI**: Constructor injection for all services

### Frontend (Vue 3 + TypeScript)
1. **Components**: Use `<script setup>` with TypeScript
2. **State**: Pinia stores with computed properties (never mutate directly)
3. **Performance**: Use `v-memo` for large lists
4. **Types**: All API responses need TypeScript types in `types/index.ts`
5. **Downloads**: Filter terminal states ('Moved', 'Completed', 'Failed', 'Cancelled')
6. **SignalR**: Handle reconnection gracefully

## Security
- No hardcoded secrets (use `IConfiguration`)
- Parameterized queries only (EF Core auto-handles)
- Validate file paths (prevent traversal)
- See [AGENTS.md](AGENTS.md) for OWASP/CWE guidance

## Common Issues
- **Downloads not importing**: Check logs for auth errors, verify 30s stability window
- **Multiple databases**: Run from repo root, not `bin/Debug`
- **Hot reload fails**: Restart services with `npm run dev`

For comprehensive guidance, see [copilot-instructions.md](copilot-instructions.md) and [AGENTS.md](AGENTS.md).
