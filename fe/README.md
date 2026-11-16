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
Their specs live under `playwright/frontend-integration/` and run with the configuration in `playwright.frontend-integration.config.ts`.

Put every API intercept in `playwright/support/api-stubs.ts`, including one-off endpoints, so route matching and response setup are reusable from any spec. Add an exported helper there when a test needs a new endpoint or response shape; specs call the helper and keep test-specific request assertions beside the scenario. For example, `stubSettingsPage(settingsFixture({ outputPath: '/audiobooks' }))` supplies the common page dependencies, while `stubLibrary([libraryAudiobookFixture({ title: 'Dune' })])` stubs the backend's slim library-list response.

The generated types in `playwright/support/generated-api/` come from the backend OpenAPI document and cover the documented bootstrap, settings, library, download, quality-profile, filesystem, API-configuration, and health responses used by the frontend integration helpers. Run `npm run api-types:generate` after changing one of these contracts; CI runs `npm run api-types:check` and `npm run type-check:playwright` to catch contract drift in shared helpers and fixtures. For endpoints without an OpenAPI response schema, keep the intercept helper centralized here and pass scenario-specific reply data to it when needed.

### Run Backend Integration Tests

```sh
npm run test:backend-integration
```

This builds the production Docker image and a separate backend integration host image, then runs Playwright API tests against the app in Docker without launching a browser. The test-only host registers a fixture-backed Audible client so test requests never reach Audible; production registration and its image contain no test stub. The runner (`scripts/backend-integration/run.mjs`) mounts unique temporary config and filesystem directories, seeds the database from `scripts/backend-integration/seed-library.json`, and restores both snapshots before every write test. It generates a tagged M4B import fixture with the generator created by [m4bard](https://github.com/m4bard/listenarr-testdata), pinned and invoked by `scripts/backend-integration/generate-audio-fixtures.sh`; the write suite scans, imports, and moves that file, then verifies its persisted record and filesystem location. The container and temporary data are removed on exit, so the runner does not use a developer database or instance. The dedicated host is built from `tests/Listenarr.TestHost/Listenarr.TestHost.csproj` by `.devcontainer/backend-integration.Dockerfile`. Docker, Git, Python 3 with `venv`, and FFmpeg are required; the dev container (`.devcontainer/`) provides all of them.
Their specs run with the configuration in `playwright.backend-integration.config.ts`. Read-only specs live under `playwright/backend-integration/read-only/`; database-mutating specs live under `playwright/backend-integration/write/` and restore the seeded database and filesystem before each test.

The ordered full suite is the CI command. For local debugging, `npm run test:backend-integration:read-only` runs only the read-only suite and `npm run test:backend-integration:write` runs only the reset-before-each-test write suite.

### Run E2E Tests

```sh
npm run test:e2e
```

End-to-end browser tests: Chromium drives the frontend, served by the Vite dev server, which forwards every API and SignalR request to the app running in Docker. No Listenarr API response is stubbed. The test host (`tests/Listenarr.TestHost`) stubs only the app's own outbound requests: every `IHttpClientFactory` client goes to one handler that answers Audible from the fixtures in `tests/Listenarr.TestHost/Metadata/Fixtures/`: product records are `{ASIN}.json`, or `{ASIN}.{api host}.json` for one region, title searches are `search-{title}[--{author}].json`, and an author's catalogue (what a title and author search filters) is `search-author-{author}.json`, each part slugged. Every other provider (Audnexus, OpenLibrary, Amazon, images) gets a 404. The container log lists every request that had no fixture. The container also has no DNS, so a request made outside the factory fails too, and the suite runs offline. The runner (`scripts/e2e/run.mjs`) shares its Docker stack with the backend integration suite (`scripts/lib/listenarr-stack.mjs`). It generates the audiobook folders with `scripts/e2e/generate-library-fixtures.sh`, using the same pinned generator, and creates the `E2E Import` (default), `E2E Second`, `E2E Empty` and `E2E Library` root folders. Every test restores the seeded database and files first.

The specs live under `playwright/e2e/` and run with `playwright.e2e.config.ts`, one at a time. The run collects coverage on both sides:

- Frontend: V8 coverage from Chromium, mapped to the sources, in `test-results/e2e-coverage/index.html`. The whole suite fails when the Library Import code (`LIBRARY_IMPORT_SOURCES` in `playwright/e2e/support/coverage.ts`) falls below `THRESHOLDS`.
- Backend: the host runs under `dotnet-coverage`. The merged Cobertura report goes to `test-results/e2e-coverage-backend/cobertura.xml`, with a summary of the library import code in `summary.md`. This side is reported but not gated.

`node ../scripts/e2e/run.mjs -- <playwright arguments>` runs a subset, such as a spec or `--repeat-each`, without the coverage gate. `--no-coverage` skips collection. `--serve` starts the stack and prints the variables to run Playwright against it repeatedly.

### Lint with [ESLint](https://eslint.org/)

```sh
npm run lint
```
