# Listenarr — Agent Instructions

Listenarr is a self-hosted audiobook collection manager: a C# / .NET 10 (ASP.NET Core) backend
with a Vue 3 + TypeScript frontend (`fe/`), built as a 4-layer Clean Architecture solution.

## Layering (read this before touching backend code)

```
listenarr.domain          -> no dependencies
listenarr.application      -> depends on domain only (use-cases, contracts/ports)
listenarr.infrastructure   -> depends on application + domain (EF Core, filesystem, HTTP, adapters)
listenarr.api               -> composition root (DI wiring, controllers, middleware)
```

Each layer is organized first by feature (`Audiobooks`, `Downloads`, `Search`, `Metadata`, …) and
then by technical role. Keep infrastructure-shaped dependencies (EF Core, SQLite, HTML/image/tag
parsing libraries, ASP.NET Core hosting types, SignalR) out of `listenarr.application`; define an
application-owned port there and implement the adapter in infrastructure or API. See
`BACKEND_ARCHITECTURE.md` for the full contract.

## Read further, based on what you're touching

| Working on… | Also read |
|---|---|
| Development setup, branching, migrations, or preparing/reviewing a PR | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| Persistence, DI/composition, EF Core, background workers, move/scan/download recovery, filesystem behavior | [`BACKEND_ARCHITECTURE.md`](BACKEND_ARCHITECTURE.md) |
| Frontend components, stores, API types, SignalR behavior, or frontend tests | [`fe/AGENTS.md`](fe/AGENTS.md) |
| Authentication, user-supplied paths, external input, serialization, secrets, crypto | [`SECURITY_RULES.md`](SECURITY_RULES.md) |
| Formatting, linting, pre-commit/pre-push hooks | [`LINTING.md`](LINTING.md) |
| Backend test conventions | [`tests/README.md`](tests/README.md) |
| Docker, deployment, CI workflows, releases, or versioning | [`README.md`](README.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| Discord bot integration or tooling | [`tools/discord-bot/README.md`](tools/discord-bot/README.md) |
| Reporting or handling an actual vulnerability | [`SECURITY.md`](SECURITY.md) |
| Branding/logo assets | [`.github/BRANDING.md`](.github/BRANDING.md) |
| File headers/licensing | [`.github/COPYRIGHT_HEADER.md`](.github/COPYRIGHT_HEADER.md) |

Don't read everything unconditionally — `BACKEND_ARCHITECTURE.md` in particular is a large,
detailed contract that only pays off when you're actually working in the area it covers.

## Quick reference

- Dev stack: `npm run dev` from repo root (API on :4545, frontend on :5173).
- Backend tests: `dotnet test`. Frontend tests: `cd fe && npm run test:unit`.
- Avoid slopsquatting: don't reference or import a package without confirming it exists; call out
  low-reputation or uncommon dependencies you introduce.
