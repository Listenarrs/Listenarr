# Backend Architecture Boundaries

Listenarr is moving toward a layered backend where each project has a clear job:

- `listenarr.domain` owns the domain model, value objects, domain exceptions, and business rules that do not need hosting, persistence, files, or network access.
- `listenarr.application` owns use-case orchestration, application services, DTOs, mapping, and contracts that other layers implement. It can coordinate work, but it should avoid owning persistence, file, network, parsing, or image-processing implementations.
- `listenarr.infrastructure` owns concrete adapters for technical concerns: EF Core and SQLite persistence, filesystem work, external HTTP clients, metadata/tagging libraries, HTML scraping/parsing, image inspection, cache implementations, SignalR infrastructure, and downloader integrations.
- `listenarr.api` is the composition and hosting layer. It wires dependency injection, controllers, middleware, Swagger/OpenAPI, auth policy, and request pipeline behavior.

## Vertical Feature Structure

Active backend code is organized first by feature ownership and then by technical role:

```text
listenarr.domain/<Feature>/
listenarr.application/<Feature>/{Contracts,Models,Services}/
listenarr.infrastructure/<Feature>/{Persistence,Providers,Workers}/
listenarr.api/Features/<Feature>/{Controllers,Models,Mapping}/
tests/Features/<Layer>/<Feature>/
```

The primary feature groups are Library, Downloads, DownloadClients, Search, Metadata,
History, Notifications, Images, Configuration, Identity, Security, and System.
Cross-cutting technical infrastructure is limited to Persistence, Platform, Realtime,
BackgroundJobs, and DependencyInjection.

Namespaces must follow physical folders for active code. EF migrations are immutable
historical artifacts and remain under `Listenarr.Infrastructure.Persistence.Migrations`.
Controllers own HTTP concerns only; reusable workflows belong to application services,
while filesystem, network, persistence, process, and provider implementations belong to
infrastructure.

## Current Decision

The diagram describes the intended boundary: application is business/use-case logic and infrastructure is persistence, files, and external adapters. The codebase is still in transition, but implementation-specific packages should be kept out of `listenarr.application` unless there is a documented reason to do otherwise.

New implementation-specific dependencies should go in `listenarr.infrastructure`. The application layer should define contracts and coordinate use cases; infrastructure should implement those contracts with EF Core, filesystem, HTTP, parsing, image, tagging, and other adapter libraries.

The application project should not reference SQLite providers, EF Core implementation packages, Swagger/OpenAPI packages, HTML parsers, image libraries, audio tagging libraries, ASP.NET Core hosting types, SignalR hubs, HTTP context, or data-protection implementations directly. SQLite and EF Core belong to infrastructure, Swagger/OpenAPI belongs to API, hosted adapters and SignalR delivery belong to infrastructure/API, and parsing/tagging/image inspection belong behind application ports implemented by infrastructure.

## Compatibility and Migration Boundary

Backend compatibility begins at the branch or release that code has actually reached. An unmerged feature branch is free to replace its own schema, recovery protocol, or persisted development artifacts; those intermediate states must not become permanent production compatibility surfaces merely because a developer ran an earlier PR build.

EF migrations become immutable historical artifacts when they merge into the target branch. Before merge, a feature branch should remove superseded branch-only migrations, restore the target branch model snapshot, and regenerate the smallest final EF-scaffolded migration set from the cleaned model. Compatibility and startup reconciliation must be designed against data that can exist on the actual target branch, not against transient schemas that existed only during feature development.

This rule does not authorize deleting current crash-safety evidence. Marker files, journals, leases, identities, or other durable protocols that the final implementation itself writes and requires for restart safety remain part of the current contract. Reviewers must distinguish those from readers, endpoints, states, and migrations whose only purpose is upgrading an obsolete intermediate branch protocol.

## Boundary Cleanup

The application layer now delegates these infrastructure-shaped concerns through interfaces:

- EF Core update failures are translated by infrastructure into application-owned `PersistenceException` types before they leave persistence.
- TagLibSharp ASIN writing is behind `IAudioTagWriter`, implemented by infrastructure.
- ImageSharp cover probing is behind `ICoverImageProbe`, implemented by infrastructure.
- HtmlAgilityPack text extraction and Audible author-page parsing are behind `IHtmlTextExtractor` and `IAudibleAuthorPageParser`, implemented by infrastructure.
- Hosted services and SignalR hubs live in infrastructure. Application code publishes client events through `IHubBroadcaster` instead of referencing hubs or `IHubContext`.
- HTTP request details are exposed to application services through `IRequestContextAccessor`, with ASP.NET Core adaptation handled outside application.
- Secret protection is exposed through `ISecretProtector`, with Data Protection implemented in infrastructure.
- `listenarr.application` no longer has an ASP.NET Core framework reference. It may reference general `Microsoft.Extensions.*` abstractions for logging, options, caching, dependency-factory access, and HTTP client factories, but it should not reference host/web implementation packages.

## API Startup Composition

`listenarr.api/Program.cs` should stay as the host orchestration layer only: create the builder, register services, build the app, run startup tasks, initialize realtime logging, map the request pipeline, and run. Detailed startup behavior belongs in `listenarr.api/Startup`.

Startup modules are grouped by concern:

- `ListenarrBuilderFactory` resolves content root/environment, external config, Serilog, default URLs, and the realtime log sink.
- `ListenarrServiceRegistration` coordinates service registration and owns API versioning setup.
- `ListenarrPlatformRegistration` owns reverse-proxy headers, development CORS, and SignalR JSON protocol setup.
- `ListenarrWorkflowRegistration` owns explicit application/API workflow registrations.
- `ListenarrSecurityStartup` owns antiforgery, Data Protection, and the security middleware order.
- `ListenarrStaticAssetsStartup` owns frontend static files, placeholder fallback, and cached-image static files.
- `ListenarrSwaggerRegistration`, `ListenarrStartupTasks`, and `ListenarrPipeline` own Swagger metadata, startup-time tasks, and the top-level request pipeline respectively.

Infrastructure-specific composition remains behind `listenarr.infrastructure/Extensions/InfrastructureStartupCompositionExtensions.cs` and is invoked by `Program.cs`, the allowed composition root for cross-project infrastructure wiring.

Security middleware order is part of the contract and should remain easy to audit: session cookie authentication, API key authentication, authentication enforcement, CSRF validation, then ASP.NET Core authorization. `UseForwardedHeaders()` must run before security middleware so forwarded scheme/host information is available for cookie and request handling.

`/system/ready` is an anonymous local-prerequisite probe. It verifies database connectivity and that no migrations are pending, returns `503` when either check fails, and must not poll external APIs or download clients. Request logs carry a validated correlation ID, while periodic worker cycles and queue processors add worker/job/entity identifiers through structured logging scopes.

## User-Provided Filesystem Paths

Paths that Listenarr will store or create are validated under an explicit filesystem syntax before normalization. Windows UNC input is parsed delimiter by delimiter so server, share, and child-path boundaries cannot be changed by separator collapsing. Server and share must be non-empty valid Windows segments; current- or parent-directory authority components, reserved device names, invalid characters, trailing spaces or periods, incomplete authorities, and repeated internal separators are rejected. Mixed slash styles are accepted only when they preserve an unambiguous authority, and normalization occurs after validation. Repeated trailing separators on a root may canonicalize to the same root.

Stored or externally reported path identity has a different compatibility contract. Its delimiter-aware Windows normalization may tolerate repeated separators, but it must preserve the same server/share/remainder boundary and must never reinterpret authority text as a child path. A `//...` value is ambiguous between Windows UNC and Unix syntax without an owning filesystem context, so callers must provide the expected syntax instead of guessing.

## Durable Library Move Contracts

Library moves cross request, queue, filesystem, persistence, recovery, realtime, and scan boundaries. The following contracts are authoritative and must remain consistent across those layers.

### Persisted filesystem identity

A `MoveJob` persists the canonical source and target paths together with each endpoint's `FileSystemPathSyntax`, resolved `FileSystemCaseSensitivity`, requested `FileSystemCaseSensitivityMode`, and identity boundary. Identity-key version 3 uses that persisted target identity for active deduplication. API validation, queue deduplication, root-relocation child creation, worker execution, retry, startup reconciliation, and move-scan dispatch must use the same snapshots rather than re-resolving host-default semantics later.

New move jobs must have complete source and target identities before they are persisted, and equivalent source/target endpoints are rejected under the combined endpoint semantics. The physical-move request `SourcePath` is an optimistic-concurrency value only: it must match the audiobook's current `BasePath` and can never authorize moving an unrelated directory. The worker reloads current audiobook state under the required per-audiobook operation boundary immediately before new filesystem mutation. Untouched stale jobs become `Superseded`; malformed or ambiguous state and any mismatch after durable execution evidence exists become `NeedsAttention` so recovery artifacts are preserved. Legacy active jobs may be reconciled once at startup only when their paths and ownership evidence can be attributed safely; ambiguous or malformed jobs fail closed into `NeedsAttention`. A clean legacy identical-endpoint job is terminated as `Superseded` without move history or a move-owned scan handoff, while any manifest or filesystem execution evidence is preserved for operator review. Explicitly case-sensitive or case-insensitive root-folder settings therefore remain authoritative even when the worker host uses different defaults.

Manual move requeue is a single expected-state persistence operation for `Failed`, `NeedsAttention`, or already queued repair cases. Completed and superseded jobs are terminal and cannot be reactivated because their persisted source snapshot is historical or explicitly stale. Requeue repairs both canonical paths, both complete identity snapshots, identity-key version, active deduplication key, retry state, and lease owner/expiration before the job is published to the in-memory channel while preserving the durable recovery phase. Lease generation is never reset. A stale status, concurrent claim, or conflicting active key returns an explicit outcome instead of overwriting newer durable state; startup recovery can safely republish a committed repair after process failure.

Request cancellation remains authoritative while a workflow validates input, resolves filesystem semantics, reads mutable state, and waits for global or per-audiobook operation boundaries. It is checked once more immediately before the first irreversible filesystem mutation or durable queue commit. After that boundary, the workflow must finish the matching ownership, persistence, in-memory publication, and scan-handoff bookkeeping with a non-request cancellation token so a disconnected client cannot leave a partially deleted library, a committed but unpublished queue item, a successful import without its focused scan, or durable terminal state that is missing its in-memory transition. A manual import may still stop later untouched items after cancellation, but every already successful mutation is finalized and the API returns the known committed/partial result instead of converting that committed state into an ambiguous canceled response.

### Target scaffolding ownership

The HTTP workflow validates the nearest existing target ancestor but does not create destination directories. `AudiobookContentMoveService` is the sole owner of move-created target scaffolding. It persists every missing ancestor as `Planned`, prepares the nested chain under a job-specific temporary sibling with a bounded structured ownership marker, and publishes the chain by directory rename before recording it as `Created`.

For a target nested inside the source, every structural ancestor between source and target is excluded from source-content discovery and validated as a single-child spine. Existing unrelated content on that spine is never adopted or moved implicitly. Terminal cleanup is permitted only when the persisted rows and ownership marker identify the same job. Empty owned scaffolding is quarantined behind a durable external cleanup tombstone, validated again after rename, and removed one verified empty directory at a time while the tombstone remains recoverable. Database rows are marked `Removed` only after the quarantine and tombstone are gone; non-empty scaffolding is retained. A recreated published path, unexpected content, missing ownership without a valid cleanup tombstone, corrupt marker, linked entry, or mismatched identity requires operator attention rather than recursive deletion.

### Durable library-directory ownership

Parent cleanup never derives deletion authority from an empty directory or from a cleanup boundary. The boundary is only an upper fence. Directories created by import, manual import, companion import, rename, rename rollback, or retained move scaffolding are created one level at a time through an exclusive operating-system create operation and recorded in `LibraryDirectoryOwnerships` with their canonical path, complete filesystem identity, workflow, operation ID, and random ownership token. Existing directories are not adopted merely because they are empty or appeared during a race. Exclusive creation is the mutation boundary for that level: after creation succeeds, the database claim and marker publication finish noncancelably before request cancellation is re-observed. If fresh claim persistence fails, compensation may remove only that exact newly created directory after a new durable lookup still proves it unowned and mutation safety proves it remains empty and unlinked; any changed, claimed, linked, non-empty, or uncertain path is preserved.

Each owned directory has two matching structured proofs: an inside marker and a token-specific sibling marker. Cleanup requires the database row, both live markers, the same path identity, a non-linked directory, an active mutation boundary, and no content other than the inside marker. Deletion is non-recursive. After the row transitions to `Removing`, the intact directory is atomically renamed to a token-specific sibling quarantine and revalidated there before its inside marker is removed. The user-visible original path is never deleted during restart recovery; if it reappears while the quarantine exists, both paths are preserved for operator attention. A live `Removing` path without its inside marker is likewise preserved because its physical identity can no longer be proven.

After the quarantine is removed, the row is committed as `Removed` while the external sibling proof still exists. That proof is then deleted best-effort, so a database failure leaves restart evidence rather than an unprovable gap. A missing directory can be finalized only from persisted `Removing` state with a valid sibling proof or valid token-specific quarantine. Content appearing before quarantine publication returns the row to `Retained`; content or path replacement after ownership proof changes fails closed. Retired rows and markers are nonauthoritative and cannot block or authorize a later independently proven claim for the same path. Missing, foreign, corrupt, conflicting, unavailable, or replaced ownership evidence remains available for operator diagnosis.

Ownership markers are infrastructure, not audiobook content. A move validates and retires source ownership markers instead of copying them, and an existing owned target is accepted only after its marker pair is revalidated at every mutation checkpoint. Atomic rename is disabled when source ownership markers require explicit retirement. An exact unowned source root is also never deleted by its user-visible path after cleanup. Once empty, it is atomically renamed beneath the job-owned quarantine and deleted there. Restart recovery completes that deterministic quarantine, while recreation of the original path preserves both directories and requires operator attention.

### Filesystem mutation threat boundary

User-visible source, destination, marker, tombstone, and quarantine pathnames are untrusted between every validation and mutation. A destructive operation must therefore remain bound to an opened filesystem object, or first move that exact opened object into a private deterministic state directory and operate on the private claim. Reopening a public leaf after publication must prove that it is the same object; a replacement generation is preserved and blocks cleanup.

Named publication, move, and empty-source `.state` directories support process-crash recovery and are restricted to the owning operating-system account (`0700` on Unix-like systems). Listenarr supports one process per SQLite database and treats another malicious process running as that same account as outside this protocol's threat model: such a process can already mutate the database, configuration, and every managed media path. Unexpected entries or unverifiable objects inside private state fail closed. An interrupted one-shot Unix retirement can leave private evidence for operator review rather than deleting through an uncertain public pathname. The durable named protocols provide process-crash consistency; they do not claim power-loss durability for directory entries on every supported filesystem.

### Durable completion and realtime publication

The durable completion boundary atomically records terminal move state, the idempotent move-history event, and the unique `MoveScanHandoff`. Lease heartbeat and the per-audiobook mutation lock stop after that commit. Webhooks, toasts, scan dispatch, and SignalR publication are post-commit effects and must not make an already completed move appear to lose its lease or roll back filesystem state.

Full `AudiobookUpdate` events are published through `IAudiobookUpdatePublisher`. The infrastructure publisher serializes updates per audiobook and reloads the current entity immediately before mapping and broadcasting, preventing an older move or scan snapshot from overwriting newer client state. Post-commit effects use host cancellation, not the completed move lease token. Root-folder relocation start, retry, and reconciliation likewise check request or host cancellation immediately before the transaction commit, complete the commit noncancelably, and treat SignalR publication as best effort so a committed saga is never reported as rolled back by a later disconnect.

### Durable move-to-scan handoff

Every completed move owns at most one database-unique `MoveScanHandoff`, including the authoritative target path identity. Handoffs transition through `Pending`, `Claimed`, and terminal `Succeeded`, `Failed`, or `Superseded` states. Claim leases and attempt generations fence dispatch, heartbeat renewal, completion, and manual retry. Manual retry must match the exact terminal scan-job ID and attempt generation it is reopening.

`ScanQueueService` keeps move-handoff dispatch reservations private until `MarkDispatchedAsync` succeeds, so ordinary callers never receive an unpublished scan-job ID. `MoveScanHandoffRecoveryService` and immediate post-move dispatch use the same claim path. Before discovery, `ScanJobProcessor` verifies that the handoff target still matches the audiobook's current path identity; stale attempts terminate as `Superseded` without reading files or mutating metadata. Every production terminal path goes through `CommitTerminalJobStatusAsync`: cancellation is checked before terminal persistence, the durable history or handoff decision is then committed noncancelably, and the in-memory queue transition follows with that authoritative outcome before client publication. Lease-loss-only transitions do not create a second terminal record and instead mirror the newer durable owner directly in memory.

## Audiobook File Ownership and Rename Coordination

Audiobook file ownership is a database-enforced filesystem identity contract. `AudiobookFile.Path` remains a storage representation and may be absolute or relative to the owning audiobook's authoritative `BasePath`, but it cannot be mutated independently of its persisted canonical path, syntax, resolved case sensitivity, requested case-sensitivity mode, identity boundary, lookup key, ownership key, version, and state. All production creation flows use `IAudiobookFileService` and `IAudiobookFileRepository.ClaimAsync`; raw check-then-insert path equality is not an ownership decision. A filtered unique database index on valid ownership keys is the final concurrency authority, including simultaneous claims made under different audiobook operation locks.

Legacy rows are reconciled in restart-safe batches after migrations. Resolvable unique rows become `Valid`; equivalent duplicate groups become `Conflict`; unavailable paths remain `Unavailable`. Conflict and unavailable rows are retained rather than reassigned or deleted. A new claim whose conservative lookup identity overlaps unresolved legacy ownership fails closed and requires operator resolution. Every rename, move, destination rewrite, relocation, or other path mutation updates the stored path and complete identity together.

`Audiobook.BasePath` and legacy `FilePath` are metadata and expected-state tokens, not recursive filesystem ownership. A physical move must derive its source root from valid tracked `AudiobookFile` identities and publish an immutable manifest containing the exact files, required directories, lengths, timestamps, and hashes. Queue deduplication binds audiobook, source endpoint, target endpoint, and manifest digest. Workers may load and advance that persisted manifest but cannot create or append ownership evidence during execution. Broad author folders, shared flat folders, and stale metadata paths therefore cannot authorize moving or deleting sibling content. Missing, unresolved, changed, linked, or ambiguous tracked evidence fails closed with an explicit repair or `NeedsAttention` outcome.

Path-scoped scans follow the same boundary rule. The API, manual import workflow, and move handoff must authorize the requested scan path against a configured root or output boundary and persist the complete `PathIdentitySnapshot` before queue publication. `ScanQueueService` never resolves a caller path into authority. Focused scans are explicitly non-authoritative for absence reconciliation; only a complete authoritative scan may delete missing tracked rows, and incomplete enumeration or ambiguous attribution preserves existing records.

When discovery selects exactly one stable-identifier directory, that directory is authoritative for every new ownership claim, including exact-title matching, directory expansion, and embedded-metadata enrichment. Conflicting identifier directories fail closed. Existing tracked files outside the selected directory are preserved for compatibility, but they cannot authorize new outside claims, broaden the selected boundary, or widen `Audiobook.BasePath`. Containment always uses the persisted filesystem syntax and case semantics, and linked files or directories never extend the boundary.

Move manifest identity version 5 hashes a deterministic binary encoding with a fixed header, explicit big-endian field widths, an entry count, length-prefixed normalized UTF-8 paths and hash bytes, and an explicit hash-presence marker. Active jobs from earlier versions are transactionally rebuilt from persisted endpoint identities and manifest entries before enqueue, dequeue, or requeue; malformed jobs require attention rather than receiving guessed keys.

Metadata-only destination rewrites are the explicit repair surface for stale or invalid stored paths. They update metadata and complete path identities under the audiobook operation lock without claiming source filesystem ownership or enqueuing a physical move. Failed physical jobs can be manually requeued only when their persisted source, target, identities, and tracked-file manifest remain valid; otherwise they stay `NeedsAttention` for operator resolution.

Filesystem-mutating and file-ownership-claiming flows, including move, scan, import, file registration, and rename execution, acquire locks in this order:

```text
global filesystem mutation coordinator
→ ordered audiobook operation coordinator
```

Settings, configured roots, audiobook state, file paths, and filesystem identities are loaded after both boundaries are acquired. Rename requests carry the preview's expected current folder and each file's expected current path. Execution validates the complete operation plan, current root authorization, destination ownership, links and traversal defenses, and duplicate destinations before touching the filesystem. A stale folder, root, path, semantics, or ownership snapshot returns a conflict instead of regenerating the request against newer state. Completed file moves are rolled back on later failure when safe; if rollback cannot complete, the actual partial filesystem state is persisted best-effort rather than leaving database paths that knowingly describe nonexistent locations.

Generic `FileMover.MoveDirectoryAsync` fallback is copy-and-verify, never recursive copy-and-delete. It verifies the current source tree against the destination, atomically quarantines each verified source file before a second byte comparison, deletes only those proven copies, and removes directories non-recursively. During the active attempt, a failed comparison restores the quarantined file when the original path has not been recreated; otherwise both paths are preserved. Because this generic helper has no durable ownership row, marker pair, or external tombstone, any cleanup-pattern artifact found on a later retry is preserved and blocks automatic recovery. Filename shape alone never authorizes restoration or deletion. New, changed, linked, conflicting, or otherwise unverified source content is preserved, and the move reports failure whenever the source tree remains. Windows robocopy is copy-only and uses the same verified cleanup protocol rather than `/MOVE`.

Generic single-file fallback also reports success only after a verified destination is published and the verified source is absent. Managed fallback copies to a unique sibling staging file, compares bytes, atomically publishes, compares again, and then removes the source. Robocopy is copy-only into an exclusively created staging directory that carries a random ownership token; the directory, marker, and path boundary are revalidated after the process exits and before any publication or cleanup. A retained source, missing or mismatched staged file, replaced staging directory, link, invalid marker, or unverifiable content returns failure and preserves uncertain paths. The helper never creates a missing destination hierarchy behind the library-directory ownership protocol.

## Background Worker Ownership

Hosted workers must have one clear owner for each state transition. Queue services can dedupe, persist, or expose job status, but they should not perform the durable state transition that belongs to a worker.

Audiobook path-bearing mutations are serialized per audiobook through `IAudiobookOperationCoordinator`. Move, scan, import, rename, file registration, and metadata-only destination rewrites must acquire the keyed operation boundary before loading mutable audiobook path state. The coordinator is reentrant for nested same-audiobook helpers and follows the documented single Listenarr process per database deployment model.

Background workers expose DI-facing processor contracts for deterministic cycle/job testing. Periodic workers should prefer `IWorkerCycleRunner` and `TimeProvider` for cancellation-safe loops and testable delays. Queue-backed workers should keep hosted services as channel adapters and put per-job orchestration in processors such as `ScanJobProcessor` and `MoveJobProcessor`. Exception filters should use `WorkerExceptionClassifier.IsNonFatal` when adding or refactoring catch blocks so fatal runtime exceptions are not swallowed. Worker processors should emit lightweight `worker.*` metrics for started, completed, failed, skipped, and retry-scheduled outcomes where the state applies.

| Worker | Processor | Owned durable transitions | Forbidden transitions | Retry/backoff | Idempotency | Handoff |
| --- | --- | --- | --- | --- | --- | --- |
| `DownloadMonitorService` | `DownloadMonitorProcessor` | Polls enabled clients, updates active download progress, transitions client-reported failures to `Failed`, removes unreconcilable active client-backed downloads after a trusted live client snapshot proves either that their external ID is missing or that a non-DDL record has no stored external client ID, and enqueues an import job only when a download transitions to `Completed`. | Must not import, move, scan, or clean up files. | Per-client exponential polling backoff, capped at 15 minutes; success resets the client failure counter. | Download submission reserves a database-unique active audiobook key before contacting a client; duplicate import enqueue is delegated to `DownloadProcessingJobService`. Orphan cleanup is skipped for cached, unavailable, or suspiciously empty snapshots. DDL downloads are internally tracked with `DownloadClientId == "DDL"` and are excluded from external-ID cleanup. | `DownloadProcessingJobService` receives completed downloads for import. |
| `DirectDownloadService` | `DirectDownloadProcessor` | Owns internal DDL transfer state: `Queued -> Downloading -> Completed/Failed`, writes one trusted artifact or an atomic artifact batch to local staging, updates aggregate progress, and enqueues one import job only after every artifact is durable. | Must not import or extract files, mark imports final, poll external download clients, or clean up moved downloads. | Periodic polling; failed HTTP/file writes remove the whole staging batch and transition the DDL record to `Failed` so active deduplication is released and the UI stops showing a stuck queued item. | Only rows with `DownloadClientId == "DDL"` and a supported direct-download source policy are fetched; the selected policy validates the complete plan, every original URL, and every redirect target. Partial files are written under app config storage with a `.partial` suffix and replaced atomically on success. Rows without a persisted artifact plan retain legacy one-file behavior. | `DownloadProcessingJobService` receives completed local DDL files or directories for import. |
| `DownloadProcessingJobProcessor` | `DownloadProcessingJobProcessor` | Owns import execution and checkpointed finalization: `Completed -> ImportPending -> Moved` on success and `Completed/ImportPending -> ImportBlocked` after retries are exhausted. `Moved`, processing-job completion, and the terminal import history event are committed together only after files are registered, the client item is marked imported, and a scan is queued. DDL imports resolve source files directly from Listenarr's local staging path and skip external-client mark-import calls. | Must not poll clients, download DDL payloads, or perform deferred client cleanup. | Job-level retry via `DownloadProcessingJob.ScheduleRetry`; persisted checkpoints prevent completed file imports from being repeated during finalization retries. | Active jobs use a database-unique normalized download key; recent completed jobs retain the cooldown guard. A stale job for an already `Moved` download completes as a no-op. | `ScanQueueService` receives the post-import library scan request. |
| `DownloadProcessingJobCleanupService` | `DownloadProcessingJobCleanupProcessor` | Deletes old terminal `DownloadProcessingJob` rows after the retention window so the processing table does not grow unbounded. | Must not import downloads, move files, poll clients, remove client items, or change download state. | Daily cadence after startup delay; non-fatal failures are logged by the shared worker cycle runner and retried on the next cycle. | Only terminal `Completed`/`Failed` jobs older than retention are removed; active `Pending`, `Processing`, and `Retry` jobs remain untouched even when old. | `IDownloadProcessingJobService.CleanupOldJobsAsync` performs the cleanup. |
| `ScanBackgroundService` | `ScanJobProcessor` | Consumes preauthorized scan jobs, reconciles only files attributable to the audiobook within the persisted path boundary, and commits the authoritative move-scan handoff attempt before changing the in-memory scan state. | Must not move audiobook roots, import download payloads, manufacture path authority, claim ambiguous files, or delete missing rows from a focused or incomplete scan. | Ordinary in-memory scans can be requeued explicitly with their original identity and authority scope. Move-owned scans use a durable `MoveScanHandoff` lease and attempt generation; failed handoffs can be explicitly reopened and later attempts supersede stale workers. | `ScanQueueService` keeps database work outside its short in-memory queue gate and dedupes by audiobook, endpoint identity, correlation, and reconciliation authority. `MoveScanHandoffRecoveryService` atomically claims pending or expired handoffs, and terminal handoff updates are fenced by attempt generation and database idempotency keys. | Broadcasts library updates only after durable terminal completion. Move completion creates one database-unique `MoveScanHandoff`; immediate dispatch and periodic recovery use the same claim path and persisted target identity. |
| `MoveBackgroundService` | `MoveJobProcessor` | Owns audiobook filesystem relocation and move-job transitions `Queued/RetryScheduled -> Running -> Completed/Failed/NeedsAttention/Superseded`, including immutable tracked-file manifest checkpoints, target scaffolding, filesystem ownership evidence, metadata rebasing, artifact cleanup, and completion handoffs. | Must not import downloads, infer ownership from `BasePath`, create manifest entries during execution, move foreign sibling content, or claim scan execution ownership. It may request a post-move scan only through the durable scan handoff store. | Transient filesystem and completion-handoff failures use persisted exponential backoff with jitter and a bounded automatic retry count. Exhaustion transitions to `NeedsAttention`; explicit manual requeue validates the persisted source, target, complete identities, and manifest before resetting the retry budget while preserving lease generation fencing. Legacy or manifestless jobs remain `NeedsAttention` unless reconciliation can prove a safe terminal outcome. | `MoveQueueService` uses async persistence plus a database-unique versioned key over audiobook, source endpoint, target endpoint, and manifest digest. `IMoveExecutionStore` translates provider failures and fences every mutation/checkpoint by lease generation. Structured markers, immutable manifests, tombstoned scaffold cleanup, and persisted target scaffolding make replay idempotent. Ordinary foreign content is preserved; only manifest-owned paths and Listenarr-owned scaffolding are eligible for cleanup. Move history, one unique `MoveScanHandoff`, and terminal move state are committed atomically for genuine completions. | After terminal commit, performs durable scan-handoff dispatch and best-effort webhooks, toasts, and audiobook broadcasts outside the per-audiobook lock. |
| `MovedDownloadCleanupService` | `MovedDownloadCleanupProcessor` | Owns deferred download-client cleanup only for `Moved` downloads whose client policy requests cleanup and whose import is proven durable by a completed processing job, `LastImportedAt`, imported unified history, legacy imported download history, or old legacy `Moved` state. It removes the operational DB record only after configured client cleanup succeeds; the `none` policy retains the imported record. Legacy `Moved`-state proof may remove stale client/DB state but must not authorize external file deletion. | Must never import files, clean up an uncommitted import, delete history, or change a download back out of `Moved`. | Polls on the configured interval and retains failed cleanup records for future retries. | Cleanup attempts share the import correlation ID and remain in append-only history after operational records are removed. | Download-client gateway removes eligible client items. |
| `QueueMonitorService` | `QueueMonitorProcessor` | None; it observes external queue snapshots and emits SignalR updates. | Must not persist download/import/scan state. | Adaptive polling interval based on queue activity. | Snapshot comparison suppresses duplicate broadcasts. | `DownloadHub` receives `QueueUpdate` messages. |
| `AutomaticSearchService` | `AutomaticSearchProcessor` | Owns periodic wanted-item search decisions and download submission requests. | Must not import downloads, move files, or mark scan state. | Runs every 6 hours after startup delay; one failed audiobook does not stop the cycle. | Active-download and cutoff-quality checks prevent duplicate active work on replay. | `IDownloadService.StartDownloadAsync` creates the download handoff. |
| `AuthorMonitoringBackgroundService` | `AuthorMonitoringProcessor` | Owns due monitored-author catalog sync metadata updates. | Must not alter download/import/scan state. | Daily cadence with cancellation-safe cycles. | Sync operations should upsert/cache provider state rather than create duplicate monitored entries. | `IAuthorMonitoringService` performs provider sync. |
| `SeriesMonitoringBackgroundService` | `SeriesMonitoringProcessor` | Owns due monitored-series catalog sync metadata updates. | Must not alter download/import/scan state. | Daily cadence with cancellation-safe cycles. | Sync operations should upsert/cache provider state rather than create duplicate monitored entries. | `ISeriesMonitoringService` performs provider sync. |
| `MetadataRescanService` | `MetadataRescanProcessor` | Owns background metadata enrichment for files missing metadata and cleanup of non-audio file records discovered during rescan. | Must not claim file ownership from scan/import services. | Five-minute cadence; per-file failures are logged and isolated. | Updates missing or stale metadata only; repeated cycles skip files once metadata is present. | Metadata extraction service supplies file metadata. |
| `ImageCacheCleanupService` | `ImageCacheCleanupProcessor` | Owns image temp-cache expiration cleanup. | Must not mutate audiobook/download metadata. | Daily cleanup cadence after the first midnight delay. | Missing or already deleted files are treated as successful cleanup by the cache service. | `IImageCacheService.ClearTempCacheAsync` performs storage cleanup. |
| `FfmpegInstallBackgroundService` | `FfmpegInstallProcessor` | Owns non-blocking ffprobe/ffmpeg availability checks and install attempts. | Must not block host startup or mutate unrelated settings. | Runs once outside request startup; failures are reported without stopping the host. | Rechecks installed binaries before downloading/installing. | `DownloadHub` receives `FfmpegInstallStatus`. |
| `UnmatchedScanBackgroundService` | `UnmatchedScanProcessor` | Owns Library Import unmatched-file scan job status `Queued -> Processing -> Completed/Failed` and cached result replacement. | Must not import matched files or create audiobook records. | Queue-driven; failed/finished jobs can be superseded by a new explicit scan. | Groups files deterministically and clears stale unmatched results for the scanned root. | `SettingsHub` receives `UnmatchedScanComplete`. |

## Download Queue Visibility

`DownloadQueueService` fetches full live client queue snapshots for reconciliation, rebinding, stale-snapshot reporting, and completed-external display. That full snapshot is an internal input, not the user-visible Activity contract.

The user-facing Activity queue should expose Listenarr-owned active downloads only. Each active external queue item must first match a Listenarr download by stored ID, client-specific ID, torrent hash, or a safe non-ambiguous title/artist fallback before it is shown. Unmatched active external items from shared clients such as Transmission, qBittorrent, SABnzbd, or NZBGet must be hidden from Activity so unrelated user transfers do not appear as Listenarr work.

Unmatched completed external items are a separate opt-in display feature and may be shown only by the completed-external display path when `ShowCompletedExternalDownloads` is enabled. Do not use that setting to expose unmatched active external items.

## Download Client Adapter Slicing

Each concrete download-client adapter should be a thin facade over client-specific workflows. This keeps the application-facing `IDownloadClientAdapter` contract stable while preventing one large adapter class from owning HTTP/XML-RPC calls, response parsing, monitor polling policy, import path resolution, and client cleanup at the same time.

Use this shape for every supported client:

- `<Client>Adapter` exposes `IDownloadClientAdapter`, `ClientId`, `ClientType`, and `Protocol`, then delegates to constructor-injected workflows.
- Infrastructure DI owns concrete workflow and protocol-helper composition. Keep workflows and helpers internal implementation details; register them explicitly in `DownloadClientRegistrationExtensions` rather than manually constructing them inside adapter facades.
- `<Client>ConnectionTester` owns connection-test behavior and user-facing connection messages.
- `<Client>AddWorkflow` owns submission behavior and client-specific add quirks.
- `<Client>QueueFetchWorkflow` owns live queue polling, ID-filtered monitor polling, and monitor/display failure semantics.
- `<Client>ItemFetchWorkflow` owns full external item-list display behavior.
- `<Client>HistoryFetchWorkflow` or `<Client>HistoryEnrichmentWorkflow` owns history lookup/enrichment where the client exposes history.
- `<Client>RemovalWorkflow` owns external client cleanup only.
- `<Client>ImportItemResolver` owns import-source resolution and retry-aware path lookup.
- `<Client>ResponseMapper` should stay focused on translating client payloads into Listenarr models.
- `<Client>RequestPlanner`, `<Client>RpcClient`, and auth/session helpers own protocol mechanics.

Do not move concrete downloader behavior into `listenarr.application`. Client-specific HTTP, XML-RPC, JSON parsing, history quirks, category behavior, seed-limit evaluation, and cleanup semantics belong in `listenarr.infrastructure/DownloadClients/<Client>`. Do not introduce a shared abstract base adapter for all clients unless the behavior is genuinely identical. Prefer uniform class roles over forced inheritance because NZBGet, SABnzbd, qBittorrent, and Transmission expose different APIs and different lifecycle semantics.

When refactoring a client, preserve unique behavior explicitly in the workflow that owns it. Examples include NZBGet history/final-path enrichment, SABnzbd queue/history reconciliation, qBittorrent tracker injection and post-import category marking, and Transmission local ID filtering for monitor polls.

The main download handoff is:

1. `DownloadMonitorService` observes the external client and persists `Completed`; for DDLs, `DirectDownloadService` downloads the trusted artifact plan locally and persists `Completed` only after the complete plan is durable.
2. DDL sources are additive through infrastructure source policies. Adding a new DDL source should not require changing `DirectDownloadProcessor`; each source must provide explicit allow-list validation for complete plans, initial URLs, and redirects.
3. `DownloadProcessingJobService` creates or returns the single active/recent import job for that download.
4. `DownloadProcessingJobProcessor` imports and registers files, marks the client item imported, enqueues a scan, and persists checkpoints after each step.
5. `ImportFinalizationService` commits the terminal history event, completed processing job, and `Moved` download state in one database save.
6. `ScanBackgroundService` reconciles the library files asynchronously and records its outcome.
7. `MovedDownloadCleanupService` drives `MovedDownloadCleanupProcessor`, which performs deferred client cleanup according to the client removal policy.

`History` is the canonical append-only activity ledger. Download-specific history interfaces are compatibility adapters over this ledger; they must not mutate earlier events to represent later outcomes. Events carry an outcome, correlation ID, optional parent ID, entity identifiers, and structured details so the API can provide Sonarr-style paging, filtering, sorting, and row details. `HistoryRetentionDays = 0` means unlimited retention.

These guarantees are tested in three layers:

- `HostedServicesRegistrationTests` keeps every hosted worker registered once, requires every worker and processor to appear in this document, and verifies processor interfaces resolve through DI.
- Processor tests such as `WorkerProcessorBoundaryTests`, `ScanJobProcessorTests`, `MoveJobProcessorTests`, `DownloadProcessingJobProcessorTests`, and `DownloadProcessingJobCleanupProcessorTests` exercise durable state ownership, idempotent replay behavior, cancellation behavior, and handoffs without running long-lived hosted loops.
- `WorkerCycleRunnerTests` pins down the shared `worker.{name}.cycle.started`, `completed`, `failed`, and `skipped` metric contract used by periodic workers.

## Migration Direction

Use this pattern when moving a concern out of application:

1. Keep the application-level interface, DTOs, and result models in `listenarr.application` or `listenarr.domain`.
2. Move the concrete implementation to the appropriate `listenarr.infrastructure` feature or technology folder.
3. Register the implementation in `listenarr.infrastructure/Extensions/InfrastructureServiceRegistrationExtensions.cs`.
4. Keep `listenarr.api` responsible for calling the registration extension and composing the host.
5. Add or update focused tests before deleting the old implementation.

Recommended follow-up slices:

- Revisit background workers that combine orchestration with persistence or filesystem details and split the use case from the hosted adapter.
- Continue replacing direct service-locator patterns with narrower application ports where a worker or service only needs one operation from another layer.
- Keep new host-specific concerns in API or infrastructure and expose them to application through small application-owned contracts.

Until those slices are complete, reviewers should treat any new infrastructure-shaped application dependency as a boundary regression unless it is explicitly documented.
