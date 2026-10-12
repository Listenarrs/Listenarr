# Listenarr — Copilot Instructions

This is a C# / .NET 10 (ASP.NET Core) backend with a Vue 3 + TypeScript frontend, structured as
a 4-layer Clean Architecture solution. Full guidance lives outside `.github/` — this file just
points you there so it isn't duplicated (and doesn't drift) in two places.

**Start here:** [`../AGENTS.md`](../AGENTS.md) — project overview, layering rules, and a table of
which deeper doc to read based on what you're touching.

**Deeper references, as needed:**
- [`../BACKEND_ARCHITECTURE.md`](../BACKEND_ARCHITECTURE.md) — backend layer boundaries, vertical
  feature structure, persistence/worker contracts.
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — dev setup, branching, PR process, code review bar.
- [`../SECURITY_RULES.md`](../SECURITY_RULES.md) — secure coding guidance (OWASP/CWE).
- [`../LINTING.md`](../LINTING.md) — formatting, linting, hooks.
- [`../tests/README.md`](../tests/README.md) — backend test conventions.

**Quick start:**
```bash
npm run dev   # starts API (:4545) and frontend (:5173) from repo root
```
Run from the repository root, not `listenarr.api/bin/...` — running from a build output
directory creates a second, empty database.
