# Linting Standards

Listenarr uses a small set of shared checks so local hooks and CI describe the same expectations.

## Formatting

- Root whitespace and line endings are defined in `.editorconfig`.
- Git normalizes text files to LF through `.gitattributes`, with Windows script files kept as CRLF.
- C# formatting is checked with `dotnet format listenarr.slnx --no-restore --verify-no-changes`.
- Frontend formatting is defined by `fe/.editorconfig` and `fe/.prettierrc.json`.

## Frontend linting

- ESLint is configured in `fe/eslint.config.ts`.
- Vue uses the essential Vue rules.
- TypeScript uses the Vue TypeScript recommended rules.
- Vitest files use their matching recommended plugin rules.
- Prettier owns formatting; ESLint formatting rules stay disabled through the Prettier skip config.
- Multi-line Vue event handlers must be a single expression, an inline function, or a named
  handler. If a handler needs multiple statements, prefer a named function; otherwise wrap the
  statements in an arrow-function block so Prettier cannot split them into invalid template syntax.

## Commands

- `npm run lint` runs the project lint checks that are safe to run across the current tree.
- `npm run lint:staged` runs the pre-commit staged-file checks.
- `npm --prefix fe run lint:vue-handlers` checks for fragile multi-line Vue event handlers.
- `npm run format:check` runs the strict full formatting check.
- `npm run format` applies backend and frontend formatting.
- `npm test` runs backend and frontend tests.

## Hooks

- `pre-commit` runs staged lint and format checks so changed files follow the standard.
- `pre-push` runs the full test suite.

The full formatting check is intentionally separate because the current tree still needs a dedicated normalization pass before it can be used as a required CI gate.
