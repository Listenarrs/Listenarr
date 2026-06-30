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

## Background Worker Ownership

Hosted workers must have one clear owner for each state transition. Queue services can dedupe, persist, or expose job status, but they should not perform the durable state transition that belongs to a worker.

Background workers expose DI-facing processor contracts for deterministic cycle/job testing. Periodic workers should prefer `IWorkerCycleRunner` and `TimeProvider` for cancellation-safe loops and testable delays. Queue-backed workers should keep hosted services as channel adapters and put per-job orchestration in processors such as `ScanJobProcessor` and `MoveJobProcessor`. Exception filters should use `WorkerExceptionClassifier.IsNonFatal` when adding or refactoring catch blocks so fatal runtime exceptions are not swallowed. Worker processors should emit lightweight `worker.*` metrics for started, completed, failed, skipped, and retry-scheduled outcomes where the state applies.

| Worker | Processor | Owned durable transitions | Forbidden transitions | Retry/backoff | Idempotency | Handoff |
| --- | --- | --- | --- | --- | --- | --- |
| `DownloadMonitorService` | `DownloadMonitorProcessor` | Polls enabled clients, updates active download progress, transitions client-reported failures to `Failed`, removes unreconcilable active client-backed downloads after a trusted live client snapshot proves either that their external ID is missing or that a non-DDL record has no stored external client ID, and enqueues an import job only when a download transitions to `Completed`. | Must not import, move, scan, or clean up files. | Per-client exponential polling backoff, capped at 15 minutes; success resets the client failure counter. | Download submission reserves a database-unique active audiobook key before contacting a client; duplicate import enqueue is delegated to `DownloadProcessingJobService`. Orphan cleanup is skipped for cached, unavailable, or suspiciously empty snapshots. DDL downloads are internally tracked with `DownloadClientId == "DDL"` and are excluded from external-ID cleanup. | `DownloadProcessingJobService` receives completed downloads for import. |
| `DirectDownloadService` | `DirectDownloadProcessor` | Owns internal DDL transfer state: `Queued -> Downloading -> Completed/Failed`, writes one trusted artifact or an atomic artifact batch to local staging, updates aggregate progress, and enqueues one import job only after every artifact is durable. | Must not import or extract files, mark imports final, poll external download clients, or clean up moved downloads. | Periodic polling; failed HTTP/file writes remove the whole staging batch and transition the DDL record to `Failed` so active deduplication is released and the UI stops showing a stuck queued item. | Only rows with `DownloadClientId == "DDL"` and a supported direct-download source policy are fetched; the selected policy validates the complete plan, every original URL, and every redirect target. Partial files are written under app config storage with a `.partial` suffix and replaced atomically on success. Rows without a persisted artifact plan retain legacy one-file behavior. | `DownloadProcessingJobService` receives completed local DDL files or directories for import. |
| `DownloadProcessingJobProcessor` | `DownloadProcessingJobProcessor` | Owns import execution and checkpointed finalization: `Completed -> ImportPending -> Moved` on success and `Completed/ImportPending -> ImportBlocked` after retries are exhausted. `Moved`, processing-job completion, and the terminal import history event are committed together only after files are registered, the client item is marked imported, and a scan is queued. DDL imports resolve source files directly from Listenarr's local staging path and skip external-client mark-import calls. | Must not poll clients, download DDL payloads, or perform deferred client cleanup. | Job-level retry via `DownloadProcessingJob.ScheduleRetry`; persisted checkpoints prevent completed file imports from being repeated during finalization retries. | Active jobs use a database-unique normalized download key; recent completed jobs retain the cooldown guard. A stale job for an already `Moved` download completes as a no-op. | `ScanQueueService` receives the post-import library scan request. |
| `DownloadProcessingJobCleanupService` | `DownloadProcessingJobCleanupProcessor` | Deletes old terminal `DownloadProcessingJob` rows after the retention window so the processing table does not grow unbounded. | Must not import downloads, move files, poll clients, remove client items, or change download state. | Daily cadence after startup delay; non-fatal failures are logged by the shared worker cycle runner and retried on the next cycle. | Only terminal `Completed`/`Failed` jobs older than retention are removed; active `Pending`, `Processing`, and `Retry` jobs remain untouched even when old. | `IDownloadProcessingJobService.CleanupOldJobsAsync` performs the cleanup. |
| `ScanBackgroundService` | `ScanJobProcessor` | Consumes scan jobs and reconciles audiobook files/metadata for the audiobook library path. | Must not move audiobook roots or import download payloads. | In-memory scan jobs can be requeued from failed/completed/queued status. | `ScanQueueService` dedupes queued/processing jobs by audiobook and path; explicit rescans are allowed after completion/failure. | Broadcasts library updates after reconciliation. |
| `MoveBackgroundService` | `MoveJobProcessor` | Owns audiobook filesystem relocation and move-job status transitions `Queued -> Processing -> Completed/Failed`. | Must not import downloads or rewrite scan ownership. | Failed jobs keep `AttemptCount` and can be requeued through `MoveQueueService`. | `MoveQueueService` uses async persistence plus a database-unique active deduplication key for audiobook and requested path; terminal transitions release the key. | Broadcasts library updates after a completed move. |
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
