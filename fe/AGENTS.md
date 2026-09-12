# Frontend Agent Instructions

These instructions apply to work under `fe/`. Also follow the repository entrypoint in
[`../AGENTS.md`](../AGENTS.md), and read [`../SECURITY_RULES.md`](../SECURITY_RULES.md) when
handling authentication, external input, URLs, or other trust boundaries.

## Durable frontend rules

- Use Vue 3's Composition API with `<script setup lang="ts">` for new components. Keep derived
  values in `computed` state and side effects in explicit lifecycle hooks or watchers.
- Keep shared server state and cross-view behavior in the owning Pinia store. Components should
  call store actions rather than directly mutating store state or duplicating request logic.
- Keep API response and SignalR payload types explicit and synchronized with backend contracts.
  Do not bypass a contract mismatch with `any`, unchecked casts, or invented properties.
- Treat initial HTTP hydration and later SignalR messages as one state flow. Register realtime
  subscriptions once, clean them up when ownership ends, handle reconnects, and make repeated or
  out-of-order messages safe where the backend contract permits them.
- Preserve accessibility when changing UI behavior: use semantic controls, associated labels,
  keyboard-operable interactions, visible focus, and appropriate status announcements.
- Add or update focused Vitest tests for changed behavior. Add Cypress coverage when the behavior
  depends on full browser navigation or a multi-step user flow.

## Commands

Run from `fe/` unless noted otherwise:

- `npm run type-check` — TypeScript and Vue type checking.
- `npm run test:unit` — frontend unit tests.
- `npm run lint:check` — ESLint checks.
- `npm run format:check` — Prettier verification.

Formatting and Vue syntax conventions live in [`../LINTING.md`](../LINTING.md).
