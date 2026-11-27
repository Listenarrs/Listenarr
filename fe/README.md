# Listenarr Frontend (fe)

This template should help get you started developing with Vue 3 in Vite.

## Recommended IDE Setup

[VS Code](https://code.visualstudio.com/) + [Vue (Official)](https://marketplace.visualstudio.com/items?itemName=Vue.volar) (and disable Vetur).

## Recommended Browser Setup

- Chromium-based browsers (Chrome, Edge, Brave, etc.):
  - [Vue.js devtools](https://chromewebstore.google.com/detail/vuejs-devtools/nhdogjmejiglipccpnnnanhbledajbpd) 
  - [Turn on Custom Object Formatter in Chrome DevTools](http://bit.ly/object-formatters)
- Firefox:
  - [Vue.js devtools](https://addons.mozilla.org/en-US/firefox/addon/vue-js-devtools/)
  - [Turn on Custom Object Formatter in Firefox DevTools](https://fxdx.dev/firefox-devtools-custom-object-formatters/)

## Type Support for `.vue` Imports in TS

TypeScript cannot handle type information for `.vue` imports by default, so we replace the `tsc` CLI with `vue-tsc` for type checking. In editors, we need [Volar](https://marketplace.visualstudio.com/items?itemName=Vue.volar) to make the TypeScript language service aware of `.vue` types.

## Customize configuration

See [Vite Configuration Reference](https://vite.dev/config/).

## Project Setup

```sh
npm install
```

### Compile and Hot-Reload for Development

```sh
npm run dev
```

### Type-Check, Compile and Minify for Production

```sh
npm run build
```

### Run Unit Tests with [Vitest](https://vitest.dev/)

```sh
npm run test:unit
```

### Run Frontend Integration Tests with [Playwright](https://playwright.dev/)

```sh
npm run test:frontend-integration
```

These browser tests exercise the frontend alone: they stub the HTTP API and run against Vite without starting the C# API.
Their specs live under `playwright/frontend-integration/` and run with the configuration in `playwright.frontend-integration.config.ts`. They start their own dev server on port 5190 (`LISTENARR_FRONTEND_INTEGRATION_WEB_PORT` to change it), so they run beside `npm run dev`; they stop at once if that port is already in use.

Put every API intercept in `playwright/support/api-stubs.ts`, including one-off endpoints, so route matching and response setup are reusable from any spec. Add an exported helper there when a test needs a new endpoint or response shape; specs call the helper and keep test-specific request assertions beside the scenario. For example, `stubSettingsPage(settingsFixture({ outputPath: '/audiobooks' }))` supplies the common page dependencies, while `stubLibrary([libraryAudiobookFixture({ title: 'Dune' })])` stubs the backend's slim library-list response.

The generated types in `playwright/support/generated-api/` come from the backend OpenAPI document and cover the documented bootstrap, settings, library, download, quality-profile, filesystem, API-configuration, and health responses used by the frontend integration helpers. Run `npm run api-types:generate` after changing one of these contracts; CI runs `npm run api-types:check` and `npm run type-check:playwright` to catch contract drift in shared helpers and fixtures. For endpoints without an OpenAPI response schema, keep the intercept helper centralized here and pass scenario-specific reply data to it when needed.

### Backend Integration Tests

The backend integration tests are .NET tests, not Playwright: `tests/Listenarr.BackendIntegrationTests/` starts the whole application in process and drives it only through its HTTP API, with no browser and no Docker. Run them from the repository root with `dotnet test tests/Listenarr.BackendIntegrationTests`; see `tests/README.md`.

### Run E2E Tests

```sh
npm run test:e2e
```

End-to-end browser tests: Chromium drives the frontend, served by the Vite dev server, which forwards every API and SignalR request to the app running in Docker. No Listenarr API response is stubbed. `tests/Listenarr.FakeProviders` stubs only the app's own outbound requests: every `IHttpClientFactory` client goes to one handler that answers the metadata providers (Audible, Audnexus, OpenLibrary) from `tests/Listenarr.FakeProviders/Fixtures/{Provider}/`, one route class per provider in `tests/Listenarr.FakeProviders/Routes/` (naming rules in `Fixtures/README.md`). Every other request (Amazon, images, any provider without a fixture) gets a 404. The container log lists every request that had no fixture. The container also has no DNS, so a request made outside the factory fails too, and the suite runs offline. The runner (`scripts/e2e/run.mjs`) starts the app in Docker with `scripts/lib/listenarr-stack.mjs` and seeds its library from `scripts/e2e/seed-library.json`. It writes the audiobook folders the specs scan from one manifest, `tests/Fixtures/Audiobooks/import-folders.json` (`scripts/e2e/import-folders.mjs`), copying the checked-in, pre-tagged files next to it, and creates the `E2E Import` (default), `E2E Second`, `E2E Empty` and `E2E Library` root folders; the specs read the same manifest (`playwright/e2e/support/import-folders.ts`). Every test restores the seeded database and files first.

The specs live under `playwright/e2e/` and run with `playwright.e2e.config.ts`, one at a time. The runner builds the images once, generates the audiobook folders once, then starts one stack per Playwright shard and runs the shards in parallel (3 by default; `--stacks=N` or `LISTENARR_E2E_STACKS=N` changes it). Shards are split by test, not by spec file (`fullyParallel`), so they hold about the same number of tests. Each shard has its own container, ports, traces and Vite cache; the images are shared. On a clean checkout the images are tagged with the commit and reused by the next run on it. The runner never removes those commit-tagged images: on a machine that keeps its Docker cache, remove the old ones with `docker image rm`, for example `docker image ls listenarr-e2e-app` and `listenarr-e2e-host`. With `--coverage` (`npm run test:e2e -- --coverage`) the run collects coverage on both sides, reported but not gated:

- Frontend: V8 coverage from Chromium, mapped to the sources, one report per shard in `test-results/e2e-coverage/frontend/<n>/` (`index.html`, `lcov.info`).
- Backend: each host runs under `dotnet-coverage`. The merged Cobertura report goes to `test-results/e2e-coverage-backend/cobertura.xml`, with a summary of the library import code in `summary.md`.

`node ../scripts/e2e/run.mjs -- <playwright arguments>` runs a subset, such as a spec, `--grep` or `--repeat-each`, on one stack, since a filter can leave a shard without tests (`--stacks=N` still shards it, except with `--coverage`, where an empty shard fails its report). `--coverage` collects coverage (off by default). `--serve` starts a single stack (no sharding) and prints the variables to run Playwright against it repeatedly.

### Test Coverage

Coverage is off by default, locally and in CI. To get it in CI, run the `Run Tests` workflow manually with the `coverage` input (`gh workflow run run-tests.yml --ref <branch> -f coverage=true`): every test job then collects coverage and uploads it as a `coverage-<job>` artifact, and the last job, `coverage`, merges them (`scripts/test-coverage/aggregate.mjs`) into a job summary, per side of the app, with each job's share of the coverable lines, the lines no other job covers and the total, plus a `coverage-report` artifact (HTML report, merged lcov). It is reported, not gated. Locally:

- Unit tests: `npm run test:coverage` writes `coverage/unit/` (`lcov.info`, HTML).
- Frontend integration tests: `LISTENARR_COVERAGE=1 npm run test:frontend-integration` writes `test-results/frontend-integration-coverage/`.
- E2E tests: `npm run test:e2e -- --coverage` (see above).
- Backend tests: `dotnet test <project> --settings tests/coverage.runsettings --results-directory <dir>` from the repository root.

To merge them, put each job's reports in its own folder under one directory and run `dotnet tool restore && node scripts/test-coverage/aggregate.mjs <directory> <output> --html` from the repository root.

### Lint with [ESLint](https://eslint.org/)

```sh
npm run lint
```
