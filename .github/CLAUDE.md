````markdown
# Secure Code Generation Rules for .NET/ASP.NET Core

## Required Repository Context

Before making code, dependency, workflow, or documentation changes, review and follow:

- `CONTRIBUTING.md`
- `BACKEND_ARCHITECTURE.md`
- `.github/RULES.md`
- `.github/copilot-instructions.md`
- `.github/.cursorrules`

Repository-specific guidance takes precedence over general examples in this file. Keep infrastructure-shaped dependencies out of `listenarr.application`; define application-owned ports there and implement adapters in infrastructure/API.

## Mandatory Independent and Adversarial Code Review

Every code review must be a fresh, independent, adversarial review of the authoritative complete diff. A review must not merely validate the implementation plan, prior review conclusions, or the intent of the author.

Required review behavior:

- Start from the complete branch, commit, staged, or working-tree diff against its authoritative base and review the changed behavior from first principles.
- Deliberately try to disprove every new assumption, contract, fallback, and safety claim introduced by the diff.
- Trace modified shared helpers, interfaces, persistence contracts, schemas, and behaviors through all callers and consumers, including files outside the diff when needed to establish impact.
- Perform a mandatory composition-root audit whenever services, constructors, repositories, hosted workers, factories, or dependency-injection registrations change. Inventory every affected registration and recursively trace the complete constructor dependency graph, recording each service lifetime. Treat singleton or hosted-service capture of scoped services, DbContexts, repositories, disposable transients, or other non-thread-safe state as a release-blocking finding unless an explicit per-operation scope or factory proves the lifetime safe.
- Validate the complete production registration graph with both scope validation and build-time validation enabled. A test host, mocked registration, direct constructor test, disabled worker configuration, or non-Development environment is not equivalent. Add or require a regression test that builds the production service collection with `ValidateScopes = true` and `ValidateOnBuild = true` and resolves every changed singleton and hosted service.
- Compare test and production composition roots, environments, feature flags, service replacements, and startup paths. Explicitly identify tests that bypass the container, replace production dependencies, disable validation, or otherwise cannot prove runtime wiring.
- Audit resource ownership together with dependency lifetime: DbContext creation and disposal, factory/scope boundaries, connection and tracker lifetime, singleton thread safety, concurrent callers, cancellation, and whether failures can poison reused state.
- Use a review coverage matrix for every complete pass. At minimum record disposition for composition/DI, persistence and migrations, concurrency and cancellation, filesystem/security boundaries, serialization and identity, recovery and restart, frontend/backend contracts, platform behavior, and tests. Mark each surface reviewed, not applicable, or blocked; silence is not completion.
- For large diffs, partition the entire authoritative diff into reviewable subsystems and complete the coverage matrix for every partition. Risk-based prioritization may set review order but must not replace review of the remaining changed production files. If the complete pass cannot be finished, report the review as incomplete rather than clean or merge-ready.
- Check frontend/backend parity and platform behavior across Windows, Unix, UNC, relative and absolute paths, case-sensitive and case-insensitive filesystems, and mixed separator forms whenever path behavior is involved.
- Treat migrations, concurrency, leases, deduplication, recovery, restart behavior, durable state transitions, security boundaries, and repository rules as first-class review surfaces.
- Treat passing tests as supporting evidence, not proof of correctness. Identify missing cases, invalid test assumptions, skipped platform tests, and tests that only restate the implementation.
- Require native validation for platform-specific claims. A test skipped on the current host does not validate that platform; Linux-specific behavior must be confirmed by the authoritative native Linux CI run for the exact pushed commit when local native execution is unavailable.
- Keep implementation and review passes separate. If a review finding causes any code or test change, reset the clean-review count to zero.
- Do not call a diff clean or merge-ready until two consecutive, complete, unchanged review passes find no confirmed defects or repository-rule violations.
- Clearly distinguish confirmed findings, unverified risks, missing platform validation, process blockers, and non-blocking suggestions.

## Compatibility Boundary for Development Branches

- The authoritative compatibility boundary is the target branch or released version that a change will merge into. Persisted states produced only by an unmerged feature branch, pull-request build, or intermediate development image are not supported upgrade inputs.
- Do not add production compatibility code, recovery states, filesystem marker readers, API endpoints, schema versions, or tests solely to preserve intermediate iterations of an unmerged change. Remove or regenerate those development artifacts before merge.
- EF migrations are immutable historical artifacts only after they reach the target branch. While a feature branch is unmerged, delete superseded branch-only migrations, restore the target branch model snapshot, and regenerate the minimum final EF migration set from the cleaned final model. Never hand-shape generated migrations to preserve intermediate branch states.
- Compatibility with the actual target-branch schema and persisted data remains mandatory. Before deleting legacy-looking behavior, prove whether the state can exist on the target branch; released or merged states must continue to fail safely or upgrade deterministically.
- Reviews must distinguish current crash-safety evidence from development-history compatibility. A filesystem marker or recovery protocol that is still part of the final safety contract is not removable merely because earlier versions of that protocol existed during development.

## Cross-shell null redirection

- Never redirect output to `NUL` from Git Bash, MSYS, WSL, or another POSIX shell; those environments can create a real Windows-reserved file named `NUL` in the checkout.
- Use `/dev/null` only in POSIX shells, `$null` only in PowerShell, and process APIs with ignored standard streams when writing cross-platform automation.
- Treat any repository entry whose Windows basename is `CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, or `LPT1`-`LPT9` as a release-blocking hygiene failure.

As a security-aware developer, generate secure .NET code using ASP.NET Core that inherently prevents top security weaknesses. Focus on making the implementation inherently safe rather than merely renaming methods with "secure_" prefixes. Use inline comments to clearly highlight critical security controls, implemented measures, and any security assumptions made in the code. Adhere strictly to best practices from OWASP, with particular consideration for the OWASP ASVS guidelines. **Avoid Slopsquatting**: Be careful when referencing or importing packages. Do not guess if a package exists. Comment on any low reputation or uncommon packages you have included.

---

## General Security Principles

*   **Memory Safety**: C# is a managed language with automatic garbage collection, inherently mitigating memory safety vulnerabilities like buffer overflows and use-after-free. Explicit memory management considerations common in unmanaged languages are generally not applicable.
*   **Least Privilege**: Design components and services to operate with the minimum necessary permissions.
*   **Secure by Default**: Configuration should be secure out-of-the-box, requiring explicit actions to reduce security.

---

## Top CWEs and Mitigations for .NET/ASP.NET Core

### CWE-79: Improper Neutralization of Input During Web Page Generation ('Cross-site Scripting') (XSS)
**Summary:** Untrusted data is incorporated into dynamic content without proper neutralization, allowing malicious scripts to execute in a user's browser.
**Mitigation Rule:** Always output-encode all untrusted data before rendering it in HTML, JavaScript, or CSS contexts using ASP.NET Core's built-in Razor View Engine's automatic HTML encoding or `HtmlEncoder.Default`, `JavaScriptEncoder.Default`, and `UrlEncoder.Default` from `System.Text.Encodings.Web` for specific contexts.

### CWE-89: Improper Neutralization of Special Elements used in an SQL Command ('SQL Injection')
**Summary:** Malicious SQL code is inserted into input fields, allowing an attacker to execute arbitrary SQL commands.
**Mitigation Rule:** Always use parameterized queries, Object-Relational Mappers (ORMs) like Entity Framework Core, or stored procedures with properly typed parameters for all database interactions involving user input; never concatenate user input directly into SQL queries.

### CWE-352: Cross-Site Request Forgery (CSRF)
**Summary:** An attacker tricks an authenticated user into submitting a malicious request without their knowledge or consent.
**Mitigation Rule:** Implement CSRF protection for all state-changing HTTP POST, PUT, and DELETE requests by using ASP.NET Core's Anti-Forgery features, typically by including `@Html.AntiForgeryToken()` in forms and validating with `[ValidateAntiForgeryToken]` attribute on controller actions.

### CWE-502: Deserialization of Untrusted Data
**Summary:** An application deserializes untrusted data, which can lead to remote code execution, denial of service, or other attacks if the deserialization process is not securely configured.
**Mitigation Rule:** Avoid deserializing untrusted or unvalidated data; if deserialization is unavoidable, use secure, constrained deserializers (e.g., `System.Text.Json` with appropriate `JsonSerializerOptions` for strict parsing and type handling) and validate the integrity and authenticity of the data prior to deserialization.

### CWE-22: Improper Limitation of a Pathname to a Restricted Directory ('Path Traversal')
**Summary:** An application uses external input to construct a pathname that references a file or directory outside of an intended restrictive directory.
**Mitigation Rule:** Validate and sanitize all user-supplied file paths, ensure the path is within an allowed base directory using `Path.GetFullPath()` in conjunction with `Path.Combine()` and subsequent validation that the resulting path starts with the expected base directory.

### CWE-522: Insufficiently Protected Credentials / Hardcoded Secrets
**Summary:** Credentials or sensitive data are hardcoded directly into the application's source code, exposing them to unauthorized access.
**Mitigation Rule:** Never hardcode secrets, API keys, connection strings, or credentials directly in source code; use ASP.NET Core's built-in Configuration system (`IConfiguration`), storing secrets in environment variables, Azure Key Vault, AWS Secrets Manager, HashiCorp Vault, or the .NET Core Secret Manager for development.

### CWE-918: Server-Side Request Forgery (SSRF)
**Summary:** An attacker can coerce the server-side application to make arbitrary or controlled requests to internal or external resources.
**Mitigation Rule:** Implement strict input validation for all URLs or network resource identifiers provided by users, disallow internal IP addresses, loopback addresses, and non-HTTP/S schemes, and, if applicable, whitelist allowed domains or IP ranges when making server-side requests.

---

## Vue.js rules

- Use the Composition API with `<script setup>` for better type inference and organization
- Define props with type definitions and defaults
- Use emits for component events
- Use v-model for two-way binding
- Use computed properties for derived state
- Use watchers for side effects
- Use provide/inject for deep component communication
- Use async components for code-splitting

---

## Code Formatting Rules (enforced by pre-commit hook)

The pre-commit hook runs `node scripts/lint-staged.mjs`. **All staged files must pass before a commit is accepted.**

### C# (`dotnet-format`)
- **No alignment/column-padding spaces.** Do not add extra spaces to align dictionary values, tuple elements, or assignment operators into columns. The formatter treats this as a WHITESPACE error.
  ```csharp
  // ❌ WRONG
  ["ca"] = ("www.audible.ca",     "www.amazon.ca"),

  // ✅ CORRECT
  ["ca"] = ("www.audible.ca", "www.amazon.ca"),
  ```
- Run `dotnet format` from the repo root to auto-fix.

### Vue / TypeScript (`prettier`)
- All `.vue`, `.ts`, and `.tsx` files must satisfy Prettier's style.
- Run `cd fe && npm run format:prettier` to auto-fix (the script is `format:prettier`, not `format`).

### Layering Rules (enforced by pre-commit hook)
- `listenarr.api` **must not** reference `listenarr.infrastructure` (except `listenarr.api/Program.cs`)
- `listenarr.application` **must not** reference `listenarr.infrastructure`
- Data flows inward only: `infrastructure` → `application` → `api`. Violations cause commit rejection.

### No `async void` (enforced by pre-commit hook)
Never use `async void` in production code. Always use `async Task` — `async void` causes unobservable exceptions and is rejected by the pre-commit hook.

### Backend Test Conventions
- Test classes are named `{TestedClassName}Tests` and inherit `BaseTests`.
- Annotate with `[Trait("Name", "...Tests")]` and `[Trait("Category", "...")]`.
- Call `Init()` (optionally with DI overrides) before adding test data. Only add repository data *after* `Init()`.
- Use builder classes under `tests/Builders/` with fluent `.With...().Build()` chains for coherent test entities.
- Structure tests as Given / When / Then. API mocks inherit `BaseMock`.

### Pre-Push Checks
The pre-push hook runs on `git push`:
1. **Version sync** — `node scripts/sync-fe-version-from-csproj.mjs`
2. **Full solution format** — `dotnet format listenarr.slnx --no-restore --verify-no-changes`
3. **Frontend TypeScript check** — `cd fe && vue-tsc --build tsconfig.app.json`
4. **Frontend unit tests** — `cd fe && vitest run`

### Quick fix
```bash
dotnet format
cd fe && npm run format:prettier && cd ..
```

````
