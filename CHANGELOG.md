# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.53] - 2026-02-27

### Fixed
- **Security banner state when auth is enabled:** Fixed a frontend state bug where unauthenticated `401` responses from `GET /api/configuration/startupconfig` were interpreted as `auth disabled`, causing the no-auth security banner to remain visible even when authentication was enabled.
- **`GET /api/library` wanted-flag path evaluation:** Hardened wanted-flag file checks to safely handle invalid/problematic file paths without throwing, preventing endpoint-level `500` responses in production data edge cases.
- **`GET /api/library` legacy ISBN materialization crash:** Fixed a production-only crash path where legacy non-array/invalid JSON values in `Audiobooks.Isbn` could fail EF materialization (`Invalid token type`) and return `500`.

### Changed
- **Audiobook EF mapping resiliency:** Updated `Audiobook.Isbn` persistence mapping to use the resilient JSON value converter/comparer pattern already used by other JSON-backed list fields (`Authors`, `Genres`, `Tags`, `Narrators`, `AuthorAsins`).

### Added
- **Regression coverage for library resilience:** Added API integration coverage that simulates legacy ISBN text data and verifies `GET /api/library` remains successful.

## [0.2.52] - 2026-02-26

### Changed
- **Authentication-disabled UX & deployment guidance:** Listenarr now emits a clear startup warning in the backend logs and shows a persistent in-app banner when authentication is disabled, reinforcing that no-auth mode is intended for trusted LAN/VPN use and not direct internet exposure.
- **Secret handling in API responses:** Centralized API response redaction for sensitive configuration/indexer payloads (startup config, application settings, API configs, download clients, and indexers) so remote unauthenticated callers receive masked values instead of raw secrets.
- **Audiobook identifier model:** Introduced a canonical typed external identifier system for audiobooks (`ASIN`, `ISBN`, `OLID`) with legacy field compatibility (dual-read/dual-write behavior for existing `Asin`, `Isbn`, and `OpenLibraryId` fields during migration).
- **Image loading pipeline (frontend):** Unified AudiobooksView image loading onto the protected image/blob pipeline so authenticated deployments no longer rely on direct `<img src="/api/images/...">` requests that cannot send auth headers.
- **Audiobook detail & library view architecture:** Streamlined AudiobooksView/AudiobookDetailView behavior with safer shared image handling, consolidated selection logic, memoized status calculation, improved tab/hash/query sync, canonical detail-endpoint loading, and shared desktop/mobile action configuration.
- **Metadata refresh behavior:** Metadata refresh is now an explicit identifier-driven “rescan metadata” workflow that performs patch-style updates (non-empty provider values overwrite existing values, blanks do not erase data).
- **Description normalization:** Metadata descriptions are stripped/normalized from HTML while preserving readable text content for display and storage.
- **Edit audiobook UX:** The Edit Audiobook modal now opens in the large size layout to better accommodate metadata and identifier editing.

### Fixed
- **Reported security issues:** Verified/fixed the previously reported issues where anonymous callers could retrieve startup API key material and create arbitrary admin users via `POST /api/account/register` (`isAdmin=true` abuse path).
- **Startup config secret exposure:** Startup config responses now redact secrets (including SSL certificate password) for remote unauthenticated callers, and startup config save responses no longer echo raw secrets to untrusted callers.
- **Identifier provenance spoofing:** `PUT /api/library/{id}/identifiers` now forces user-submitted identifiers to `Manual` source unless the row is an unchanged existing server-owned identifier (`Imported`/`Provider`), preventing provenance spoofing.
- **Duplicate identifiers in UI:** Fixed duplicate identifier rows in the edit modal caused by legacy imported identifiers overlapping with canonical manual/provider identifiers (same normalized value now deduped in effective responses and cleanup-on-save flow).
- **Metadata rescan leakage:** `rescan-metadata` failure responses now return a generic error body to callers instead of exposing attempted ASIN/ISBN lists (attempted IDs are retained only in debug logs).
- **Metadata rescan abuse controls:** Added cooldown/rate limiting per audiobook + actor (IP/user) and caps on provider attempts per rescan to reduce abuse potential in no-auth deployments.
- **Logging leaks:** Removed raw header dumps from session-auth logging, and replaced token/API key prefix logging with hashed fingerprints in auth middleware and logout logging.
- **SSRF hardening gaps (outbound tests/webhooks):** Added DNS/private-IP/final-URI validation across notification sends and high-risk indexer outbound test/import paths; public callers can no longer use these routes to target localhost/private-network hosts.
- **Debug/process endpoint exposure:** Restricted debug/diagnostic/process-control endpoints (library debug, FFmpeg, Discord bot control/diagnostics, diagnostics notification test, Prowlarr debug routes) to localhost/private-network callers or authenticated admin/API-key users.
- **Image cache SSRF protections:** Hardened image downloading with DNS/private-IP checks and redirect validation to reduce SSRF risk in image caching/fetch flows.
- **Audiobook cover recovery on cache miss:** Fixed `/api/images/{identifier}` fallback cases that returned empty/placeholder responses when cache files were missing but metadata providers could still supply a valid image.
- **Audimeta fallback bug:** Fixed a fallback chain bug where Audimeta `Description` values were incorrectly treated as image URLs, blocking Audnexus/OpenLibrary image fallback.
- **ASIN/author/ISBN fallback routing:** Tightened ISBN detection so author names/ASIN-like values are no longer misrouted into OpenLibrary ISBN lookups.
- **Cache alias reuse for changed primary ASINs:** When a primary ASIN changes, `/api/images/{newAsin}` can now reuse a cached image stored under an alternate identifier instead of falling back to placeholder.
- **Author image behavior:** Author cards no longer fall back to audiobook cover art; they now correctly show the placeholder when no author-specific image exists.
- **Auth-required image loading in AudiobooksView:** Fixed 401 image failures caused by direct `<img>` requests in authenticated mode; images now load via authenticated fetch + blob URLs.
- **Missing cover recovery (provider fallback):** When no local image exists and no cached file is present, Listenarr now properly reaches out to providers (Audimeta/Audnexus/OpenLibrary), caches the image, and returns it instead of a zero-size/placeholder response when recoverable.
- **Genres after metadata refresh:** Fixed audiobook detail responses so refreshed metadata fields (including genres and other rescanned fields) are returned by the detail endpoint and visible after metadata rescan.
- **Runtime formatting in AudiobookDetailView:** Fixed audiobook runtime display to treat stored runtime values as minutes (e.g., `1472` now renders as `24h 32m` instead of `0h 24m`).
- **AudiobookDetail/AudiobooksView navigation mismatch:** Fixed status-click navigation and tab resolution issues between AudiobooksView and AudiobookDetailView (`downloads` mismatch vs supported detail tabs).
- **Frontend test stability and warnings:** Fixed failing frontend tests (`AddNewView.spec.ts`, `AudiobooksView`, `AudiobookDetailView`, related suites) and cleaned up Vue test warnings introduced during refactors.
- **API test determinism:** Stabilized test auth defaults in the API test factory so endpoint tests don’t inherit local auth-enabled config unexpectedly.

### Added
- **Typed audiobook external identifiers:** Added `AudiobookExternalIdentifier` entity/model/table and migration-backed persistence for multiple identifiers per audiobook (ASIN/ISBN/OLID) with normalization, primary marker support, source tracking (`manual/provider/imported`), and optional region support for ASINs.
- **Identifier migration & backfill:** Added an EF Core migration to create the external identifiers table and backfill legacy ASIN/ISBN/OpenLibrary values into the new structure at startup migration time.
- **Identifier management API:** Added `GET /api/library/{id}/identifiers` and `PUT /api/library/{id}/identifiers` to view/edit associated identifiers with validation, dedupe, and legacy-field synchronization.
- **Identifier editing UI:** Added identifier editing in the Edit Audiobook modal (add/remove ASIN/ISBN/OLID, mark primary identifier, show source badges) and a full associated identifier list with primary indicator on the audiobook detail page.
- **Metadata rescan endpoint and UI action:** Added `POST /api/library/{id}/rescan-metadata` plus a new “Rescan Metadata” action in AudiobookDetailView so users can repair metadata after adding/correcting identifiers.
- **Metadata rescan image repair:** Metadata rescans now also attempt to cache/update the audiobook image when providers return a cover image URL.
- **Cover recovery fallback expansion:** Added additional image fallback paths for cache-miss covers using local library identifiers (ISBN/OLID), alternate stored identifiers, and OpenLibrary title+author ISBN discovery when provider ASIN lookups fail.
- **Security utility infrastructure:** Added shared security helpers for request trust evaluation, secret hashing, endpoint access gating, outbound request validation (URL/DNS/redirect/final URI checks), and reusable API response redaction.
- **Regression coverage:** Added/updated tests covering image fallback chains, identifier deduplication/provenance handling, metadata rescan behavior/rate limits, and security redaction/hardening paths.

### Removed
- **Verbose sensitive logging:** Removed raw request-header dumps from session authentication logging on missing-token startupconfig requests.
- **Public error detail leakage:** Removed detailed attempted identifier lists and attempt metadata from public `rescan-metadata` failure payloads (kept only in debug logs).
- **Author image fallback to book covers:** Removed audiobook-cover fallback behavior for author cards so missing author images consistently use the placeholder image.
- **Legacy duplicate identifier presentation:** Removed duplicate imported/manual identifier rows from effective identifier responses when a canonical identifier already exists for the same normalized value.
- **api/account/register:** Removed because the app currently creates/updates admin credentials through SaveApplicationSettingsAsync() via Settings, but a user could start with "AuthenticationRequired": "true" in the config.json and no users exist and be locked out, but this is not a valid usecase.

## [0.2.51] - 2026-02-23

### Fixed
- **UI (Remote Path Mapping):** Fixed Remote Path Mapping modal Save action by ensuring the shared `ModalForm` includes `id="modal-form"` so footer Save buttons using `form="modal-form"` correctly submit the form.


## [0.2.50] - 2026-02-22

### Changed
- **Persistence & EF Core:** Pinned EF Core to 9.0.0 in central package management and refactored persistence registration to avoid resolving scoped EF option-configurators from the root provider. Registered a singleton `DbContextOptions<ListenArrDbContext>` and an `IDbContextFactory<ListenArrDbContext>` (Simple factory) so contexts are created safely at scoped time.
- **Startup migrations:** Startup now applies EF migrations via the `IDbContextFactory` (migration errors are logged and do not prevent startup in development). Added a development-friendly fallback for safe startup when migrations cannot be applied.
- **Migrations:** Added an AutoSync migration `20260222154541_SyncModelToCurrent` (no-op `Up` with a preserved `.Designer.cs` model snapshot) to keep tooling/model metadata in sync; `dotnet ef database update` reported the database as already up to date.
- **Design-time tooling:** Added `ListenArrDesignTimeDbContextFactory` to improve EF tooling support.
- **Bugfix (DI):** Fixed runtime failures caused by resolving scoped EF configurators from the root provider (controllers and startup no longer throw when activating DbContexts).
- **Frontend polish:** `fe/src/App.vue` — brand/logo now links to `/`, hover background bleed fixed, headphone animation triggers on brand hover, and a mobile sidebar backdrop was added. `fe/src/views/settings/IndexersTab.vue` — Prowlarr modal inputs updated to use shared `.form-input` styles for consistent visuals.
- **Build/dev:** Verified solution build succeeded locally and frontend dev server (Vite) ran for visual validation of the UI changes.
 - **Notifications UI:** Redesigned the Notifications modal to accept service-specific credentials (Telegram Bot Token + Chat ID, Pushover API Token + User Key, Pushbullet Access Token), hide the generic webhook URL for token-only services, and ensure trigger badges render in a consistent order.

### Added
- **Notifications / NTFY:** Implemented NTFY publish compatibility (plain text POST body plus `Title`, `Tags`, and `Priority` headers) and added a diagnostics endpoint `POST /api/diagnostics/test-notification` to send test notifications. Frontend Test buttons now call the diagnostics endpoint for live testing.

### Fixed
- **Notifications UI & Webhooks:** Fixed the notification card Test button so it triggers a real test call; added webhook trigger selection in the Notifications settings and aligned `CheckboxCard` layout for consistent visuals.
 - **Notifications implementations:** Standardized and fixed all notification integrations and payloads (NTFY, Telegram, Pushover, Pushbullet, Slack, and generic/Zapier). Highlights:
   - NTFY: sends plain-text body with `Title`, `Priority`, and `Tags` headers.
   - Telegram: sends JSON to `sendMessage` with `chat_id`, `text`, `disable_notification`, and `parse_mode`.
   - Pushover: posts `application/x-www-form-urlencoded` to `/1/messages.json` with `token`, `user`, `message`, and `title`.
   - Pushbullet: posts JSON to `/v2/pushes` using `Authorization: Bearer <access_token>` and `type=note` payloads.
   - Slack: posts `{"text":"..."}` to Incoming Webhooks URLs.
   - Zapier/Generic: posts the full rich JSON payload produced by the payload builder to the exact configured webhook URL.
   Temporary redacted request/response logging was added to aid diagnostics during verification.

### Security
- **DevDependency removal:** Removed `source-map-explorer` from devDependencies to address a transitive `ejs` vulnerability and updated lockfile(s).

## [0.2.49] - 2026-02-21

### Fixed
- **Settings save CSRF failure**: ensured antiforgery token is refreshed and bound to authenticated user. Added `tokenReadyPromise` in `ApiService` and blocked unsafe requests until token is available. Removed manual CSRFFetch from `saveApplicationSettings`.
- **Startup config persistence**: previously, `AuthenticationRequired` was preserved from `config.json` and ignored when the frontend saved.  Toggle in General Settings now updates the flag and writes it to the file; authentication behaves the same as the other startup options.
- **Token export**: properly export `ensureImageCached` and cleaned stray code from `api.ts` that caused build errors.
- **Startup cache logging**: added missing `logger` import and removed unsafe `console` usage.

### Changed
- **Logging cleanup**: converted remaining `console.log` calls in `ApiService` to `logger.debug` and tidied comments.


## [0.2.48] - 2026-01-14

### Added
- **Prowlarr compatibility improvements**: `POST /api/v1/indexers`, `POST /api/v1/indexer` and `PUT /api/v1/indexer/{id}` now accept varied payload shapes (nested `settings`, `fields` arrays and multiple property name variants) and return standard DTOs with non-null `fields` and `tags` for better interoperability.
- **Toast suppression**: Global message-level and per-indexer toast suppression to reduce notification noise during rapid indexer imports (default suppression window: 5 seconds).
- **Settings — loading UI**: Added visible loading indicators and a `LoadingState` placeholder to Settings tab components (`QualityProfilesTab`, `NotificationsTab`, `RootFoldersSettings`, `IndexersTab`, `DownloadClientsTab`). Inline header spinners and unit tests were added to improve perceived responsiveness during async loads.


### Changed
- **`ProwlarrCompatController` behavior**:
  - `PUT /api/v1/indexer/{id}` implements upsert semantics (creates when missing) and **deduplicates** by normalized URL + API key. Deduplication runs client-side (pulls results with `AsNoTracking().ToList()` then normalizes) to avoid EF translation issues.
  - Removed early create-time broadcast in `PUT` and compute `created` after dedupe so `IndexersUpdated` is broadcast once (prevents duplicate broadcasts/toasts).
  - `DELETE /api/v1/indexer/{id}` tolerates `id == 0` from external clients and returns an empty JSON object with a warning log to avoid noisy caller errors.
- **General Settings — API Key control**: Improved the API key input in the General Settings tab—input is full width with an inline visibility toggle and the regenerate/copy buttons placed inside the input (order: visibility, regenerate, copy). The regenerate button uses a red hue to indicate the key will be invalidated, and the copy button uses a blue hue. Functionality is unchanged and unit tests pass locally.
- **PasswordInput component**: Added a named `append` slot to `PasswordInput.vue` so callers can inject inline controls (e.g., copy/regenerate buttons) without relying on deep CSS overrides. `ApiKeyControl` now uses the slot, improving layout robustness and accessibility. Unit tests updated and pass locally.
- **Frontend — route prefetch**: Added route prefetch in `main.ts` to improve perceived navigation performance.
- **Images / Author ASIN**: Prefer stored author ASIN for author image lookup and probe the DB when a cached image lookup returns NotFound; this reduces unnecessary Audnexus calls and improves cache hit rates.
- **qBittorrent adapter**: Prefer `IHttpClientFactory` with a cookie-client fallback, use injected `HttpClient` for auth requests, and clarified auth failure messages; added `QBittorrentHelpers` and robustness improvements in the adapter.
- **Dependencies**: Bumped frontend/backend dependencies and regenerated lockfiles.

### Fixed
- **qBittorrent Test**: `qBittorrent` client test now attempts authentication when the unauthenticated `/api/v2/app/version` values.
- **Duplicate notifications & race**: Added `NotificationSuppressionSeconds`, `_lastToastTimes`, `_lastToastMessages`, and helper methods `ShouldSendToastForIndexer`/`ShouldSendToastForMessage`. Fixed an edge-case race where the per-indexer check previously updated the global message timestamp causing unintended self-suppression.
- **EF translation error**: Moved normalization/dedupe to in-memory evaluation to avoid EF Core InvalidOperationException when calling `NormalizeIndexerUrl` inside an EF expression.
- **Download client test behavior**: The Test button on the **Download Client** modal uses the current (unsaved) form input values; the Test button on the download client card in the Settings tab tests the saved DB configuration values.
- **Images / Author ASIN tests**: Mocked `IAudiobookRepository.GetAuthorAsinByNameAsync` in ImagesController tests so the stored‑ASIN path is exercised; tests updated accordingly.
- **Frontend — Loading UI & tests**: Added VTU test stubs for `LoadingState` and `PhSpinner` and unit tests covering loading indicators in settings tabs to prevent component-resolution warnings in tests.
- **Tests**: Added and updated unit tests in `tests/Listenarr.Api.Tests` (e.g., `ProwlarrCompatControllerTests`, `ProwlarrEndpointsTests`) to validate broadcasting, idempotent PUT upsert, delete `id==0` tolerance, and toast/message-level dedupe. All API tests pass locally (253 tests).

### Removed
- Removed duplicate/early Broadcast/toast on the create path in the `PUT` flow to avoid double notifications.


## [0.2.47] - 2026-01-13

### Added
- **Prowlarr → Notifications**: When indexers are imported via the Prowlarr-compatible API, the server now broadcasts `IndexersUpdated` and also publishes a toast and persistent `Notification` so the activity bell dropdown shows the import (includes created indexer names).
- **Settings hub auto-connect**: The frontend now automatically establishes a dedicated Settings hub connection (when the downloads hub's downloads connection is established) so the SPA reliably receives settings and indexer broadcasts.
- **Debugging**: Added debug endpoints `GET /api/v1/debug/settings/clients` and `POST /api/v1/debug/indexers/publish` to help verify hub connectivity.

### Fixed
- **SignalR**: Fixed missing SettingsHub client connections and ensured notifications are published when indexers are created.
- **Add New / Search runtime formatting**: Fixed a miscalculation where search result runtimes (provided in seconds) were being treated as minutes in the Add New and Search views. Runtimes are now converted from seconds to minutes and formatted correctly (e.g., "10h 20m"); added a unit test verifying the formatted display.
- **SettingsView / GeneralSettingsTab reactivity**: Fixed a recursive update/prop sync issue in `GeneralSettingsTab` by adding a syncing guard and a focused watcher for `useUsProxy` so in-place parent updates propagate reliably without creating a watch loop. This resolves the failing SettingsView unit tests.
- **Tests**: Updated/added frontend unit tests to cover the runtime formatting and reactivity fixes; all frontend unit tests pass locally.

## [0.2.46] - 2026-01-07

### Added
- **Download finalization**: Added `ExtractArchives` application setting and an EF Core migration to persist it (migration: `20251231003000_AddExtractArchivesToApplicationSettings`). This enables automatic archive extraction on completed downloads when enabled.
- **Wanted view download indicators**: Visual feedback for active downloads in wanted view with download icon, status badge, and pulse/bounce animations using CSS keyframes
- **Legacy root folder migration**: On startup, a legacy single `ApplicationSettings.outputPath` will be migrated into the new `RootFolder` table as a named root called `Default` with `IsDefault = true` (only when no root folders already exist).
- **Download client test endpoint**: Implemented test connection functionality for download clients in settings modal with real API integration and proper error handling.
- **Root folder management**: Complete root folder system with named folders, selection when adding/editing audiobooks, move/rename confirmation dialogs, and comprehensive E2E and unit tests.
- **Bulk update endpoint**: Batch update API endpoint for audiobooks with frontend integration for efficient mass updates.
- **Notification system**: Toast messages now also appear as persistent notifications via SignalR, with support for import and deletion broadcasts.
- **Quality profile minimum score threshold**: Added MinimumScore property to quality profiles to reject releases below specified threshold (migration: `20260103235802_AddMinimumScoreToQualityProfile`).
- **Import item resolution service**: Implemented GetImportItemAsync pattern across all download client adapters for accurate post-download path resolution.
- **Lazy image loading**: Native browser loading="lazy" for all images with placeholder support, replacing custom lazy loading logic.
- **Advanced search and collection features**: New AdvancedSearchModal with ASIN, author, title, and series search prefixes for precise queries
- **Collection view**: Comprehensive CollectionView for managing audiobook collections with author and series grouping
- **Author ASIN support**: Backend author ASIN resolution via Audimeta with author image caching and database persistence (migration: `20251225220155_AddAuthorAsins`)
- **Sub-navigation for audiobooks**: Sidebar navigation for grouping audiobooks by books, authors, or series with route sync
- **Series display enhancements**: Full series lists as badges with tooltips, normalized series data from various sources
- **Smart score UI and sorting**: Prowlarr-style composite scoring with normalized score display, sorting by Grabs and Language
- **Inspect torrent modal**: View and download cached torrent files and announce URLs for downloads with diagnostics support
- **MyAnonamouse enhancements**: Advanced search options including filters, language, and enrichment toggles
- **Search cancellation**: Cancel ongoing searches with abort signal support throughout backend async operations
- **Image fallback system**: Consistent image fallback mechanism with placeholder handling and failed image caching
- **Download client toggle**: Enable/disable download clients directly from settings view

### Changed
- **Production-ready logging standardization**: Comprehensive console logging cleanup and standardization across the entire application
  - Removed 10 debug `console.log` statements from production code
  - Migrated 82 `console.error` calls to professional `errorTracking` service across 18 files (views, stores, services)
  - Migrated 16 `console.warn` calls to `logger.warn` service across 10 files
  - All application code now uses centralized logging services (`logger` and `errorTracking`)
  - Infrastructure code (auth, errorTracking, SignalR) appropriately retains console for low-level diagnostics
  - SignalR logs now gated behind DEV mode checks
  - Clean production console output with structured error tracking ready for external monitoring integration (Sentry, LogRocket)
- **SearchService architecture refactoring**: Implemented provider pattern for improved maintainability and extensibility
  - Created `IIndexerSearchProvider` interface with dynamic provider selection
  - Extracted 3 indexer-specific providers: `TorznabNewznabSearchProvider`, `MyAnonamouseSearchProvider`, `InternetArchiveSearchProvider`
  - Separated indexer-specific logic into focused, testable provider classes
  - Follows proven adapter pattern already used in download client implementations
  - Easier to add new indexer types by implementing the provider interface
- **SettingsView component extraction**: Fully componentized settings view for improved maintainability
  - Extracted 7 focused tab components reducing SettingsView from 4,722 to 3,540 lines (-25% reduction)
  - `ApiSettingsTab` (682 lines), `DownloadClientSettingsTab` (840 lines), `QualityProfilesTab` (634 lines)
  - `NotificationSettingsTab` (1,082 lines), `ImportSettingsTab` (363 lines), `UiSettingsTab` (480 lines), `GeneralSettingsTab` (897 lines)
  - All tabs use composition API with proper props/emits pattern
  - Improved code organization and component reusability
- **Search/Metadata API refactor**: Added `api/metadata` controller and deprecated `api/search/metadata` + `api/search/audimeta` (any external consumers should migrate)
- **Search pipeline refactoring**: Modular ASIN enrichment (AsinEnricher), fallback scraping (FallbackScraper), direct ASIN search (AsinSearchHandler), and result scoring (SearchResultScorer)
- **Download monitoring optimization**: Reduced qBittorrent API calls by consolidating torrent info requests, added per-client poll scheduling for Transmission, SABnzbd, and NZBGet to avoid overload.
- **qBittorrent polling optimization**: Per-client polling intervals, batch requests, memory caching for torrent properties, field limiting with category/hash filtering
- **Download cleanup**: Added 'remove completed downloads' option for download clients (migration: `20260103175654_AddRemoveCompletedDownloadsToClients`), always stores absolute file paths for imports, enhanced download queue removal and orphaned download handling.
- **Image handling improvements**: Local image paths preserved when moving audiobooks, normalized all image URLs to use API endpoint, placeholder images served when covers missing, simplified error handling, consistent placeholder usage across views.
- **Automatic search improvements**: AutomaticSearchService now skips searches if quality cutoff is already met.
- **ActivityView debug tools**: Added debug tools and comprehensive tests for download activity monitoring
- **MyAnonamouse result enrichment**: Richer result data with seeders, leechers, grabs, files, language, and quality fields
- **Frontend build optimization**: Patched @microsoft/signalr to remove Rollup warnings, included only available font formats
- **Download status filtering**: Active downloads in frontend now exclude terminal states ('Moved', 'Completed', 'Failed', 'Cancelled') for cleaner UI state management
- **Performance optimization**: Added v-memo directive to WantedView audiobook cards with proper reactive dependencies to optimize large list rendering

### Fixed
- **Transmission download import**: Fixed authentication issues preventing automatic import by implementing proper 409/session-id retry pattern in PollTransmissionAsync to match TransmissionAdapter CSRF protection
- **Download queue processing**: Fixed stuck jobs blocking all imports by implementing ResetStuckJobsAsync() to reset "Processing" state jobs on DownloadProcessingBackgroundService startup
- **Download status lifecycle**: Ensured Status = DownloadStatus.Moved is set after successful import in all 8 code paths within CompletedDownloadProcessor for consistent terminal state handling
- **Import notifications and history**: Moved history entry and notification creation to execute BEFORE cleanup operations to ensure they're created for all successful imports, not just when downloads remain in client
- **Transmission cleanup**: Fixed torrent removal after import by extracting torrent hash using torrentInfo.HashString instead of download.ExternalId for proper cleanup
- **Wanted status accuracy**: Fixed wanted view showing incorrect status when files deleted by adding physical file existence checks (File.Exists) in 3 locations: LibraryController.GetAllAudiobooks, LibraryController.GetAudiobook, and ScanBackgroundService.BroadcastLibraryUpdate
- **TypeScript compilation**: Removed non-existent contentPath property reference from downloads store that was causing TS2339 errors
- **MyAnonamouse authentication & downloads**: Persist `mam_id` values received from tracker responses and explicitly include `mam_id` cookie on direct torrent downloads when the torrent host differs from the configured indexer; adds unit tests covering cookie persistence and download caching.
- **Null checks**: Added missing null checks for audiobook properties in EditAudiobookModal and simplified author assignment logic in SearchService to handle null values consistently.
- **Import directory creation**: Destination directories now created automatically if missing instead of skipping import
- **Cache stampede prevention**: Added AsyncKeyedLock to prevent concurrent cache operations
- **Result table UI**: Row hover now underlines title for better visual feedback
- **Font loading**: Removed unavailable WOFF font format, optimized font loading
- **Null handling**: Improved null handling across services and test setup
- **Logging and error handling**: Enhanced logging for download updates and auth operations with detailed debugging

### Security
- **MyAnonamouse cookie handling**: Persist `mam_id` cookies from tracker responses, include on direct torrent downloads with proper caching and validation

### Technical Debt
- **Error tracking infrastructure**: Comprehensive `ErrorTrackingService` implemented with structured error context, ready for external service integration
- **Documentation**: Updated release readiness review, TODO tracking, console logging audit, and refactoring plans to reflect completed work
- **Test coverage**: Added E2E tests for move flow and root folders, unit tests for move queue, import resolution, quality profile scoring, bulk update operations, search sorting, scoring, and MyAnonamouse parsing
- **Code quality**: Replaced `Assert.True(...Any)` with `Assert.Contains` to satisfy xUnit analyzers
- **Search result types**: Enhanced type definitions with optional fields and richer metadata support

### Added
- **Advanced search and collection features**: New AdvancedSearchModal with ASIN, author, title, and series search prefixes for precise queries
- **Collection view**: Comprehensive CollectionView for managing audiobook collections with author and series grouping
- **Author ASIN support**: Backend author ASIN resolution via Audimeta with author image caching and database persistence (migration: `20251225220155_AddAuthorAsins`)
- **Sub-navigation for audiobooks**: Sidebar navigation for grouping audiobooks by books, authors, or series with route sync
- **Series display enhancements**: Full series lists as badges with tooltips, normalized series data from various sources
- **Smart score UI and sorting**: Prowlarr-style composite scoring with normalized score display, sorting by Grabs and Language
- **Inspect torrent modal**: View and download cached torrent files and announce URLs for downloads with diagnostics support
- **MyAnonamouse enhancements**: Advanced search options including filters, language, and enrichment toggles
- **Search cancellation**: Cancel ongoing searches with abort signal support throughout backend async operations
- **Image fallback system**: Consistent image fallback mechanism with placeholder handling and failed image caching
- **Download client toggle**: Enable/disable download clients directly from settings view

### Changed
- **Search/Metadata API refactor**: Added `api/metadata` controller and deprecated `api/search/metadata` + `api/search/audimeta` (any external consumers should migrate)
- **Search pipeline refactoring**: Modular ASIN enrichment (AsinEnricher), fallback scraping (FallbackScraper), direct ASIN search (AsinSearchHandler), and result scoring (SearchResultScorer)
- **qBittorrent polling optimization**: Per-client polling intervals, batch requests, memory caching for torrent properties, field limiting with category/hash filtering
- **Download monitoring improvements**: Enhanced completed and failed download handling with deduplication between queue and DB failures
- **ActivityView debug tools**: Added debug tools and comprehensive tests for download activity monitoring
- **Image handling**: Normalized all image URLs to use API endpoint, placeholder images served when covers missing
- **MyAnonamouse result enrichment**: Richer result data with seeders, leechers, grabs, files, language, and quality fields
- **Frontend build optimization**: Patched @microsoft/signalr to remove Rollup warnings, included only available font formats

### Fixed
- **Import directory creation**: Destination directories now created automatically if missing instead of skipping import
- **Cache stampede prevention**: Added AsyncKeyedLock to prevent concurrent cache operations
- **Result table UI**: Row hover now underlines title for better visual feedback
- **Font loading**: Removed unavailable WOFF font format, optimized font loading
- **Null handling**: Improved null handling across services and test setup
- **Logging and error handling**: Enhanced logging for download updates and auth operations with detailed debugging

### Security
- **MyAnonamouse cookie handling**: Persist `mam_id` cookies from tracker responses, include on direct torrent downloads with proper caching and validation

### Documentation
- **AI agent instructions**: Comprehensive update to all AI assistant instruction files in .github folder
  - Enhanced copilot-instructions.md with critical backend/frontend architecture patterns, troubleshooting scenarios, and security considerations
  - Updated .cursorrules with critical patterns sections for both backend and frontend
  - Restructured RULES.md as comprehensive navigation guide with file organization and quick reference
  - Updated all provider-specific files (ANTHROPIC.md, OpenAI.md, AZURE_OPENAI.md, BARD.md, COHERE.md, BEDROCK.md, HUGGINGFACE.md) with Listenarr-specific guidance
  - Enhanced tool-specific files (clinerules, windsurfrules, WARP.md) with project overview and critical patterns
  - Updated CONVENTIONS.md with references to primary documentation files
  - All files now include download lifecycle, file validation, authentication patterns, job processing, and common troubleshooting scenarios

### Technical Debt
- **Test coverage expansion**: Added comprehensive tests for search sorting, scoring, MyAnonamouse parsing, quality profiles, and import service
- **Code quality**: Replaced `Assert.True(...Any)` with `Assert.Contains` to satisfy xUnit analyzers
- **Search result types**: Enhanced type definitions with optional fields and richer metadata support

## [0.2.45] - 2025-12-10

### Changed
- **Manual Import Modal UX Improvements**: Significantly improved the manual import workflow for better usability
  - Directory picker now automatically populates the input field when clicking folders (no need for green checkmark)
  - Interactive Import and Automatic Import buttons moved to modal footer when directory picker is open
  - Buttons are disabled until a valid directory is selected
  - Centered action buttons appear when a valid path exists and browser is closed
  - Recent folder selection now triggers automatic path validation with visual feedback
- **File System Browser Enhancements**: Backend now returns both files and directories for comprehensive browsing
  - Files and directories sorted with directories first, then alphabetically
  - Files displayed with appropriate styling (gray icon, non-interactive)
  - FolderBrowser component now supports optional `showFiles` prop (default: false)
  - Manual Import Modal enables file display for better context when selecting import folders
- **Interactive Import Table Improvements**: Action column now shows informative icon with tooltip
  - Replaced non-functional warning button with info icon showing validation issues
  - Tooltip displays missing required fields (audiobook, quality profile, language)
  - Info icon only appears when validation issues exist
  - Added `rejections` field to preview items for backend validation feedback
- **Search/Metadata API refactor**: Added `api/metadata` controller and deprecated `api/search/metadata` + `api/search/audimeta` (Discord bot and any external consumers should migrate)

### Fixed
- **FolderBrowser Validation**: External path changes (like selecting recent folders) now automatically trigger validation
- **Manual Import Footer Logic**: Import mode dropdown now only shows during preview mode when relevant

## [0.2.44] - 2025-12-10

### Fixed
- Updated test files to use correct type assertions for wrapper.vm and simplified timeout options. Changed autoCloseTimer type in NotificationModal.vue to use ReturnType<typeof setTimeout> for better type safety.

## [0.2.43] - 2025-12-10

### Fixed
- **Discord Bot JsonDocument Disposal Issue**: Fixed `ObjectDisposedException` when starting Discord bot by returning original diagnostics object instead of disposed `JsonElement`
- **Tools Directory in Development Builds**: Fixed missing Discord bot files in development builds by properly configuring tools directory to copy to build output while avoiding publish conflicts
- **Single-File Publish Compatibility**: Replaced `Assembly.Location` with `AppContext.BaseDirectory` for Playwright script path resolution to support single-file published applications

### Changed
- **Build Configuration**: Updated `.csproj` to properly handle tools directory for both development and production scenarios
  - Tools now copy to `bin/Debug` and `bin/Release` during development builds
  - Publish operations use custom targets to avoid file duplication conflicts

## [0.2.42] - 2025-12-10

### Changed
- **Frontend Dependencies**: Updated multiple dependencies to their latest versions for improved performance, features, and security
  - Upgraded `vue` from 3.5.22 to 3.5.24 for latest Vue.js features and bug fixes
  - Updated `@tsconfig/node22` from 22.0.2 to 22.0.5 for improved TypeScript Node.js configuration
  - Upgraded `eslint-plugin-vue` from 10.4.0 to 10.6.0 with new linting rules and Vue 3 support enhancements
  - Updated `vite-plugin-vue-devtools` from 8.0.2 to 8.0.5 for better development experience
  - Major upgrade: `vitest` from 3.2.4 to 4.0.13 including all related @vitest/* packages for improved testing capabilities
  - Updated `chai` to 6.2.1 and `tinyrainbow` to 3.0.3 for testing library compatibility
  - Updated `postcss-selector-parser` to 7.1.0 for improved CSS selector parsing

### Removed
- **Dependency Cleanup**: Removed deprecated and unused packages from frontend lock file to reduce bloat and potential security risks
  - Removed `cac`, `check-error`, `deep-eql`, `loupe`, `pathval`, `strip-literal`, `tinypool`, `tinyspy`, and `vite-node` (internalized by Vitest v4)

## [0.2.41] - 2025-12-09

### Fixed
- **Download Client Timeouts**: Added 30-second timeout to all download client HTTP requests (Transmission, qBittorrent, SABnzbd, NZBGet) to prevent indefinite hangs on unresponsive clients
- **Transmission RPC Compatibility**: Fixed Transmission v4.0.6+ compatibility by using legacy bespoke RPC format with kebab-case method names (`torrent-add`, `torrent-get`, `session-get`) instead of JSON-RPC 2.0
- **Private Tracker Support**: Implemented proper torrent file caching and base64-encoded `metainfo` transmission for MyAnonamouse and other private trackers requiring authentication
- **Download Directory Handling**: Fixed Transmission rejecting empty `download-dir` parameter; now omits field when not specified to use Transmission's default path
- **CSRF Protection**: Proper X-Transmission-Session-Id header management for Transmission authentication with automatic retry on 409 Conflict

### Security
- **Log Injection Prevention**: Comprehensive sanitization for user-provided input in log statements to prevent log injection attacks across the entire application
  - Enhanced `LogRedaction` class with `SanitizeUrl()`, `SanitizeText()`, and `SanitizeFilePath()` methods
  - Sanitized URLs, search queries, file paths, titles, IDs, client names, and user-provided text in 122+ log statements
  - Applied to Services: AmazonSearchService, AudibleSearchService, AudibleMetadataService, DownloadService, DownloadClientGateway, NotificationService, MoveQueueService, and OpenLibraryService
  - Applied to Controllers: FfmpegController, IndexersController, LibraryController, and SearchController
  - Applied to Download Client Adapters: NzbgetAdapter, QbittorrentAdapter, TransmissionAdapter, and SabnzbdAdapter
  - Prevents log injection attacks via newlines, log forging, path traversal disclosure, and credential leakage in all log outputs
  - All user-controllable data is now sanitized before being written to logs throughout the application
  - Added CodeQL workflow configuration to exclude `cs/log-forging` query (comprehensive custom sanitization implemented)
- **Authorization Bypass Prevention**: Fixed user-controlled bypass in MyAnonamouse torrent caching with triple validation:
  - Requires valid database-backed IndexerId (no arbitrary search result processing)
  - Validates indexer implementation against database configuration instead of user-provided search results
  - Validates torrent download URLs match the configured indexer's domain to prevent SSRF attacks

### Changed
- **TransmissionAdapter**: Now prefers cached torrent file data over URLs for authenticated downloads, falling back to URLs/magnet links for public torrents
- **Improved Logging**: Added comprehensive debug logging for download client operations including request/response details
- **Program Structure Refactor**: Major architectural improvement with complete separation of concerns:
  - Split `Program.cs` into three distinct files for better maintainability:
    - `Program.cs` - Main production application entry point with standard ASP.NET Core configuration
    - `Program.Testing.cs` - Isolated testing environment setup with dedicated WebApplicationFactory support
    - `Program.TestingHook.cs` - Testing integration hooks and utilities for dependency injection testing
  - Improved code organization with clear boundaries between production and testing code paths
  - Enhanced testability through modular architecture allowing independent testing of application components
  - Better separation of concerns with testing infrastructure completely isolated from production runtime
  - Enables cleaner dependency injection testing and integration test scenarios

## [0.2.40] - 2025-11-21

### Added

- **Automatic Remote Path Mapping Assignment**: When creating a new remote path mapping, it is now automatically assigned to the selected download client if a `downloadClientId` is provided, streamlining the user workflow
- **Reactive UI Updates**: Remote path mapping assignments update local state immediately for instant UI feedback while asynchronously saving changes to the server
- **Error Recovery**: If server synchronization fails during client configuration updates, local changes are automatically reverted to maintain data consistency

### Changed

- **SettingsView.vue**: Enhanced `saveMapping` function to handle automatic client assignment with immediate local state updates and server error recovery

### Added

- **Remote Path Mapping Assignment**: Download clients can now be assigned one or more remote path mappings directly through a dropdown selector in the client configuration modal
- **Dynamic Remote Path Mapping Loading**: Remote path mappings are loaded dynamically when editing download clients for better data initialization
- **Visual Remote Path Mapping Display**: Settings view now shows which remote path mappings are assigned to each download client
- **Confirmation Modals for Deletions**: Added confirmation modals for deleting APIs, remote path mappings, and metadata sources to prevent accidental deletions
- **Enhanced Type Safety**: Introduced `DownloadClientSettings` type and extended `DownloadClientConfiguration` for better typed access to client settings including `remotePathMappingIds`

### Changed

- **API Endpoint Consistency**: Updated all remote path mapping API calls in `api.ts` to use pluralized `/remotepathmappings` endpoints for consistency with backend routes
- **Download Client Form Modal**: Replaced `RemotePathMappingsManager` component with a streamlined dropdown selector for assigning remote path mappings
- **Settings View Layout**: Improved organization of remote path mapping display and deletion flows with confirmation modals
- **Type Safety Improvements**: Replaced unsafe `window as any` type assertions with safer `window as unknown as Record<string, unknown>` throughout the codebase
- **Password Field Accessibility**: Updated password visibility toggle buttons to use correct boolean values for `aria-pressed` attribute

### Fixed

- **Remote Path Mapping Reactivity**: Fixed reactivity issues with remote path mapping assignments in download client settings
- **Indexer Delete Button Styling**: Resolved CSS issues with delete button styling in indexer configuration
- **Type Assertion Safety**: Refactored `getResultLink` function in search components to use safer type assertions and prevent runtime errors
- **Modal Delete Button Styling**: Improved styling of modal delete buttons to be more prominent and accessible, distinguishing them from icon-style list buttons



## [0.2.38] - 2025-11-20

### Added

- MyAnonamouse search improvements: added targeted audiobook searches (sets `main_cat=13`) and refined request payloads to match the indexer's `loadSearchJSONbasic.php` form shape for more reliable results.

### Changed

- SearchService: switched MyAnonamouse requests to use the site-expected form-encoded payload, include `tor[text]`, `tor[main_cat][]`, `tor[searchIn]=torrents`, and scoped `tor[srchIn][...]` flags to prefer title/author where available.
- Improved response parsing for MyAnonamouse: robust detection and prioritization of magnet links and .torrent URLs (including constructing magnet links from known hashes), and conservative NZB handling when explicit NZB fields are present.
- Query sanitization: sanitize indexer queries to remove problematic characters (curly apostrophes and parentheses) that impacted matching on MyAnonamouse.
- Logging: outbound MyAnonamouse payloads are logged at Information level to aid debugging and verification.

### Fixed

- Reduced false-positive ebook results from MyAnonamouse searches by targeting audiobooks and tightening search fields.
- Hardened parsing to populate `SearchResult` fields (Title, Torrent/Magnet/NZB URLs, Size, Seeders) for a wider range of MyAnonamouse response shapes.


## [0.2.37] - 2025-11-19

### Added

- Search Settings: new section in General Settings with toggles to enable/disable provider searches and controls to tune search behavior:
  - `enableAmazonSearch`, `enableAudibleSearch`, `enableOpenLibrarySearch` (all enabled by default)
  - `searchCandidateCap` (default: 100) — limit of unified ASIN candidates prior to metadata enrichment
  - `searchResultCap` (default: 100) — overall result cap returned to the UI
  - `searchFuzzyThreshold` (default: 0.20) — fuzzy-matching threshold used by the intelligent search

### Changed

- Backend: `ApplicationSettings` extended with search configuration fields and an EF Core migration added so these values are persisted in the database.
- Search pipeline: `SearchService` now reads application-level search settings and applies provider skip flags, candidate limits and fuzzy threshold. Unified candidate lists are trimmed prior to metadata enrichment and the combined result set is capped after merging indexer results.
- Frontend: `SettingsView` and types updated to expose the new controls. Normalization logic now prefers camelCase and preserves a single canonical payload shape when saving.

### Fixed

- Fixed settings persistence and save behavior: removed duplicated/Conflicting PascalCase keys in the frontend payload and corrected Pinia ref handling so settings save/load remain reactive and consistent.
- Fixed an issue where previously-applied migrations left existing databases with zero/default values for the new search fields; migrations and DB updates were added to ensure sensible defaults are present for existing installs.
- Tests: updated intelligent-search integration tests to reflect the new search settings and behavior.


## [0.2.36] - 2025-11-17

### Added

- Debug endpoint for indexer troubleshooting: `GET /api/indexers/{id}/debug-search` returns raw remote payload and parsed results for developer inspection.

### Changed

- MyAnonamouse indexer: switched authentication to use the `mam_id` cookie (from indexer settings) and hardened request handling to tolerate multiple JSON/HTML-wrapped response shapes.
- Search result canonical links: added `ResultUrl` to `SearchResult` and populated it for MyAnonamouse (canonical `https://myanonamouse.net/t/{id}`), Internet Archive (`https://archive.org/details/{identifier}`), and Torznab/Newznab (use `<link>` when present). Frontend now prefers `result.resultUrl` for title links.
- Frontend: updated `ManualSearchModal.vue`, `SearchView.vue` and related components to make result titles link to the indexer item page (fallbacks: `productUrl`, `torrentUrl`, `nzbUrl`, `magnetLink`) and added `resultUrl` to TypeScript types.

### Fixed

- Robust parsing fixes for multiple indexer response shapes and case-insensitive deserialization in debug flows; ensures `SearchResult` fields (Title, Size, Seeders, TorrentUrl, Source) are populated for MyAnonamouse results.
- Backend: ensured `SearchResult.Source` is consistently set (fallbacks: indexer name, implementation, host) so UI displays indexer names reliably.

## [0.2.35] - 2025-11-16

### Added

- Authoritative EF Core migration to add missing `ApplicationSettings` columns (e.g. `EnabledNotificationTriggers`, `WebhookUrl`, US proxy fields, and related settings). This ensures fresh installs and upgrades receive the required schema changes via migrations.

### Changed

- Removed the emergency runtime ALTER/PRAGMA schema patch from `Program.cs`; schema changes are now managed exclusively through EF Core migrations.
- Consolidated and cleaned up iterative/no-op migration artifacts introduced during debugging; created a single authoritative migration and backed up removed migration files for safety.

### Fixed

- Resolved runtime error "SQLite Error 1: 'no such column: a.EnabledNotificationTriggers'" by recording and applying the missing schema changes.
- Eliminated duplicate-migration compile errors caused by conflicting migration classes.


## [0.2.32] - 2025-11-15

### Added

- **Persistent State Management**: AudiobooksView and AddNewView now persist search queries, results, and UI state in localStorage for improved user experience across sessions
- **Item Details Toggle**: Added toolbar button to toggle extra item details in both grid and list views of the audiobooks library
- **Centralized Confirm Dialog**: New global confirm dialog service and component that replaces all individual confirm dialogs throughout the application
- **Custom Filter System**: Advanced filtering capabilities with custom filter modal, dropdown, and rule-based evaluator supporting complex boolean logic
- **Status Badges**: Clickable status badges in list view with keyboard navigation support for quick access to audiobook details
- **Expanded Sorting Options**: Added first name and last name sorting for authors and narrators in addition to existing options

### Changed

- **Mobile & Responsive UX Improvements**:
  - Toolbar buttons hide text and show only icons on screens 1024px and below for cleaner mobile interface
  - CustomSelect component uses PhArrowsDownUp icon and hides text/icons in trigger on mobile screens
  - List view badges stack vertically on screens 768px and below instead of being hidden
  - Improved mobile search and action controls with stacked, full-width CTAs
  - Enhanced responsive design across AudiobooksView, AddNewView, and other components

- **Audiobooks List View Enhancements**:
  - Persistent view mode (grid/list) using localStorage
  - Improved accessibility with better keyboard navigation and focus management
  - Enhanced visual feedback for row selection and hover states
  - Status badges with click/keyboard navigation to audiobook details
  - Better checkbox and row click handling for intuitive selection

- **Component Architecture**:
  - Refactored confirm dialogs to use centralized modal system
  - Added custom filter evaluation utility with support for grouping and parentheses
  - Improved component reusability and consistency across the application

### Fixed

- **Toggle Functionality**: Fixed additional details toggle that stopped working when switching between grid and list views by removing problematic v-memo directive
- **Mobile Responsiveness**: Resolved layout issues and improved touch ergonomics on mobile devices
- **State Persistence**: Ensured search queries and UI preferences are properly saved and restored across sessions

## [0.2.31] - 2025-11-10

### Added

- Server-side API key passthrough: the `DiscordBotService` now injects a `LISTENARR_API_KEY` environment variable into the helper process when a startup-config API key exists. This allows trusted helper processes to authenticate programmatic requests to the backend without an interactive login.

### Changed

- Admin command changes:
  - Temporarily disabled the admin `request-config set-channel` subcommand to avoid accidental channel configuration changes while debugging production helper behavior. The change was applied in both the server command payload (`DiscordController`) and the helper `tools/discord-bot/index.js` (including published/bin copies) so the subcommand will not be registered until explicitly re-enabled.

- Bot helper auth and networking:
  - The Node helper (`tools/discord-bot/index.js`) now reads `process.env.LISTENARR_API_KEY` when present and automatically attaches `X-Api-Key: <key>` to outgoing fetch requests (unless a request explicitly provides its own ApiKey header).
  - SignalR connections from the helper use the API key as the access token (via `accessTokenFactory`) so the `/hubs/*/negotiate` step accepts the helper in authenticated deployments.
  - The same changes were applied to the published/bin copies of the helper (`listenarr.api/publish/...` and `listenarr.api/bin/Release/...`) so runtime images built from publish output include the fix.

- Frontend: Mobile & responsive UX improvements
  - `SettingsView` desktop tabs now use a horizontal carousel when tabs overflow to prevent layout overflow and enable keyboard/chevron navigation.
  - `App` header: mobile search replaced the pseudo-backdrop with a real DOM backdrop for reliable click-to-close behavior; mobile search input overlay behavior refined; mobile menu (hamburger) button hidden on desktop via responsive CSS.
  - `AudiobookDetailView`: mobile actions reorganized — primary actions are surfaced inside a collapsed "More" menu on small screens and the dropdown is positioned as an absolute overlay to avoid expanding the top-nav.
  - `AddNewView`: search and action controls improved for small screens (stacked, full-width CTAs, scrollbar-gutter stability) for better touch ergonomics.
  - `SystemView` / `LogsView`: fixed horizontal overflow and long-line wrapping on narrow viewports.
  - `CustomSelect` component: fixed click propagation race and replaced an unsafe `$event.target` access with a typed native input handler to resolve TypeScript build issues.
  - Misc CSS tweaks: added `scrollbar-gutter`, ensured `min-width: 0` on flex children, and other responsive fixes to eliminate unintended horizontal scrolling across multiple views.

### Fixed

- Resolved 401s observed in production where the helper used the correct `LISTENARR_URL` but did not present credentials when calling `/api/configuration/settings` or negotiating SignalR. Passing the API key into the helper and including it on requests resolves those authentication failures for programmatic helper flows.

- Frontend fixes:
  - Resolved a TypeScript build error caused by unsafe event target access in `CustomSelect.vue` by introducing a typed native input handler.
  - Fixed mobile top-nav layout shift when opening the "More" actions menu by making the dropdown an absolute overlay.
  - Prevented settings tabs from overflowing the header by adding an overflow-hidden carousel wrapper and navigation chevrons on desktop when needed.

## [0.2.30] - 2025-11-09

### Fixed

- Discord helper bot startup race: the Node helper resolved `LISTENARR_URL` asynchronously at module load time which allowed the initial network calls to default to `http://localhost:5000`, causing authentication failures (SignalR negotiation and settings fetch returned 401) in containerized production. The startup routine now awaits `resolveListenarrUrl()` before performing any outbound requests so the environment-provided `LISTENARR_URL` (or `.env`) is used immediately.

## [0.2.29] - 2025-11-09

### Changed

- Key changes for 0.2.29 (URL resolution & Docker-aware fallbacks)
  - Added `IHttpContextAccessor` usage/injection to support constructing the Listenarr public URL from the current HTTP request when available (useful behind reverse proxies)
  - Improved URL resolution priority (used when the app needs to provide an absolute Listenarr URL to helper processes or external systems):
    1. `LISTENARR_PUBLIC_URL` environment variable (highest priority)
    2. Current HTTP request (uses `IHttpContextAccessor` and honors X-Forwarded-* headers)
    3. Startup config (configured via the existing startup JSON/config service)
    4. Fallback: `host.docker.internal` (when running in Docker) or `localhost` (non-Docker fallback)
  - Docker-aware fallback: when `DOCKER_ENV` environment variable is present/true the runtime will prefer `host.docker.internal` instead of `localhost` for local host fallbacks
  - Additional per-step logging added to help diagnose URL resolution issues (logs which source was selected and any header-based values used)
  - Updated Dockerfile to explicitly set the `DOCKER_ENV` environment variable so Docker-aware fallbacks are enabled.

### Notes

- These changes make URL resolution more robust behind reverse proxies and in Docker-based deployments, and provide better logging to debug any cases where the helper bot (or external integrations) cannot reach the Listenarr API.

## [0.2.28] - 2025-11-09

### Changed

 - Publish and deployment reliability improvements
  - Ensure `tools/**` is included in publish output and runtime images by updating `listenarr.api/Listenarr.Api.csproj` and adding an explicit MSBuild `CopyToolsToPublish` target as a fallback.
  - The runtime Dockerfile (`listenarr.api/Dockerfile.runtime`) now accepts a build-arg `PUBLISH_DIR` and copies from that path; CI workflows were updated to pass the appropriate publish directory so images are built from the exact CI publish output (for example `listenarr.api/publish/linux-x64`).
  - CI and Canary workflows: added publish sanity checks, artifact upload (for debug), a fail-fast check in CI, and a copy-then-verify safety step in Canary so builds abort or repair when `tools` is missing from publish output.

### Fixed

 - Verified that local `dotnet publish` now includes `listenarr.api/publish/tools/discord-bot` and adjusted workflows and Docker build logic to make image builds deterministic and reproducible locally and in CI.
## [0.2.27] - 2025-11-09

### Fixed

  - **CI: fail-fast & publish verification**: Added quick-fail checks and publish-folder verification to CI and Canary workflows so builds abort if the `tools` folder is missing from publish output
    - Canary workflow now lists the publish folder, uploads the publish artifact for inspection, and contains a copy-then-verify step that will copy `tools` into the publish folder if CIS publish missed them
    - Main CI workflow now performs a fail-fast check after `dotnet publish` to avoid building/pushing images that don't include the discord helper files
    - These steps reduce the risk of releasing runtime images that cannot start the Discord helper bot

## [0.2.26] - 2025-11-09

### Fixed

 - **Publish: include tools folder**: Ensured `tools/**` is copied to the publish output by updating `Listenarr.Api.csproj` (added <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory> for `tools\**\*.*`)
   - Fixes missing `/app/tools/discord-bot` inside runtime containers when publishing + copying publish output into images
   - After this change, run `dotnet publish` and rebuild your image so the tooling directory is included in the container

## [0.2.25] - 2025-11-09

### Added

- **Docker as Primary Production Method**: Promoted Docker as the recommended production deployment method in README.md
  - Docker section moved to first position with clear benefits highlighted
  - Added Docker Compose example for easier production deployments
  - Emphasized Docker's advantages: isolation, updates, consistency, security, and included Node.js
- **Pre-built Executables for Production**: Promoted executable downloads as secondary production deployment method
  - Clear instructions for downloading from GitHub Releases
  - Emphasized self-contained executables with no .NET Runtime requirement
  - Added Node.js and LISTENARR_PUBLIC_URL prerequisites for Discord bot functionality
  - Reorganized deployment options to prioritize Docker over executables for production
- **Docker Environment Configuration**: Added `LISTENARR_PUBLIC_URL` environment variable to `docker-compose.yml`
  - Required for Discord bot functionality in Docker production deployments
  - Enables proper URL configuration for bot interactions with the Listenarr API
  - Users must replace `https://your-domain.com` with their actual domain or IP address
- **Non-Docker Production Deployment Guide**: Added comprehensive instructions for production deployments without Docker
  - Publishing instructions for Windows, Linux, and macOS platforms
  - Environment variable configuration for Discord bot functionality
  - IIS deployment guidance for Windows servers
  - Node.js installation requirements for Discord bot support

### Fixed

- **Docker Runtime**: Added Node.js 20 installation to final runtime image for Discord bot support
  - Resolves "Failed to start bot" errors in Docker production deployments
  - Ensures Node.js runtime is available for Discord bot process execution

## [0.2.24] - 2025-11-08

### Fixed

- **Database migration: Discord settings**: recreated migration with new timestamp `20251109043000_AddDiscordSettingsToApplicationSettings` to ensure it runs on all databases, including those with broken migration history
  - Migration adds `DiscordApplicationId`, `DiscordBotAvatar`, `DiscordBotEnabled`, `DiscordBotToken`, `DiscordBotUsername`, `DiscordChannelId`, `DiscordCommandGroupName`, `DiscordCommandSubcommandName`, and `DiscordGuildId`
  - **Automatic fix for existing users**: Renamed migration ensures it executes regardless of previous broken migration state, fixing databases without manual intervention
  - Verified migration applies correctly and resolves 'no such column' errors
- **Discord bot service**: fixed production deployment issues and URL configuration
  - **Working directory**: Changed from hardcoded relative path to use `IHostEnvironment.ContentRootPath` for correct path resolution in published applications
  - **Directory inclusion**: Added tools directory to project publish to ensure discord-bot files are available in production
  - **URL configuration**: Added support for `LISTENARR_PUBLIC_URL` environment variable for production deployments, with fallback to startup config
  - **Error handling**: Added validation for bot directory and index.js existence with detailed error logging
  - **Dependencies**: Injected `IStartupConfigService` and `IHostEnvironment` for proper configuration access

## [0.2.23] - 2025-11-08

### Fixed

- **Database migration: Discord settings**: recreated migration with new timestamp `20251109043000_AddDiscordSettingsToApplicationSettings` to ensure it runs on all databases, including those with broken migration history
  - Migration adds `DiscordApplicationId`, `DiscordBotAvatar`, `DiscordBotEnabled`, `DiscordBotToken`, `DiscordBotUsername`, `DiscordChannelId`, `DiscordCommandGroupName`, `DiscordCommandSubcommandName`, and `DiscordGuildId`

## [0.2.22] - 2025-11-08

### Fixed

- **Backend: warning cleanup**: silence CS1998 compiler warnings in `DiscordBotService` by returning completed tasks for synchronous methods (StopBotAsync, IsBotRunningAsync)

## [0.2.21] - 2025-11-08

### Added

- **Professional Webhook Test Menu**: Enhanced notification testing UI
  - Bell icon dropdown menu in AudiobookDetailView with 3 trigger options
  - Only appears in development builds when webhooks are configured and at least one is enabled
  - Automatic webhook selector modal for multiple webhook configurations
  - Targeted testing: Send test notifications to specific webhooks
  - Backend support: DiagnosticsController now accepts optional webhookId parameter
  - Improved UX: Shows webhook name in success toast notifications
- **Discord helper bot (tools/discord-bot)**: reference Node.js bot to register a slash command and forward requests to the Listenarr API for development and troubleshooting
  - Ephemeral interactive flow: search → select → quality → confirm → request
  - Automatic Listenarr URL persistence: prompts once and saves `tools/discord-bot/.env` (or reads `LISTENARR_URL` env)
  - README documentation for bot setup and troubleshooting

### Fixed

- **Development-Only UI Elements**: Hidden test notification buttons in production
  - AudiobookDetailView: Wrapped 3 test notification buttons in `v-if="isDevelopment"` check
  - Buttons only visible in development mode, preventing confusion in production deployments
- **Discord bot session & CSRF flows**: improved reliability when users interact with the ephemeral select/confirm flow
  - Preserve interaction tokens so ephemeral replies can be removed when a request completes
  - Fetch antiforgery token from `/api/antiforgery/token` and retry POST /api/library/add with `X-XSRF-TOKEN` when the server returns CSRF errors
  - Implement cookie-aware fetch where possible (optional `fetch-cookie` + `tough-cookie` packages)
- **Metadata validation**: normalize metadata shapes before POSTing to `/api/library/add` so authors, narrators, tags and genres are always string arrays and series fields are stringified
- **Idempotency**: add `Idempotency-Key` header to library add requests to enable safe retries and deduplication
- **Message lifecycle & UX**: make the interactive flow ephemeral-only to avoid duplicate channel posts and update the original message instead of replying
  - On success the confirm button is updated to a disabled green “Added” button
  - Components are disabled immediately after Request to prevent double-processing

### Changed

- **Webhook Test Menu**: Gate Test menu to development builds and require at least one enabled webhook for visibility

## [0.2.20] - 2025-11-05

### Added

- **Production Logger Utility**: Environment-aware logging system (`fe/src/utils/logger.ts`)
- Automatically disabled in production (except errors) for performance
- Supports debug, info, warn, and error levels
- Integrated across entire Vue.js frontend
- **CHANGELOG.md**: Comprehensive changelog following Keep a Changelog format
- **SECURITY.md**: Complete security policy with vulnerability reporting process, best practices, and audit trail
- **Audiobook Status Indicators**: Visual border colors on audiobook cards
- Red border: No files (missing)
- Blue pulsing border: Currently downloading
- Yellow border: Quality mismatch (has files but doesn't meet cutoff)
- Green border: Quality match (meets requirements)

### Fixed

- **CRITICAL: qBittorrent Incremental Sync Cache**: Fixed torrents disappearing from queue UI on incremental updates
- The `/api/v2/sync/maindata` endpoint only returns changed torrents, not the full list
- Implemented `_qbittorrentTorrentCache` dictionary to maintain complete torrent state across polls
- Incremental updates now merge into cache instead of replacing it
- Handles `torrents_removed` to properly clean up deleted torrents
- Full updates clear cache and rebuild from scratch
- Queue UI now shows all torrents consistently, regardless of which ones changed
- **Production URL Configuration**: Fixed hardcoded localhost in `loadInitialLogs` function
- Now uses environment-aware URL: localhost in dev, configured base URL in production
- Ensures system logs load correctly in all deployment scenarios
- **XML Documentation**: Fixed incorrect HTML entity decode example in NotificationService
- Changed from confusing double-encoded example to accurate single-decode: "&amp;amp;" -> "&amp;"
- **Critical Test Failures**: Fixed 6 failing unit tests achieving 100% pass rate (50/50 tests passing)
  - Fixed 4 DownloadService constructor tests by adding IHttpClientFactory, IMemoryCache, and IDbContextFactory dependencies
  - Fixed 2 SearchController tests by properly mocking AudimetaService with required constructor parameters
  - Test files: `DownloadProcessingTests.cs`, `DownloadProcessing_NoDoubleMoveTests.cs`, `DownloadNaming_AudiobookMetadataTests.cs`, `SearchControllerTests.cs`
- **Production Logging Cleanup**: Removed/replaced 35+ console.log statements for production readiness
  - App.vue: 19 console statements replaced with logger calls
  - SettingsView.vue: 5 debug statements removed from webhook migration code
  - WantedView.vue: 5 statements replaced with logger.debug/error
  - SystemView.vue: 2 statements replaced with logger.debug/error
  - AudiobookDetailView.vue: 4 console statements replaced with logger.debug/error
- **Resource Management**: Fixed memory leaks by properly disposing HttpContent objects
  - DownloadService: Added `using var` to 8 FormUrlEncodedContent instances
  - DownloadService: Added `using var` to 1 StringContent instance (NZBGet ping)
  - DownloadMonitorService: Added `using var` to 1 FormUrlEncodedContent instance
  - NotificationService: Added `using var` to 1 StringContent instance
- **Cross-Browser Compatibility**: Replaced `crypto.randomUUID()` with polyfill for Safari <15.4 and older browsers
  - SettingsView: Implemented `generateUUID()` function using `Math.random()` with RFC 4122 v4 format
- **Virtual Scrolling**: Fixed ROW_HEIGHT constant in WantedView (140 → 165) for accurate scroll positioning
- **Performance Optimization**: Replaced inefficient `ContainsKey` + indexer pattern with `TryGetValue`
  - DownloadService: 30+ instances optimized in qBittorrent queue parsing
  - SearchService: Changed ASIN deduplication to use `TryAdd`
- **Code Quality**: Fixed useless assignment in SystemService log reading

### Changed

- **Code Documentation**: Replaced vague TODO comments with detailed NOTE explanations
- DownloadService: Documented 4 minimal method implementations (GetActiveDownloadsAsync, GetDownloadAsync, CancelDownloadAsync, UpdateDownloadStatusAsync)
- AudiobooksView: Explained downloading status requires Download-to-Audiobook linking
- SettingsView: Documented webhook test API integration path for future enhancement
- **Code Formatting**: Moved inline comments to separate lines for better readability
- DownloadService: Fixed 3 inline comments in dictionary declarations following C# conventions
- **Logger Integration**: Systematic replacement of console statements with environment-aware logging
  - Development: Full debug logging enabled
  - Production: Only error logging for performance and log pollution prevention
- **API Documentation**: Verified Swagger/OpenAPI configuration with XML comments enabled
- **Release Readiness**: Comprehensive polish for stable production deployment

### Documentation

- Added security best practices for deployment, configuration, and known security considerations
- Documented supported versions and security update process
- Created complete release documentation structure
- Added GitHub repository links for version comparison
- Fixed XML comment HTML entity encoding in NotificationService

### Technical Debt

- Download-to-Audiobook linking system not yet implemented (documented in AudiobooksView.vue)
  - Currently downloads tracked separately in DownloadsView until completion
  - Future enhancement: Link Download records to Audiobook IDs for real-time status
- DownloadService methods remain minimal as downloads managed by external clients
  - Architecture decision: Queue fetched directly from qBittorrent, Transmission, SABnzbd, NZBGet
  - SignalR broadcasts handle real-time updates without polling
- **Generic Exception Catches**: Program.cs uses generic catches for startup resilience (intentional design)
  - Proxy configuration (line 331): Non-critical, logs warning and continues
  - Swagger XML comments (line 378): Non-critical, logs warning and continues
  - Database migration (line 465): Has detailed fallback strategy for test compatibility
  - EnsureCreated fallback (line 493): Explicitly designed for test harness flexibility
  - All catches log appropriately and allow app to start despite configuration failures

## [0.2.19] - Previous Release

### Added

- Initial beta release with core audiobook management features
- Multi-API search across torrent and NZB providers
- Download client integration (qBittorrent, Transmission, SABnzbd, NZBGet)
- Audible metadata integration via Audnexus API
- SQLite database with Entity Framework Core
- Vue.js 3 frontend with TypeScript and Pinia state management
- Real-time download status via SignalR
- Image caching service with automatic cleanup
- File browser for path selection
- Modern responsive dashboard

---

## Version History Legend

- **Added**: New features
- **Changed**: Changes in existing functionality
- **Deprecated**: Soon-to-be removed features
- **Removed**: Removed features
- **Fixed**: Bug fixes
- **Security**: Vulnerability fixes
