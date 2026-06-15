# Frontend Test Layout

Vitest specs live in `test/` folders next to the code they exercise:

- Components: `src/components/<area>/test/*.spec.ts`
- Views: `src/views/<area>/test/*.spec.ts`
- Stores, services, composables, and utilities: `src/<area>/test/*.spec.ts`

Use `*.spec.ts` for frontend tests; Vitest only discovers that extension under
`src/**/test/`. Specs run in jsdom by default. Use `*.node.spec.ts` only for
tests that intentionally run without browser globals. Test infrastructure in
`src/test` is opt-in only: factories, explicit mocks, local stubs, and mount
helpers. Shared specs under `src/test` are limited to app-shell, framework, and
smoke coverage.

Rules:

- Keep Vitest setup files small and side-effect focused.
- The only global app-service mock is `@/services/signalr`, because the real
  singleton auto-connects on import and leaks WebSocket/timer work into
  unrelated tests.
- Do not add global browser/API monkeypatches.
- Put API, toast, storage, and component stubs directly in the spec that needs
  them, or import explicit helpers from `src/test`.
- Use `src/test/mocks/signalr` when a spec needs to inspect or emit SignalR
  callbacks from the global mock.
- Keep test data in factories when the same shape appears in multiple specs.
- Keep auth boundaries explicit: auth store specs cover state, API calls, and
  browser auth markers without a real router; router, login view, and app-shell
  specs own redirect behavior.
- Run `npm run type-check:test` before changing shared helpers or fixture
  factories.
- Run `npm run verify` before submitting frontend test infrastructure changes.
