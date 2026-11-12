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

### Run Full-stack E2E Tests

```sh
npm run test:e2e
```

This starts the real API and Vite frontend, runs the read-only library check first, then runs write tests. The write suite stops the API and restores its generated SQLite/config seed before every test. All data, including the app's content root, lives under a temporary directory that is removed on exit; the runner does not use a developer database or instance.
Read-only specs live under `cypress/e2e/read-only/`; database-mutating specs live under `cypress/e2e/write/` and use a separate Cypress config and reset hook.

The ordered full suite is the CI command. For local debugging, `npm run test:e2e:read-only` runs only the read-only suite and `npm run test:e2e:write` runs only the reset-before-each-test write suite.

### Lint with [ESLint](https://eslint.org/)

```sh
npm run lint
```
