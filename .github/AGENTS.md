````markdown
# Secure .NET Code Generation Codex

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

As a security-aware developer, generate secure .NET code using ASP.NET Core that inherently prevents top security weaknesses.
Focus on making the implementation inherently safe rather than merely renaming methods with "secure_" prefixes.
Use inline comments to clearly highlight critical security controls, implemented measures, and any security assumptions made in the code.
Adhere strictly to best practices from OWASP, with particular consideration for the OWASP ASVS guidelines.
**Avoid Slopsquatting**: Be careful when referencing or importing packages. Do not guess if a package exists. Comment on any low reputation or uncommon packages you have included.


### Memory Safety Considerations for .NET
.NET is a memory-managed language that utilizes a garbage collector. This significantly mitigates common memory safety issues such as buffer overflows, use-after-free, and double-free vulnerabilities that are prevalent in languages like C or C++. Therefore, explicit rules for low-level memory safety are not required, as these are handled by the .NET Runtime. Focus should remain on logical and application-level security vulnerabilities.


### CWE-89: Improper Neutralization of Special Elements used in an SQL Command ('SQL Injection')
**Summary:** The application constructs all or part of an SQL query using externally-controlled input, allowing an attacker to modify the intended query.
**Mitigation Rule:** Always use parameterized queries (e.g., `SqlCommand` with `SqlParameter`, Entity Framework Core LINQ queries, or Dapper with anonymous objects) to separate SQL logic from user-supplied data. Never concatenate user input directly into SQL strings.

### CWE-79: Improper Neutralization of Input During Web Page Generation ('Cross-site Scripting')
**Summary:** The application incorporates untrusted input into web page content without proper encoding, allowing attackers to inject malicious scripts into the user's browser.
**Mitigation Rule:** Apply context-sensitive output encoding for all user-supplied data displayed in HTML using ASP.NET Core's built-in Razor `@` syntax for HTML encoding by default, or explicitly using `HtmlEncoder.Default.Encode()` for other contexts. For JavaScript, use `JavaScriptEncoder.Default.Encode()`.

### CWE-502: Deserialization of Untrusted Data
**Summary:** The application deserializes untrusted data without proper validation or constraint, which can lead to remote code execution, denial of service, or other attacks.
**Mitigation Rule:** Avoid deserializing untrusted or unvalidated data using insecure formatters like `BinaryFormatter`. When deserializing data, use secure, constrained formats (e.g., `System.Text.Json` with `JsonSerializerOptions` configured to forbid unknown types, or `Newtonsoft.Json` with `JsonSerializerSettings` configured to disable `TypeNameHandling` and `SerializationBinder`). Validate the integrity and origin of all serialized data.

### CWE-259: Use of Hard-coded Password (and other secrets)
**Summary:** The application contains sensitive information, such as passwords, API keys, or cryptographic keys, directly embedded in the source code.
**Mitigation Rule:** Never hardcode secrets (e.g., connection strings, API keys, cryptographic keys) directly in the code or configuration files checked into source control. Utilize ASP.NET Core's configuration system (`IConfiguration`) to load secrets from secure sources like Azure Key Vault, AWS Secrets Manager, HashiCorp Vault, environment variables, or User Secrets during development.

### CWE-22: Improper Limitation of a Pathname to a Restricted Directory ('Path Traversal')
**Summary:** The application uses untrusted input to construct a file path without proper validation, allowing attackers to access or modify files outside the intended directory.
**Mitigation Rule:** Sanitize all user-supplied input used in file paths by using `Path.GetFileName()` to extract only the filename, validating against allowed file extensions, and performing strict allow-listing of allowed characters. Ensure all file operations are confined to a designated, restricted directory, and use `Path.Combine()` for constructing safe paths.

### CWE-287: Improper Authentication
**Summary:** The application allows an actor to pose as a legitimate user without proper validation of their identity.
**Mitigation Rule:** Implement robust authentication mechanisms using ASP.NET Core Identity or OpenID Connect (OIDC) with battle-tested libraries. Enforce strong password policies, multi-factor authentication (MFA), account lockout for failed login attempts, and use secure password hashing algorithms (e.g., `PasswordHasher` from `Microsoft.AspNetCore.Identity`).

### CWE-732: Incorrect Permission Assignment for Critical Resource (Broken Access Control)
**Summary:** The application fails to properly restrict access to resources based on the user's authorization level, allowing unauthorized users to perform actions or access data they should not.
**Mitigation Rule:** Implement granular authorization checks using ASP.NET Core's built-in `[Authorize]` attributes with roles, policies, or custom authorization handlers. Apply the principle of least privilege, ensuring users and services only have the minimum necessary permissions required for their functions. Validate authorization at every API endpoint and for every resource access.


## Vue.js rules

- Use the Composition API with `<script setup>` for better type inference and organization
- Define props with type definitions and defaults
- Use emits for component events
- Use v-model for two-way binding
- Use computed properties for derived state
- Use watchers for side effects
- Use provide/inject for deep component communication
- Use async components for code-splitting


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

### Quick fix
```bash
dotnet format
cd fe && npm run format:prettier && cd ..
```

### Layering Rules (enforced by pre-commit hook)

- `listenarr.api` **must not** reference `listenarr.infrastructure` (except `listenarr.api/Program.cs`)
- `listenarr.application` **must not** reference `listenarr.infrastructure`
- Data flows inward only: `infrastructure` → `application` → `api`

Violations cause commit rejection with a "Layering violation" error.

### No `async void` (enforced by pre-commit hook)

Never use `async void` in production code. Always use `async Task` — `async void` causes unobservable exceptions and the pre-commit hook will reject it.

### Backend Test Conventions

- Test classes are named `{TestedClassName}Tests` and mirror the source project path under `tests/`.
- Every test class inherits `BaseTests`.
- Annotate with `[Trait("Name", "...Tests")]` and `[Trait("Category", "...")]`.
- Call `Init()` (optionally with DI overrides) before adding test data:
  ```csharp
  Init(services => services.WithSingleton(myMock.Object));
  Init(services => services.Without<IServiceToRemove>());
  ```
- Only add repository data *after* calling `Init()`.
- Use builder classes under `tests/Builders/` with fluent `.With...().Build()` chains for coherent test entities. Add a focused builder rather than repeating multi-property inline object initializers.
- Structure tests as Given / When / Then.
- API mocks inherit `BaseMock`; use `GetCallCount()`, `GetLastRequest()`, `GetLastContent()`.

### Pre-Push Checks

The pre-push hook runs additional checks on `git push`:
1. **Version sync** — `node scripts/sync-fe-version-from-csproj.mjs`
2. **Full solution format** — `dotnet format listenarr.slnx --no-restore --verify-no-changes`
3. **Frontend TypeScript check** — `cd fe && vue-tsc --build tsconfig.app.json`
4. **Frontend unit tests** — `cd fe && vitest run`
````
