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

End-to-end browser tests: Chromium drives the frontend, served by the Vite dev server, which forwards every API and SignalR request to the app running in Docker. No Listenarr API response is stubbed. `tests/Listenarr.FakeProviders` stubs only the app's own outbound requests: every `IHttpClientFactory` client goes to one handler that answers the metadata providers (Audible, Audnexus, OpenLibrary) from `tests/Listenarr.FakeProviders/Fixtures/{Provider}/`, one route class per provider in `tests/Listenarr.FakeProviders/Routes/` (naming rules in `Fixtures/README.md`). Every other request (Amazon, images, any provider without a fixture) gets a 404. The container log lists every request that had no fixture. The container also has no DNS, so a request made outside the factory fails too, and the suite runs offline. The runner (`scripts/e2e/run.mjs`) starts the app in Docker with `scripts/lib/listenarr-stack.mjs` and seeds its library from `scripts/e2e/seed-library.json`. It generates the audiobook folders with `scripts/e2e/generate-library-fixtures.sh`, using the same pinned generator, and creates the `E2E Import` (default), `E2E Second`, `E2E Empty` and `E2E Library` root folders. Every test restores the seeded database and files first.

The specs live under `playwright/e2e/` and run with `playwright.e2e.config.ts`, one at a time. With `--coverage` (`npm run test:e2e -- --coverage`) the run collects coverage on both sides, reported but not gated:

- Frontend: V8 coverage from Chromium, mapped to the sources, in `test-results/e2e-coverage/` (`index.html`, `lcov.info`).
- Backend: the host runs under `dotnet-coverage`. The merged Cobertura report goes to `test-results/e2e-coverage-backend/cobertura.xml`, with a summary of the library import code in `summary.md`.

`node ../scripts/e2e/run.mjs -- <playwright arguments>` runs a subset, such as a spec or `--repeat-each`. `--coverage` collects coverage (off by default). `--serve` starts the stack and prints the variables to run Playwright against it repeatedly.

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
