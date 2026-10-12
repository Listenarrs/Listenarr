# Secure Coding Rules

This file is implementation guidance for anyone (human or AI) writing code in Listenarr. It is
not the vulnerability-reporting policy — if you've found an actual security issue in a released
version, see [`SECURITY.md`](SECURITY.md) instead.

Generate code that is inherently safe rather than merely renamed to look safe (no `secure_`
prefixes standing in for real controls). Use inline comments to call out non-obvious security
controls or assumptions. Follow OWASP practices, with particular attention to OWASP ASVS.
**Avoid slopsquatting**: don't reference or import a package without confirming it exists, and
flag any low-reputation or uncommon package you introduce.

.NET is memory-managed (GC-backed), which mitigates classic buffer-overflow/use-after-free/
double-free issues found in unmanaged languages. Focus stays on logical and application-level
vulnerabilities below.

## Input handling (untrusted data in, safe data out)

### CWE-79 — Cross-Site Scripting (XSS)
Untrusted input rendered without neutralization lets attackers execute scripts in a user's
browser. Apply context-sensitive output encoding for all untrusted data: Razor's automatic HTML
encoding or `HtmlEncoder.Default.Encode` for HTML, `JavaScriptEncoder.Default.Encode` for JS
contexts, `Uri.EscapeDataString`/`WebUtility.UrlEncode` for URLs. Data passed into a JS context
should be JSON-encoded via `JsonSerializer.Serialize`, not string-interpolated.

### CWE-89 — SQL Injection
Never build SQL by concatenating untrusted input. Use parameterized queries or an ORM (EF Core
LINQ queries, or explicit `SqlParameter` objects with ADO.NET) for all database access.

### CWE-22 — Path Traversal
User-supplied input used to construct filesystem paths must be validated before use. Use
`Path.Combine()` to build paths, canonicalize with `Path.GetFullPath()` to resolve `../`
sequences, then verify the canonicalized path remains inside an explicitly defined base with a
delimiter-aware containment check. Never use a raw string-prefix check such as `StartsWith`:
`C:\library-evil` must not be accepted as a child of `C:\library`. Account for the owning
filesystem's syntax and case semantics, Windows drive and UNC roots, alternate separators, and
links. For library filesystem code, use the repository's established path authorization and
identity primitives and follow `BACKEND_ARCHITECTURE.md`; do not invent a new containment helper.

### CWE-77 — Command Injection
Avoid executing OS commands built from user input. If unavoidable, use
`System.Diagnostics.ProcessStartInfo` with the command and each argument passed separately (never
concatenated into one shell string), and don't allow shell interpretation.

### CWE-918 — Server-Side Request Forgery (SSRF)
When a feature accepts a URL or target from user input (e.g. a scraping/metadata source),
validate it against an allow-list of approved domains/IP ranges before the app requests it. Use
an `HttpClient` configuration that respects proxying, and disable or tightly control automatic
redirects so an allow-listed URL can't redirect into an internal resource.

### CWE-502 — Deserialization of Untrusted Data
Deserializing untrusted data with an unsafe formatter can lead to RCE or DoS. Never use
`BinaryFormatter`, `NetDataContractSerializer`, or `SoapFormatter` on untrusted input. Prefer
`System.Text.Json` with options that reject unknown/unexpected types; if using
`Newtonsoft.Json`, disable `TypeNameHandling`. Only deserialize data from trusted sources, and
validate integrity/origin where possible.

## Authentication and access control

### CWE-287 — Improper Authentication
Use ASP.NET Core Identity (or OIDC with a vetted library) for authentication. Enforce strong
password policies, support MFA, hash passwords with `PasswordHasher` (PBKDF2/Argon2), implement
account lockout on repeated failures, and manage session cookies/tokens with `HttpOnly`,
`Secure`, and `SameSite` set appropriately.

### CWE-276 / CWE-732 — Incorrect Permission Assignment (Broken Access Control)
Enforce authorization at every layer — UI, API, and data access — not just the UI. Use
`[Authorize]` with role- or policy-based checks, apply least privilege, and validate
authorization explicitly on every endpoint and resource access rather than assuming a prior
check covers it.

### CWE-352 — Cross-Site Request Forgery (CSRF)
State-changing requests (POST/PUT/DELETE) need CSRF protection. Use ASP.NET Core's anti-forgery
tokens (`[ValidateAntiForgeryToken]`, or `AutoValidateAntiforgeryTokenAttribute` applied
globally) and ensure the token is included in forms and AJAX request headers. For SPA/API
endpoints that can't rely on cookies, use a custom header-token scheme instead.

## Secrets and cryptography

### CWE-259 / CWE-522 — Hard-Coded or Insufficiently Protected Credentials
Never hardcode secrets, API keys, or connection strings in source or committed config. Use
`IConfiguration` backed by a secure provider: `dotnet user-secrets` in development, and
environment variables, Azure Key Vault, AWS Secrets Manager, or HashiCorp Vault in production.

### CWE-327 — Broken or Risky Cryptography
Use modern, industry-recommended algorithms only: AES-256 for symmetric encryption, SHA-256/
SHA-512 for hashing, PBKDF2 or Argon2 (via `PasswordHasher`) for password hashing. Avoid MD5,
SHA1, DES, and RC4. Generate keys/salts with `System.Security.Cryptography.RandomNumberGenerator`
and manage key material through a secrets provider, not inline constants.
