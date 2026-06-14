# Backend Architecture Boundaries

Listenarr is moving toward a layered backend where each project has a clear job:

- `listenarr.domain` owns the domain model, value objects, domain exceptions, and business rules that do not need hosting, persistence, files, or network access.
- `listenarr.application` owns use-case orchestration, application services, DTOs, mapping, and contracts that other layers implement. It can coordinate work, but it should avoid owning persistence, file, network, parsing, or image-processing implementations.
- `listenarr.infrastructure` owns concrete adapters for technical concerns: EF Core and SQLite persistence, filesystem work, external HTTP clients, metadata/tagging libraries, HTML scraping/parsing, image inspection, cache implementations, SignalR infrastructure, and downloader integrations.
- `listenarr.api` is the composition and hosting layer. It wires dependency injection, controllers, middleware, Swagger/OpenAPI, auth policy, and request pipeline behavior.

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

## Background Worker Ownership

Hosted workers must have one clear owner for each state transition. Queue services can dedupe, persist, or expose job status, but they should not perform the durable state transition that belongs to a worker.

Background workers expose DI-facing processor contracts for deterministic cycle/job testing. Periodic workers should prefer `IWorkerCycleRunner` and `TimeProvider` for cancellation-safe loops and testable delays. Queue-backed workers should keep hosted services as channel adapters and put per-job orchestration in processors such as `ScanJobProcessor` and `MoveJobProcessor`. Exception filters should use `WorkerExceptionClassifier.IsNonFatal` when adding or refactoring catch blocks so fatal runtime exceptions are not swallowed. Worker processors should emit lightweight `worker.*` metrics for started, completed, failed, skipped, and retry-scheduled outcomes where the state applies.

| Worker | Processor | Owned durable transitions | Forbidden transitions | Retry/backoff | Idempotency | Handoff |
| --- | --- | --- | --- | --- | --- | --- |
| `DownloadMonitorService` | `DownloadMonitorProcessor` | Polls enabled clients, updates active download progress, transitions client-reported failures to `Failed`, and enqueues an import job only when a download transitions to `Completed`. | Must not import, move, scan, or clean up files. | Per-client exponential polling backoff, capped at 15 minutes; success resets the client failure counter. | Duplicate import enqueue is delegated to `DownloadProcessingJobService`. | `DownloadProcessingJobService` receives completed downloads for import. |
| `DownloadProcessingJobProcessor` | `DownloadProcessingJobProcessor` | Owns import execution: `Completed -> ImportPending -> Moved` on success and `Completed/ImportPending -> ImportBlocked` after job retries are exhausted. It also enqueues the post-import library scan. | Must not poll clients or perform deferred client cleanup. | Job-level retry via `DownloadProcessingJob.ScheduleRetry`; pending retry jobs are ignored until `NextRetryAt`. | Active/recent completed jobs dedupe in `DownloadProcessingJobService`. A stale job for an already `Moved` download completes as a no-op. | `ScanQueueService` receives the post-import library scan request. |
| `ScanBackgroundService` | `ScanJobProcessor` | Consumes scan jobs and reconciles audiobook files/metadata for the audiobook library path. | Must not move audiobook roots or import download payloads. | In-memory scan jobs can be requeued from failed/completed/queued status. | `ScanQueueService` dedupes queued/processing jobs by audiobook and path; explicit rescans are allowed after completion/failure. | Broadcasts library updates after reconciliation. |
| `MoveBackgroundService` | `MoveJobProcessor` | Owns audiobook filesystem relocation and move-job status transitions `Queued -> Processing -> Completed/Failed`. | Must not import downloads or rewrite scan ownership. | Failed jobs keep `AttemptCount` and can be requeued through `MoveQueueService`. | `MoveQueueService` dedupes active jobs by audiobook and requested path, including persisted jobs. | Broadcasts library updates after a completed move. |
| `MovedDownloadCleanupService` | `MovedDownloadCleanupProcessor` | Owns deferred download-client cleanup after import has already reached `Moved`, then removes the DB queue record. | Must never import files or change a download back out of `Moved`. | Polls on the configured interval; retries cleanup until the client allows removal or grace periods expire. | Stale removal records are cleaned after grace periods; repeated cycles are no-ops once the DB record is removed. | Download-client gateway removes eligible client items. |
| `QueueMonitorService` | `QueueMonitorProcessor` | None; it observes external queue snapshots and emits SignalR updates. | Must not persist download/import/scan state. | Adaptive polling interval based on queue activity. | Snapshot comparison suppresses duplicate broadcasts. | `DownloadHub` receives `QueueUpdate` messages. |
| `AutomaticSearchService` | `AutomaticSearchProcessor` | Owns periodic wanted-item search decisions and download submission requests. | Must not import downloads, move files, or mark scan state. | Runs every 6 hours after startup delay; one failed audiobook does not stop the cycle. | Active-download and cutoff-quality checks prevent duplicate active work on replay. | `IDownloadService.StartDownloadAsync` creates the download handoff. |
| `AuthorMonitoringBackgroundService` | `AuthorMonitoringProcessor` | Owns due monitored-author catalog sync metadata updates. | Must not alter download/import/scan state. | Daily cadence with cancellation-safe cycles. | Sync operations should upsert/cache provider state rather than create duplicate monitored entries. | `IAuthorMonitoringService` performs provider sync. |
| `SeriesMonitoringBackgroundService` | `SeriesMonitoringProcessor` | Owns due monitored-series catalog sync metadata updates. | Must not alter download/import/scan state. | Daily cadence with cancellation-safe cycles. | Sync operations should upsert/cache provider state rather than create duplicate monitored entries. | `ISeriesMonitoringService` performs provider sync. |
| `MetadataRescanService` | `MetadataRescanProcessor` | Owns background metadata enrichment for files missing metadata and cleanup of non-audio file records discovered during rescan. | Must not claim file ownership from scan/import services. | Five-minute cadence; per-file failures are logged and isolated. | Updates missing or stale metadata only; repeated cycles skip files once metadata is present. | Metadata extraction service supplies file metadata. |
| `ImageCacheCleanupService` | `ImageCacheCleanupProcessor` | Owns image temp-cache expiration cleanup. | Must not mutate audiobook/download metadata. | Daily cleanup cadence after the first midnight delay. | Missing or already deleted files are treated as successful cleanup by the cache service. | `IImageCacheService.ClearTempCacheAsync` performs storage cleanup. |
| `FfmpegInstallBackgroundService` | `FfmpegInstallProcessor` | Owns non-blocking ffprobe/ffmpeg availability checks and install attempts. | Must not block host startup or mutate unrelated settings. | Runs once outside request startup; failures are reported without stopping the host. | Rechecks installed binaries before downloading/installing. | `DownloadHub` receives `FfmpegInstallStatus`. |
| `UnmatchedScanBackgroundService` | `UnmatchedScanProcessor` | Owns Library Import unmatched-file scan job status `Queued -> Processing -> Completed/Failed` and cached result replacement. | Must not import matched files or create audiobook records. | Queue-driven; failed/finished jobs can be superseded by a new explicit scan. | Groups files deterministically and clears stale unmatched results for the scanned root. | `SettingsHub` receives `UnmatchedScanComplete`. |

The main download handoff is:

1. `DownloadMonitorService` observes the external client and persists `Completed`.
2. `DownloadProcessingJobService` creates or returns the single active/recent import job for that download.
3. `DownloadProcessingJobProcessor` performs the import, marks the download `Moved`, marks the client item imported, and enqueues a scan for the audiobook library path.
4. `ScanBackgroundService` reconciles the library files.
5. `MovedDownloadCleanupService` drives `MovedDownloadCleanupProcessor`, which performs deferred client cleanup for `Moved` downloads according to the client removal policy.

These guarantees are tested in three layers:

- `HostedServicesRegistrationTests` keeps every hosted worker registered once, requires every worker and processor to appear in this document, and verifies processor interfaces resolve through DI.
- Processor tests such as `WorkerProcessorBoundaryTests`, `ScanJobProcessorTests`, `MoveJobProcessorTests`, and `DownloadProcessingJobProcessorTests` exercise durable state ownership, idempotent replay behavior, cancellation behavior, and handoffs without running long-lived hosted loops.
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
