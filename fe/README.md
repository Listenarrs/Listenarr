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

### Run API-stub Integration Tests with [Cypress](https://www.cypress.io/)

```sh
npm run test:integration:cypress
```

These browser tests stub the HTTP API, so they are classified as integration tests rather than full-stack E2E tests. They run against Vite without starting the C# API.
Their specs live under `cypress/integration/` and use integration-only support/configuration.

Put every API intercept in `cypress/support/api-stubs.ts`, including one-off endpoints, so route matching and response setup are reusable from any spec. Add an exported helper there when a test needs a new endpoint or response shape; specs call the helper and keep test-specific request assertions beside the scenario. For example, `stubSettingsPage(settingsFixture({ outputPath: '/audiobooks' }))` supplies the common page dependencies, while `stubLibrary([libraryAudiobookFixture({ title: 'Dune' })])` stubs the backend's slim library-list response.

The generated types in `cypress/support/generated-api/` come from the backend OpenAPI document and cover the documented bootstrap, settings, library, download, quality-profile, filesystem, API-configuration, and health responses used by Cypress. Run `npm run api-types:generate` after changing one of these contracts; CI runs `npm run api-types:check` and `npm run type-check:cypress` to catch contract drift in shared helpers and fixtures. For endpoints without an OpenAPI response schema, keep the intercept helper centralized here and pass scenario-specific reply data to it when needed.

### Run Full-stack E2E Tests

```sh
npm run test:e2e
```

This builds and starts the production Docker image, which serves both the real API and frontend. Cypress runs against that container; it does not start a host API or Vite server. The runner mounts unique temporary config and filesystem directories, seeds the database from `scripts/e2e/seed-library.json`, and restores both snapshots before every write test. It generates a tagged M4B import fixture with the pinned `m4bard/listenarr-testdata` generator from `scripts/e2e/generate-audio-fixtures.sh`; the write suite scans, imports, and moves that file, then verifies its persisted record and filesystem location. The container and temporary data are removed on exit, so the runner does not use a developer database or instance. Docker Compose, Git, Python 3 with `venv`, and FFmpeg are required.
Read-only specs live under `cypress/e2e/read-only/`; database-mutating specs live under `cypress/e2e/write/` and use a separate Cypress config and reset hook.

The ordered full suite is the CI command. For local debugging, `npm run test:e2e:read-only` runs only the read-only suite and `npm run test:e2e:write` runs only the reset-before-each-test write suite.

### Lint with [ESLint](https://eslint.org/)

```sh
npm run lint
```
