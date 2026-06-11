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
````
