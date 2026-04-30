/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Encodings.Web;
using Listenarr.Domain.Utils;
using Listenarr.Api.Utils;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that monitors download clients and pushes updates via SignalR
    /// </summary>
    public class DownloadMonitorService : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IHubContext<DownloadHub> _hubContext;
        private readonly ILogger<DownloadMonitorService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAppMetricsService _metrics;
        private TimeSpan _pollingInterval = TimeSpan.FromSeconds(30); // default; overridden by ApplicationSettings.PollingIntervalSeconds
        private readonly Dictionary<string, Download> _lastDownloadStates = new();
        // Tracks downloads that appear complete and the time they were first observed complete
        private readonly Dictionary<string, DateTime> _completionCandidates = new();
        private readonly TimeSpan _completionStableWindow = TimeSpan.FromSeconds(10);
        // Track missing-source retry attempts and scheduled retries to avoid duplicate scheduling
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _missingSourceRetryAttempts = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _missingSourceRetryScheduled = new();

        // Per-client polling controls to avoid overloading download clients
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _nextClientPoll = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _clientFailureCounts = new();

        // Simple memory cache for per-torrent properties fetched from qBittorrent
        private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache;

        public DownloadMonitorService(
            IServiceScopeFactory serviceScopeFactory,
            IHubContext<DownloadHub> hubContext,
            ILogger<DownloadMonitorService> logger,
            IHttpClientFactory httpClientFactory,
            IAppMetricsService? appMetrics = null)
        {
            Microsoft.Extensions.Caching.Memory.IMemoryCache? memCache = null;
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                memCache = scope.ServiceProvider.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            if (memCache == null)
            {
                memCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
            }

            _serviceScopeFactory = serviceScopeFactory;
            _hubContext = hubContext;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _memoryCache = memCache;
            _metrics = appMetrics ?? new NoopAppMetricsService();
        }

        // Cache entry for qbittorrent per-torrent properties (used sparingly, only when needed)
        private sealed class QbittorrentPropertiesCacheEntry
        {
            public string SavePath { get; set; } = string.Empty;
        }

        // Schedule next poll for a client after a successful interaction
        private async Task ScheduleNextClientPollOnSuccess(string clientId, int activeDownloadsForClient)
        {
            try
            {
                // Prefer client-specific setting if provided (PollingIntervalSeconds >= 15)
                int interval = (int)_pollingInterval.TotalSeconds;
                try
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var clientConfigRepository = scope.ServiceProvider.GetRequiredService<IDownloadClientConfigurationRepository>();
                    var client = await clientConfigRepository.GetByIdAsync(clientId);
                    if (client != null && client.Settings != null)
                    {
                        bool hasSetting = client.Settings.TryGetValue("PollingIntervalSeconds", out var v);
                        if (hasSetting && int.TryParse(v?.ToString() ?? string.Empty, out var custom) && custom >= 15)
                            interval = custom;
                    }
                }
                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                // If no active downloads for client, back off to a longer interval
                if (activeDownloadsForClient == 0)
                {
                    interval = Math.Max(interval * 4, 120);
                }

                // Add small jitter to avoid synchronized polls: +/- 5s
                var jitter = (int)(new Random().NextDouble() * 10 - 5);
                var next = DateTime.UtcNow.AddSeconds(Math.Max(15, interval + jitter));
                _nextClientPoll.AddOrUpdate(clientId, next, (_, __) => next);

                // Reset failure count
                _clientFailureCounts.TryRemove(clientId, out _);

                _logger.LogDebug("Scheduled next poll for client {ClientId} at {Next} (interval {Interval}s)", clientId, next, interval);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to schedule next client poll for {ClientId}", clientId);
            }
        }

        // Schedule next poll for a client after a failure using exponential backoff
        private void ScheduleNextClientPollOnFailure(string clientId)
        {
            try
            {
                var count = _clientFailureCounts.AddOrUpdate(clientId, 1, (_, old) => old + 1);
                // base backoff 30s, exponential, cap at 15min
                var backoff = Math.Min(900, 30 * Math.Pow(2, count - 1));
                var jitter = (int)(new Random().NextDouble() * 5);
                var next = DateTime.UtcNow.AddSeconds(backoff + jitter);
                _nextClientPoll.AddOrUpdate(clientId, next, (_, __) => next);
                _logger.LogWarning("Scheduled next poll for client {ClientId} after failure in {Seconds}s (attempt {Attempt})", clientId, backoff, count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to schedule next client poll on failure for {ClientId}", clientId);
            }
        }


        /// <summary>
        /// Attempt to move a directory with retries and exponential backoff. Emits diagnostics (file listing and ACLs)
        /// on failures to aid debugging file-lock/permission issues.
        /// </summary>
        private async Task<bool> TryMoveDirectoryWithRetryAsync(string sourceDir, string destDir, int maxAttempts = 4, int initialDelayMs = 1000)
        {
            var attempt = 0;
            var delay = initialDelayMs;

            for (; attempt < maxAttempts; attempt++)
            {
                try
                {
                    Directory.Move(sourceDir, destDir);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Directory.Move attempt {Attempt}/{Max} failed: {Source} -> {Dest}", attempt + 1, maxAttempts, LogRedaction.SanitizeText(sourceDir), LogRedaction.SanitizeText(destDir));

                    // Dump a small directory listing sample for diagnostics
                    try
                    {
                        var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
                        _logger.LogWarning("Directory listing for {Source} (count={Count}), sample: {Sample}", LogRedaction.SanitizeText(sourceDir), files.Length, LogRedaction.SanitizeText(string.Join(", ", files.Take(5).Select(f => Path.GetFileName(f)))));
                    }
                    catch (Exception listEx) when (listEx is not OperationCanceledException && listEx is not OutOfMemoryException && listEx is not StackOverflowException)
                    {
                        _logger.LogDebug(listEx, "Failed to enumerate files in {Source} while diagnosing move failure", LogRedaction.SanitizeText(sourceDir));
                    }

                    // Dump ACL/owner information if available (Windows-friendly). Failures are non-blocking.
                    try
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var dirSec = new DirectoryInfo(sourceDir).GetAccessControl();
                            var owner = dirSec.GetOwner(typeof(NTAccount))?.ToString() ?? "unknown";
                            _logger.LogWarning("Directory owner for {Source}: {Owner}", LogRedaction.SanitizeText(sourceDir), LogRedaction.SanitizeText(owner));

                            var rules = dirSec.GetAccessRules(true, true, typeof(NTAccount));
                            foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>().Take(10))
                            {
                                _logger.LogWarning("ACL {Source}: {Identity} {Type} {Rights}", LogRedaction.SanitizeText(sourceDir), LogRedaction.SanitizeText(rule.IdentityReference.Value), rule.AccessControlType, rule.FileSystemRights);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Skipping ACL diagnostics for {Source} (non-Windows OS)", LogRedaction.SanitizeText(sourceDir));
                        }
                    }
                    catch (Exception aclEx) when (aclEx is not OperationCanceledException && aclEx is not OutOfMemoryException && aclEx is not StackOverflowException)
                    {
                        _logger.LogDebug(aclEx, "Failed to read ACLs for {Source}", LogRedaction.SanitizeText(sourceDir));
                    }

                    if (attempt < maxAttempts - 1)
                    {
                        _logger.LogInformation("Retrying Directory.Move in {Delay}ms...", delay);
                        await Task.Delay(delay);
                        delay *= 2;
                    }
                }
            }

            return false;
        }

        private async Task<bool> TryMoveFileWithRetryAsync(string sourceFile, string destFile, int maxAttempts = 4, int initialDelayMs = 1000)
        {
            var attempt = 0;
            var delay = initialDelayMs;

            for (; attempt < maxAttempts; attempt++)
            {
                try
                {
                    // Use File.Move with overwrite when available
                    File.Move(sourceFile, destFile, true);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "File.Move attempt {Attempt}/{Max} failed: {Source} -> {Dest}", attempt + 1, maxAttempts, LogRedaction.SanitizeText(sourceFile), LogRedaction.SanitizeText(destFile));

                    // Try opening the source file to detect locks
                    try
                    {
                        using var stream = File.Open(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                        _logger.LogDebug("Able to open source file for read during diagnostic: {File}", LogRedaction.SanitizeText(sourceFile));
                    }
                    catch (Exception openEx) when (openEx is not OperationCanceledException && openEx is not OutOfMemoryException && openEx is not StackOverflowException)
                    {
                        _logger.LogWarning(openEx, "Failed to open source file for read (may be locked): {File}", LogRedaction.SanitizeText(sourceFile));
                    }

                    try
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var fileSec = new FileInfo(sourceFile).GetAccessControl();
                            var owner = fileSec.GetOwner(typeof(NTAccount))?.ToString() ?? "unknown";
                            _logger.LogWarning("File owner for {File}: {Owner}", LogRedaction.SanitizeText(sourceFile), LogRedaction.SanitizeText(owner));
                            var rules = fileSec.GetAccessRules(true, true, typeof(NTAccount));
                            foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>().Take(10))
                            {
                                _logger.LogWarning("ACL {File}: {Identity} {Type} {Rights}", LogRedaction.SanitizeText(sourceFile), LogRedaction.SanitizeText(rule.IdentityReference.Value), rule.AccessControlType, rule.FileSystemRights);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Skipping file ACL diagnostics for {File} (non-Windows OS)", LogRedaction.SanitizeText(sourceFile));
                        }
                    }
                    catch (Exception aclEx) when (aclEx is not OperationCanceledException && aclEx is not OutOfMemoryException && aclEx is not StackOverflowException)
                    {
                        _logger.LogDebug(aclEx, "Failed to read file ACLs for {File}", LogRedaction.SanitizeText(sourceFile));
                    }

                    if (attempt < maxAttempts - 1)
                    {
                        _logger.LogInformation("Retrying File.Move in {Delay}ms...", delay);
                        await Task.Delay(delay);
                        delay *= 2;
                    }
                }
            }

            return false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Download Monitor Service starting");

            // Wait a bit before starting to ensure the app is fully initialized
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Download Monitor Service canceled before start");
                return;
            }

            // Attempt to read configured polling interval from ApplicationSettings (fallback to current default)
            try
            {
                using var initScope = _serviceScopeFactory.CreateScope();
                var cfg = initScope.ServiceProvider.GetService<IConfigurationService>();
                var appSettings = cfg != null
                    ? await cfg.GetApplicationSettingsAsync() ?? new ApplicationSettings()
                    : new ApplicationSettings();

                if (appSettings.PollingIntervalSeconds > 0)
                {
                    _pollingInterval = TimeSpan.FromSeconds(appSettings.PollingIntervalSeconds);
                }
                _logger.LogInformation("DownloadMonitorService polling interval set to {Interval}s", _pollingInterval.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Download Monitor Service canceled while reading startup configuration");
                return;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Download monitor settings load canceled/timed out; using default polling interval");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to read polling interval from settings, using default {Default}s", _pollingInterval.TotalSeconds);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await MonitorDownloadsAsync(stoppingToken);
                }
                catch (TaskCanceledException ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Download monitor HTTP request timed out; continuing background polling loop");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Download monitor operation canceled/timed out; continuing background polling loop");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error in Download Monitor Service");
                }

                // Wait before next poll
                try
                {
                    await Task.Delay(_pollingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Download Monitor Service stopping");
        }

        private async Task MonitorDownloadsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();

            ApplicationSettings appSettings;
            try
            {
                appSettings = await configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
            {
                appSettings = new ApplicationSettings();
            }

            HashSet<string> enabledClientIds;
            try
            {
                var configuredClients = await configService.GetDownloadClientConfigurationsAsync();
                enabledClientIds = configuredClients
                    .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                    .Select(c => c.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to load download client configurations; skipping external client polling for this cycle");
                enabledClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // Get all active downloads from database
            // Include:
            // - Queued, Downloading, Paused, Processing (actively being monitored)
            // - Completed without FinalPath (completed in client but not yet imported)
            // - ImportPending without FinalPath (import attempted but still unresolved)
            // - Moved with DownloadClientId (imported but deferred client removal pending;
            //   we keep polling so CanBeRemoved metadata stays fresh for ProcessDeferredRemovalsAsync)
            // Exclude:
            // - ImportBlocked (blocked due to repeated failures, no point in retrying)
            // - Failed, Cancelled (terminal states)
            var activeDownloadsAll = await downloadRepository.GetActiveForMonitoringAsync();

            // Skip downloads from disabled/missing external clients.
            // They stay alive so they resume automatically when the client is re-enabled.
            // DDL entries are internal and not tied to external client configuration.
            var activeDownloads = activeDownloadsAll
                .Where(d =>
                    string.Equals(d.DownloadClientId, "DDL", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)))
                .ToList();

            var skippedDisabledClientDownloads = activeDownloadsAll.Count - activeDownloads.Count;
            if (skippedDisabledClientDownloads > 0)
            {
                _logger.LogDebug("Skipping {Count} active downloads from disabled or missing download clients", skippedDisabledClientDownloads);
            }

            _logger.LogInformation("DownloadMonitorService found {Count} active downloads", activeDownloads.Count);
            foreach (var dl in activeDownloads)
            {
                _logger.LogInformation("Active download: {Id} - {Title} - Status: {Status} - Client: {ClientId}",
                    LogRedaction.SanitizeText(dl.Id), LogRedaction.SanitizeText(dl.Title), dl.Status, LogRedaction.SanitizeText(dl.DownloadClientId));
            }

            // Only poll download clients if there are active downloads
            if (activeDownloads.Any())
            {
                var clientDownloads = activeDownloads
                    .Where(d => !string.Equals(d.DownloadClientId, "DDL", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(d.DownloadClientId))
                    .ToList();

                _logger.LogInformation("Client downloads (non-DDL): {Count}", clientDownloads.Count);
                if (clientDownloads.Any())
                {
                    if (enabledClientIds.Count == 0)
                    {
                        _logger.LogInformation("No enabled download clients configured; skipping client polling");
                    }
                    else
                    {
                        var pollableClientDownloads = clientDownloads
                            .Where(d => enabledClientIds.Contains(d.DownloadClientId))
                            .ToList();

                        var skippedOrphanCount = clientDownloads.Count - pollableClientDownloads.Count;
                        if (skippedOrphanCount > 0)
                        {
                            _logger.LogDebug("Skipping {Count} active downloads with missing/disabled client configuration", skippedOrphanCount);
                        }

                        if (pollableClientDownloads.Any())
                        {
                            _logger.LogInformation("Calling PollDownloadClientsAsync with {Count} downloads", pollableClientDownloads.Count);
                            await PollDownloadClientsAsync(pollableClientDownloads, configService, downloadRepository, appSettings, cancellationToken);
                        }
                        else
                        {
                            _logger.LogInformation("No active downloads mapped to enabled download clients; skipping client polling");
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("No client downloads to poll");
                }
            }

            // Get all downloads to send to clients (only if there are active downloads or every 30 seconds)
            List<Download> allDownloads = new();
            var shouldFetchAll = activeDownloads.Any() || (DateTime.UtcNow.Second % 30 == 0);

            if (shouldFetchAll)
            {
                allDownloads = await downloadRepository.GetRecentAsync(100);

                allDownloads = allDownloads
                    .Where(d =>
                        string.Equals(d.DownloadClientId, "DDL", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)))
                    .ToList();
            }

            // Check for changes and broadcast updates (only if we have data)
            if (allDownloads.Any())
            {
                await BroadcastDownloadUpdatesAsync(allDownloads, cancellationToken);
            }
        }

        /// <summary>
        /// Broadcast a candidate update for a download so clients can show completion candidates
        /// without requiring the DB status to change.
        /// </summary>
        private async Task BroadcastCandidateUpdateAsync(Download dl, bool isCandidate, CancellationToken cancellationToken)
        {
            try
            {
                var metadata = (dl.Metadata ?? new Dictionary<string, object>()).Where(kvp => !string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                metadata["CompletionCandidate"] = isCandidate;

                var payload = new
                {
                    id = dl.Id,
                    audiobookId = dl.AudiobookId,
                    title = dl.Title,
                    artist = dl.Artist,
                    album = dl.Album,
                    originalUrl = dl.OriginalUrl,
                    // Surface as Completed so UI's Completed lists can include candidates
                    status = isCandidate ? DownloadStatus.Completed.ToString() : dl.Status.ToString(),
                    progress = dl.Progress,
                    totalSize = dl.TotalSize,
                    downloadedSize = dl.DownloadedSize,
                    finalPath = dl.FinalPath,
                    startedAt = dl.StartedAt,
                    completedAt = dl.CompletedAt,
                    errorMessage = dl.ErrorMessage,
                    downloadClientId = dl.DownloadClientId,
                    metadata = metadata
                };

                _logger.LogInformation("Broadcasting candidate DownloadUpdate for {DownloadId}; isCandidate={IsCandidate}", dl.Id, isCandidate);
                await _hubContext.Clients.All.SendAsync("DownloadUpdate", new[] { payload }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast candidate update for {DownloadId}", dl.Id);
            }
        }

        private async Task PollDownloadClientsAsync(
            List<Download> downloads,
            IConfigurationService configService,
            IDownloadRepository downloadRepository,
            ApplicationSettings appSettings,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("PollDownloadClientsAsync called with {Count} downloads", downloads.Count);
            // Group downloads by client
            var downloadsByClient = downloads.GroupBy(d => d.DownloadClientId);

            foreach (var clientGroup in downloadsByClient)
            {
                var clientId = clientGroup.Key;
                _logger.LogInformation("Processing client group: ClientId={ClientId}, Count={Count}", clientId, clientGroup.Count());
                if (string.IsNullOrEmpty(clientId))
                {
                    _logger.LogWarning("Skipping client group with empty ClientId");
                    continue;
                }

                try
                {
                    var client = await configService.GetDownloadClientConfigurationAsync(clientId);
                    if (client == null)
                    {
                        _logger.LogWarning("Client configuration not found for ClientId={ClientId}", clientId);
                        continue;
                    }
                    if (!client.IsEnabled)
                    {
                        _logger.LogInformation("Client {ClientName} is disabled, skipping", client.Name);
                        continue;
                    }

                    _logger.LogInformation("Client {ClientName} (Type={Type}) is enabled, routing to poll method", client.Name, client.Type);

                    // Poll based on client type
                    switch (client.Type.ToLower())
                    {
                        case "qbittorrent":
                            await PollQBittorrentAsync(client, clientGroup.ToList(), downloadRepository, appSettings, cancellationToken);
                            break;
                        case "transmission":
                            await PollTransmissionAsync(client, clientGroup.ToList(), downloadRepository, appSettings, cancellationToken);
                            break;
                        case "sabnzbd":
                            await PollSABnzbdAsync(client, clientGroup.ToList(), downloadRepository, appSettings, cancellationToken);
                            break;
                        case "nzbget":
                            await PollNZBGetAsync(client, clientGroup.ToList(), downloadRepository, appSettings, cancellationToken);
                            break;
                    }
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Timeout polling download client {ClientId}; will retry on next schedule", clientId);
                    ScheduleNextClientPollOnFailure(clientId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error polling download client {ClientId}", clientId);
                }
            }
        }

        private Task PollQBittorrentAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            IDownloadRepository downloadRepository,
            ApplicationSettings appSettings,
            CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                _logger.LogDebug("Polling qBittorrent client {ClientName}", client.Name);
                try
                {
                    var now = DateTime.UtcNow;

                    // Respect per-client poll schedules to avoid overloading qbittorrent
                    if (_nextClientPoll.TryGetValue(client.Id, out var scheduled) && now < scheduled)
                    {
                        _logger.LogDebug("Skipping qBittorrent poll for {ClientName}, next scheduled at {Next}", client.Name, scheduled);
                        return;
                    }

                    var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
                    _logger.LogInformation("Polling qBittorrent client {ClientName} at {BaseUrl}", client.Name, baseUrl);

                    // Create an HttpClient with its own CookieContainer so the qBittorrent
                    // SID cookie from login is stored and sent with subsequent requests.
                    // The factory "DownloadClient" has UseCookies=false which breaks qBit auth.
                    var cookieJar = new System.Net.CookieContainer();
                    using var handler = new HttpClientHandler
                    {
                        CookieContainer = cookieJar,
                        UseCookies = true,
                        AutomaticDecompression = System.Net.DecompressionMethods.All
                    };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                    // Login
                    using var loginData = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                        new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                    });
                    using var loginResp = await http.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, cancellationToken);
                    if (!loginResp.IsSuccessStatusCode)
                    {
                        var loginError = await loginResp.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogWarning("qBittorrent login failed for client {ClientName} at {BaseUrl} - StatusCode={StatusCode}, Response={Response}",
                            client.Name, baseUrl, loginResp.StatusCode, loginError);
                        // Schedule a retry with backoff
                        ScheduleNextClientPollOnFailure(client.Id);
                        return;
                    }
                    _logger.LogDebug("qBittorrent login successful for client {ClientName}", client.Name);

                    // Fetch qBittorrent global preferences for seed limit evaluation (Sonarr parity)
                    bool qbtGlobalMaxRatioEnabled = false;
                    float qbtGlobalMaxRatio = -1f;
                    bool qbtGlobalMaxSeedingTimeEnabled = false;
                    long qbtGlobalMaxSeedingTime = -1;
                    bool qbtRemoveCompletedDownloads = !string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                        client.RemoveCompletedDownloads != "none";
                    try
                    {
                        using var prefsResp = await http.GetAsync($"{baseUrl}/api/v2/app/preferences", cancellationToken);
                        if (prefsResp.IsSuccessStatusCode)
                        {
                            var prefsJson = await prefsResp.Content.ReadAsStringAsync(cancellationToken);
                            if (!string.IsNullOrWhiteSpace(prefsJson))
                            {
                                var prefs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(prefsJson);
                                if (prefs != null)
                                {
                                    qbtGlobalMaxRatioEnabled = prefs.TryGetValue("max_ratio_enabled", out var mre) && mre.GetBoolean();
                                    qbtGlobalMaxRatio = prefs.TryGetValue("max_ratio", out var mr) ? (float)mr.GetDouble() : -1f;
                                    qbtGlobalMaxSeedingTimeEnabled = prefs.TryGetValue("max_seeding_time_enabled", out var mste) && mste.GetBoolean();
                                    qbtGlobalMaxSeedingTime = prefs.TryGetValue("max_seeding_time", out var mst) ? mst.GetInt64() : -1;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to fetch qBittorrent preferences for seed limit evaluation");
                    }

                    // Request all necessary fields from torrents/info to avoid additional API calls per torrent
                    // This single call replaces the need for individual /properties calls per download
                    var fields = "hash,name,save_path,content_path,progress,amount_left,state,size,category,completion_on,seeding_time,ratio,ratio_limit,seeding_time_limit";

                    // Prefer querying only the hashes we are tracking (if available) to avoid fetching all torrents
                    var trackedHashes = downloads
                        .Select(d => d.Metadata != null && d.Metadata.TryGetValue("TorrentHash", out var h) ? h?.ToString() : null)
                        .Where(h => !string.IsNullOrEmpty(h))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // If we have tracked hashes, chunk them into batches to avoid very large queries and to allow
                    // slight delays between requests to prevent overwhelming qBittorrent.
                    List<Dictionary<string, System.Text.Json.JsonElement>> allTorrents = new();

                    if (trackedHashes.Any())
                    {
                        const int batchSize = 100; // safe default batch size
                        _logger.LogDebug("Querying qBittorrent for specific hashes (total={Count}), using batches of {BatchSize}", trackedHashes.Count, batchSize);

                        var batches = Enumerable.Range(0, (trackedHashes.Count + batchSize - 1) / batchSize)
                            .Select(i => trackedHashes.Skip(i * batchSize).Take(batchSize).ToList())
                            .ToList();

                        foreach (var batch in batches)
                        {
                            var hashesParam = Uri.EscapeDataString(string.Join("|", batch));
                            var query = $"?hashes={hashesParam}&fields={Uri.EscapeDataString(fields)}";

                            using var torrentsResp = await http.GetAsync($"{baseUrl}/api/v2/torrents/info{query}", cancellationToken);
                            if (!torrentsResp.IsSuccessStatusCode)
                            {
                                var errorContent = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                                _logger.LogWarning("Failed to fetch torrent batch from qBittorrent for {ClientName} (batch size={Size}, URL={Url}, StatusCode={StatusCode}, Response={Response})",
                                    client.Name, batch.Count, $"{baseUrl}/api/v2/torrents/info{query}", torrentsResp.StatusCode, errorContent);
                                // Respect remote failure - stop processing further batches and let failure handling back off
                                ScheduleNextClientPollOnFailure(client.Id);
                                return;
                            }

                            var json = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                            var torrents = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(json);
                            if (torrents != null)
                            {
                                allTorrents.AddRange(torrents);
                            }

                            // Small delay between batches to avoid hammering the client
                            await Task.Delay(150, cancellationToken);
                        }
                    }
                    else
                    {
                        var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
                        if (!string.IsNullOrWhiteSpace(configuredCategory))
                        {
                            var cat = Uri.EscapeDataString(configuredCategory);
                            var query = $"?category={cat}&fields={Uri.EscapeDataString(fields)}";
                            _logger.LogDebug("Querying qBittorrent by category: {Category}", configuredCategory);

                            using var torrentsResp = await http.GetAsync($"{baseUrl}/api/v2/torrents/info{query}", cancellationToken);
                            if (!torrentsResp.IsSuccessStatusCode)
                            {
                                _logger.LogWarning("Failed to fetch torrents from qBittorrent for {ClientName}", client.Name);
                                ScheduleNextClientPollOnFailure(client.Id);
                                return;
                            }

                            var json = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                            var torrents = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(json);
                            if (torrents == null) return;

                            allTorrents.AddRange(torrents);
                        }
                        else
                        {
                            // Default: fetch a limited set of recent torrents
                            var query = $"?fields={Uri.EscapeDataString(fields)}";
                            using var torrentsResp = await http.GetAsync($"{baseUrl}/api/v2/torrents/info{query}", cancellationToken);
                            if (!torrentsResp.IsSuccessStatusCode)
                            {
                                _logger.LogWarning("Failed to fetch torrents from qBittorrent for {ClientName}", client.Name);
                                ScheduleNextClientPollOnFailure(client.Id);
                                return;
                            }

                            var json = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                            var torrents = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(json);
                            if (torrents == null) return;

                            allTorrents.AddRange(torrents);
                        }
                    }

                    // Build comprehensive lookup with all torrent info we need from single API call
                    var torrentLookup = new List<(string Hash, string Name, string SavePath, string ContentPath, double Progress, long AmountLeft, string State, long Size, string Category, long? SeedingTime, double Ratio, float RatioLimit, long SeedingTimeLimit, bool CanMoveFiles, bool CanBeRemoved)>();
                    foreach (var t in allTorrents)
                    {
                        var hash = t.TryGetValue("hash", out var hashElement) ? hashElement.GetString() ?? "" : "";
                        var name = t.TryGetValue("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                        var savePath = t.TryGetValue("save_path", out var savePathElement) ? savePathElement.GetString() ?? "" : "";
                        var contentPath = t.TryGetValue("content_path", out var contentPathElement) ? contentPathElement.GetString() ?? "" : "";
                        var progress = t.TryGetValue("progress", out var progressElement) ? progressElement.GetDouble() : 0.0;
                        var amountLeft = t.TryGetValue("amount_left", out var amountLeftElement) ? amountLeftElement.GetInt64() : 0L;
                        var state = t.TryGetValue("state", out var stateElement) ? stateElement.GetString() ?? "" : "";
                        var size = t.TryGetValue("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
                        var category = t.TryGetValue("category", out var categoryElement) ? categoryElement.GetString() ?? "" : "";
                        var seedingTime = t.TryGetValue("seeding_time", out var seedingTimeElement) ? seedingTimeElement.GetInt64() : (long?)null;
                        var tRatio = t.TryGetValue("ratio", out var ratioElement) ? ratioElement.GetDouble() : 0.0;
                        var tRatioLimit = t.TryGetValue("ratio_limit", out var ratioLimitElement) ? (float)ratioLimitElement.GetDouble() : -2f;
                        var tSeedingTimeLimit = t.TryGetValue("seeding_time_limit", out var seedingTimeLimitElement) ? seedingTimeLimitElement.GetInt64() : -2L;

                        // Sonarr parity: compute CanMoveFiles/CanBeRemoved per-torrent
                        var tIsStopped = state is "pausedUP" or "stoppedUP";
                        var tSeedLimitReached = QBitHasReachedSeedLimit(
                            tRatio, tRatioLimit, seedingTime, tSeedingTimeLimit,
                            qbtGlobalMaxRatioEnabled, qbtGlobalMaxRatio,
                            qbtGlobalMaxSeedingTimeEnabled, qbtGlobalMaxSeedingTime);
                        var tCanBeRemoved = qbtRemoveCompletedDownloads && tSeedLimitReached;
                        var tCanMoveFiles = tCanBeRemoved && tIsStopped;

                        torrentLookup.Add((hash, name, savePath, contentPath, progress, amountLeft, state, size, category, seedingTime, tRatio, tRatioLimit, tSeedingTimeLimit, tCanMoveFiles, tCanBeRemoved));
                    }


                    _logger.LogDebug("Found {TorrentCount} torrents in qBittorrent for client {ClientName}", torrentLookup.Count, client.Name);

                    // Log all torrents for diagnostics
                    foreach (var t in torrentLookup.Take(10))
                    {
                        _logger.LogDebug("qBittorrent torrent: Name={Name}, Hash={Hash}, Progress={Progress:P2}, State={State}, Size={Size}",
                            t.Name, t.Hash, t.Progress, t.State, t.Size);
                    }

                    // For each DB download associated with this client, try to find matching torrent
                    _logger.LogInformation("Checking {DownloadCount} downloads against qBittorrent torrents for client {ClientName}",
                        downloads.Count, client.Name);

                    foreach (var dl in downloads)
                    {
                        try
                        {
                            _logger.LogDebug("Looking for qBittorrent match for download {DownloadId}: {Title}", dl.Id, dl.Title);

                            // Try hash-based matching first (most reliable for qBittorrent)
                            var matched = (Hash: "", Name: "", SavePath: "", ContentPath: "", Progress: 0.0, AmountLeft: 0L, State: "", Size: 0L, Category: "", SeedingTime: (long?)null, Ratio: 0.0, RatioLimit: -2f, SeedingTimeLimit: -2L, CanMoveFiles: false, CanBeRemoved: false);

                            // Check if we have a stored torrent hash for this download
                            if (dl.Metadata != null && dl.Metadata.TryGetValue("TorrentHash", out var hashObj))
                            {
                                var storedHash = hashObj?.ToString();
                                if (!string.IsNullOrEmpty(storedHash))
                                {
                                    matched = torrentLookup.FirstOrDefault(t =>
                                        string.Equals(t.Hash, storedHash, StringComparison.OrdinalIgnoreCase));

                                    if (!string.IsNullOrEmpty(matched.Hash))
                                    {
                                        _logger.LogDebug("Found qBittorrent torrent by hash match: {Hash} for download {DownloadId}", storedHash, dl.Id);
                                    }
                                }
                            }

                            // Fallback to deterministic matching if hash matching failed.
                            // Following Sonarr's pattern: only match on exact identifiers
                            // (name or content path), never on fuzzy title similarity.
                            // Fuzzy matching caused cross-contamination (e.g. importing
                            // "Mr. Mercedes" files into "One Hundred Years of Solitude").
                            if (string.IsNullOrEmpty(matched.Hash))
                            {
                                _logger.LogInformation("Hash matching failed for download {DownloadId}, trying exact name/path matching", dl.Id);

                                // 1. Exact torrent name == download title
                                matched = torrentLookup.FirstOrDefault(t =>
                                    string.Equals(t.Name, dl.Title, StringComparison.OrdinalIgnoreCase));

                                // 2. Exact normalized title match (strip brackets/quality tags only)
                                if (string.IsNullOrEmpty(matched.Hash))
                                {
                                    var dlNorm = TitleUtils.NormalizeTitle(dl.Title);
                                    matched = torrentLookup.FirstOrDefault(t =>
                                        string.Equals(TitleUtils.NormalizeTitle(t.Name), dlNorm, StringComparison.OrdinalIgnoreCase));

                                    if (!string.IsNullOrEmpty(matched.Hash))
                                    {
                                        _logger.LogInformation("Normalized title match: '{DbTitle}' <-> '{TorrentTitle}'", dl.Title, matched.Name);
                                    }
                                }

                                // 3. Exact content path match
                                if (string.IsNullOrEmpty(matched.Hash) && !string.IsNullOrEmpty(dl.DownloadPath))
                                {
                                    var dlPathNorm = Path.GetFullPath(dl.DownloadPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                    matched = torrentLookup.FirstOrDefault(t =>
                                    {
                                        if (string.IsNullOrEmpty(t.ContentPath)) return false;
                                        var contentNorm = Path.GetFullPath(t.ContentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                        return string.Equals(dlPathNorm, contentNorm, StringComparison.OrdinalIgnoreCase);
                                    });
                                }
                            }

                            if (string.IsNullOrEmpty(matched.Hash))
                            {
                                _logger.LogWarning("No matching qBittorrent torrent found for download {DownloadId}: {Title}", dl.Id, dl.Title);
                                continue;
                            }

                            _logger.LogDebug("Found matching qBittorrent torrent for {DownloadId}: {TorrentName} (Hash: {Hash}, State: {State}, Progress: {Progress:P2}, SavePath: {SavePath}, ContentPath: {ContentPath})",
                                dl.Id, matched.Name, matched.Hash, matched.State, matched.Progress, matched.SavePath, matched.ContentPath);

                            // DIAGNOSTIC: Log detailed completion check values
                            _logger.LogInformation("Completion diagnostic for {DownloadId}: Progress={Progress:F4} (>= 1.0? {ProgressCheck}), AmountLeft={AmountLeft} (== 0? {AmountCheck}), State={State}",
                                dl.Id, matched.Progress, matched.Progress >= 1.0, matched.AmountLeft, matched.AmountLeft == 0, matched.State);

                            // Persist client's save/content path to the download (using data from main torrents/info call)
                            // This avoids making individual /properties API calls per download which can overwhelm qBittorrent
                            try
                            {
                                var dbDownload = await downloadRepository.FindAsync(dl.Id);
                                if (dbDownload != null)
                                {
                                    if (!string.IsNullOrEmpty(matched.SavePath) && dbDownload.DownloadPath != matched.SavePath)
                                    {
                                        dbDownload.DownloadPath = matched.SavePath;
                                    }

                                    if (dbDownload.Metadata == null) dbDownload.Metadata = new Dictionary<string, object>();

                                    if (!string.IsNullOrEmpty(matched.ContentPath))
                                    {
                                        dbDownload.Metadata["ClientContentPath"] = matched.ContentPath;
                                    }

                                    if (matched.SeedingTime.HasValue)
                                    {
                                        dbDownload.Metadata["SeedingTimeSeconds"] = matched.SeedingTime.Value;
                                    }

                                    dbDownload.Metadata["CanMoveFiles"] = matched.CanMoveFiles;
                                    dbDownload.Metadata["CanBeRemoved"] = matched.CanBeRemoved;

                                    await downloadRepository.UpdateAsync(dbDownload);
                                    _logger.LogDebug("Persisted client paths for download {DownloadId}: DownloadPath={DownloadPath}, ClientContentPath={ClientContentPath}",
                                        dl.Id, dbDownload.DownloadPath, matched.ContentPath);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to persist client paths for download {DownloadId}", dl.Id);
                            }

                            // Update database with real-time progress information
                            await UpdateDownloadProgressAsync(dl.Id, matched.Progress * 100, matched.AmountLeft, matched.State, downloadRepository, cancellationToken);

                            // Skip finalization/progress logic for downloads that are already
                            // being processed, awaiting import, or fully imported. Re-entering
                            // finalization for these would cause duplicate notifications and
                            // potentially import the wrong files a second time.
                            if (dl.Status == DownloadStatus.Moved ||
                                dl.Status == DownloadStatus.Processing ||
                                dl.Status == DownloadStatus.ImportPending)
                            {
                                _logger.LogDebug("Skipping finalization for {Status} download {DownloadId}", dl.Status, dl.Id);
                                continue;
                            }

                            var normalizedState = (matched.State ?? string.Empty).ToLowerInvariant();
                            if (normalizedState == "error" || normalizedState == "missingfiles")
                            {
                                await HandleFailedDownloadAsync(
                                    dl,
                                    client,
                                    downloadRepository,
                                    appSettings,
                                    $"qBittorrent state: {matched.State}",
                                    cancellationToken);
                                continue;
                            }

                            // Lenient completion detection for qBittorrent
                            // A torrent is complete when progress >= 100% OR amount left is 0
                            // The stability window below ensures we don't immediately import a torrent
                            // that just hit 100% - we wait for the configured delay period
                            var isComplete = matched.Progress >= 1.0 || matched.AmountLeft == 0;

                            _logger.LogDebug("Completion check for {DownloadId}: IsComplete={IsComplete}, Progress={Progress:P2}, AmountLeft={AmountLeft}, State={State}",
                                dl.Id, isComplete, matched.Progress, matched.AmountLeft, matched.State);

                            if (isComplete)
                            {
                                // Determine the best path to use for file discovery
                                // Priority: content_path (actual file/folder) > save_path + name (torrent root) > save_path (download directory)
                                var completionPath = !string.IsNullOrEmpty(matched.ContentPath)
                                    ? matched.ContentPath
                                    : (!string.IsNullOrEmpty(matched.SavePath) && !string.IsNullOrEmpty(matched.Name)
                                        ? CombineWithOptionalBase(matched.SavePath, matched.Name)
                                        : matched.SavePath);

                                _logger.LogInformation("qBittorrent torrent {TorrentName} detected as complete. Using path: {CompletionPath}",
                                    matched.Name, completionPath);

                                // Candidate for completion
                                if (_completionCandidates.TryAdd(dl.Id, DateTime.UtcNow))
                                {
                                    _logger.LogInformation("Download {DownloadId} observed as complete candidate (qBittorrent). Torrent: {TorrentName}, Path: {Path}. Waiting for stability window.",
                                        dl.Id, matched.Name, completionPath);

                                    // Update progress but do NOT set status to Completed yet.
                                    // Setting Completed here races with DownloadProcessingBackgroundService
                                    // which picks up Completed downloads and starts importing before the
                                    // stability window expires. Keep status as Downloading until finalization.
                                    try
                                    {
                                        dl.Progress = 100M;
                                        await downloadRepository.UpdateAsync(dl);
                                        _logger.LogDebug("Updated download {DownloadId} progress to 100%% in database (status remains {Status})", dl.Id, dl.Status);
                                    }
                                    catch (Exception ex2) when (ex2 is not OperationCanceledException && ex2 is not OutOfMemoryException && ex2 is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex2, "Failed to update download {DownloadId} status to Completed", dl.Id);
                                    }

                                    // Broadcast candidate so UI can surface it immediately
                                    _ = BroadcastCandidateUpdateAsync(dl, true, cancellationToken);
                                    continue;
                                }

                                // Use configured stability window if available
                                TimeSpan stableWindow = _completionStableWindow;
                                try
                                {
                                    using var settingsScope = _serviceScopeFactory.CreateScope();
                                    var cfg = settingsScope.ServiceProvider.GetService<IConfigurationService>();
                                    if (cfg != null)
                                    {
                                        var appSettings = await cfg.GetApplicationSettingsAsync();
                                        if (appSettings != null && appSettings.DownloadCompletionStabilitySeconds > 0)
                                        {
                                            stableWindow = TimeSpan.FromSeconds(appSettings.DownloadCompletionStabilitySeconds);
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogDebug(ex, "Failed to read application settings for stability window, falling back to default");
                                }

                                if (_completionCandidates.TryGetValue(dl.Id, out var firstSeen) &&
                                    DateTime.UtcNow - firstSeen >= stableWindow)
                                {
                                    // Finalize: attempt to move/copy files and mark complete
                                    _logger.LogInformation("Download {DownloadId} confirmed complete after stability window (qBittorrent). Torrent: {TorrentName}, Size: {Size:N0} bytes. Finalizing from path: {Path}",
                                        dl.Id, matched.Name, matched.Size, completionPath);
                                    await FinalizeDownloadAsync(dl, completionPath, client, cancellationToken);
                                    _completionCandidates.Remove(dl.Id);
                                }
                                else
                                {
                                    var remainingTime = _completionStableWindow - (DateTime.UtcNow - firstSeen);
                                    _logger.LogDebug("Download {DownloadId} still in stability window, {RemainingSeconds:F1} seconds remaining",
                                        dl.Id, remainingTime.TotalSeconds);
                                }
                            }
                            else
                            {
                                // Not complete anymore - remove candidate if present
                                if (_completionCandidates.Remove(dl.Id))
                                {
                                    _logger.LogDebug("Download {DownloadId} no longer appears complete in qBittorrent, removed from candidates", dl.Id);
                                    _ = BroadcastCandidateUpdateAsync(dl, false, cancellationToken);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Error processing download {DownloadId} while polling qBittorrent", dl.Id);
                        }
                    }

                    // Schedule next poll now that this client's polling completed successfully
                    _ = ScheduleNextClientPollOnSuccess(client.Id, downloads.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Error polling qBittorrent client {ClientName}", client.Name);
                    ScheduleNextClientPollOnFailure(client.Id);
                }
            }, cancellationToken);
        }

        private Task PollTransmissionAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            IDownloadRepository downloadRepository,
            ApplicationSettings appSettings,
            CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                _logger.LogInformation("Polling Transmission client {ClientName} for {Count} downloads", client.Name, downloads.Count);
                try
                {
                    var now = DateTime.UtcNow;

                    // Respect per-client poll schedules to avoid overloading Transmission
                    if (_nextClientPoll.TryGetValue(client.Id, out var scheduled) && now < scheduled)
                    {
                        _logger.LogDebug("Skipping Transmission poll for {ClientName}, next scheduled at {Next}", client.Name, scheduled);
                        _logger.LogInformation("PollTransmission early-return: scheduled skip for client {ClientName} at {Next}", client.Name, scheduled);
                        return;
                    }

                    var rpcPath = "/transmission/rpc";
                    if (client.Settings?.TryGetValue("urlBase", out var urlBaseObj) is true)
                    {
                        var custom = urlBaseObj?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(custom))
                        {
                            rpcPath = custom.StartsWith('/') ? custom : "/" + custom;
                        }
                    }
                    var baseUrl = DownloadClientUriBuilder.BuildUri(client, rpcPath).ToString();
                    using var http = _httpClientFactory.CreateClient("DownloadClient");

                    // Resolve removeCompletedDownloads for CanMoveFiles/CanBeRemoved evaluation
                    bool txRemoveCompletedDownloads = !string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                        client.RemoveCompletedDownloads != "none";

                    // Prepare RPC payload for torrent-get (includes seed limit fields for Sonarr parity)
                    var rpc = new
                    {
                        method = "torrent-get",
                        arguments = new
                        {
                            fields = new[] { "id", "hashString", "name", "percentDone", "leftUntilDone", "isFinished", "status", "downloadDir",
                                "uploadRatio", "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit", "secondsSeeding" }
                        },
                        tag = 4
                    };

                    var serializedPayload = System.Text.Json.JsonSerializer.Serialize(rpc, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    string? sessionId = null;

                    _logger.LogDebug("PollTransmission RPC request to {BaseUrl}", baseUrl);

                    // Transmission CSRF protection: first request gets 409 with session-id, retry with that session-id
                    // This mirrors TransmissionAdapter.InvokeRpcAsync pattern
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
                        {
                            Content = new StringContent(serializedPayload, System.Text.Encoding.UTF8, "application/json")
                        };

                        // Add session-id header if we have one (from previous 409 retry)
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            request.Headers.Add("X-Transmission-Session-Id", sessionId);
                            _logger.LogDebug("PollTransmission using X-Transmission-Session-Id: {SessionId}", sessionId);
                        }

                        // Add Basic auth header if configured
                        if (!string.IsNullOrWhiteSpace(client.Username))
                        {
                            var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                        }

                        var resp = await http.SendAsync(request, cancellationToken);
                        var respText = await resp.Content.ReadAsStringAsync(cancellationToken);

                        // Handle 409 Conflict (CSRF session-id flow)
                        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && attempt == 0)
                        {
                            if (resp.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
                            {
                                sessionId = values.FirstOrDefault();
                                _logger.LogDebug("PollTransmission received 409 Conflict, retrying with session-id: {SessionId}", sessionId);
                                continue; // Retry with session-id
                            }
                        }

                        // Check for success
                        _logger.LogInformation("PollTransmission HTTP response: {StatusCode}", resp.StatusCode);
                        if (!resp.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("PollTransmission failed with status {StatusCode}", resp.StatusCode);
                            _logger.LogInformation("PollTransmission early-return: non-success HTTP status {StatusCode} from {BaseUrl} for client {ClientName}", resp.StatusCode, baseUrl, client.Name);
                            return;
                        }

                        // Process successful response
                        _logger.LogDebug("PollTransmission response text length: {Length}", respText?.Length ?? 0);
                        if (string.IsNullOrWhiteSpace(respText))
                        {
                            _logger.LogInformation("PollTransmission early-return: empty response content for client {ClientName}", client.Name);
                            return;
                        }

                        // Parse response and continue with torrent processing
                        System.Text.Json.JsonElement doc;
                        try
                        {
                            doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(respText)!;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "PollTransmission failed to parse JSON response for client {ClientName}", client.Name);
                            _logger.LogInformation("PollTransmission early-return: invalid JSON response from client {ClientName}", client.Name);
                            return;
                        }

                        if (!doc.TryGetProperty("arguments", out var args))
                        {
                            _logger.LogWarning("PollTransmission response missing 'arguments' property");
                            _logger.LogInformation("PollTransmission early-return: missing 'arguments' in response for client {ClientName}", client.Name);
                            return;
                        }
                        if (!args.TryGetProperty("torrents", out var torrents))
                        {
                            _logger.LogWarning("PollTransmission response missing 'torrents' property");
                            _logger.LogInformation("PollTransmission early-return: missing 'torrents' in 'arguments' for client {ClientName}", client.Name);
                            return;
                        }
                        if (torrents.ValueKind != System.Text.Json.JsonValueKind.Array)
                        {
                            _logger.LogWarning("PollTransmission 'torrents' is not an array: {Kind}", torrents.ValueKind);
                            _logger.LogInformation("PollTransmission early-return: 'torrents' not an array (Kind={Kind}) for client {ClientName}", torrents.ValueKind, client.Name);
                            return;
                        }
                        _logger.LogInformation("PollTransmission found {Count} torrents in response", torrents.GetArrayLength());

                        // Fetch session config for seed limit evaluation (Sonarr parity)
                        bool txSessionSeedRatioLimited = false;
                        double txSessionSeedRatioLimit = 0;
                        bool txSessionIdleSeedingLimitEnabled = false;
                        int txSessionIdleSeedingLimit = 0;
                        try
                        {
                            var sessionPayload = System.Text.Json.JsonSerializer.Serialize(new { method = "session-get", arguments = new { }, tag = 99 }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                            using var sessionReq = new HttpRequestMessage(HttpMethod.Post, baseUrl)
                            {
                                Content = new StringContent(sessionPayload, System.Text.Encoding.UTF8, "application/json")
                            };
                            if (!string.IsNullOrEmpty(sessionId))
                                sessionReq.Headers.Add("X-Transmission-Session-Id", sessionId);
                            if (!string.IsNullOrWhiteSpace(client.Username))
                            {
                                var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
                                sessionReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
                            }
                            using var sessionResp = await http.SendAsync(sessionReq, cancellationToken);
                            if (sessionResp.IsSuccessStatusCode)
                            {
                                var sessionText = await sessionResp.Content.ReadAsStringAsync(cancellationToken);
                                var sessionDoc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(sessionText);
                                if (sessionDoc.TryGetProperty("arguments", out var sessionArgs))
                                {
                                    txSessionSeedRatioLimited = (sessionArgs.TryGetProperty("seedRatioLimited", out var srl) || sessionArgs.TryGetProperty("seed_ratio_limited", out srl)) && srl.GetBoolean();
                                    txSessionSeedRatioLimit = (sessionArgs.TryGetProperty("seedRatioLimit", out var srlv) || sessionArgs.TryGetProperty("seed_ratio_limit", out srlv)) ? srlv.GetDouble() : 0;
                                    txSessionIdleSeedingLimitEnabled = (sessionArgs.TryGetProperty("idle-seeding-limit-enabled", out var isle) || sessionArgs.TryGetProperty("idle_seeding_limit_enabled", out isle)) && isle.GetBoolean();
                                    txSessionIdleSeedingLimit = (sessionArgs.TryGetProperty("idle-seeding-limit", out var isl) || sessionArgs.TryGetProperty("idle_seeding_limit", out isl)) ? isl.GetInt32() : 0;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Failed to fetch Transmission session config for seed limit evaluation");
                        }

                        // Process torrents (continue with existing logic below)
                        foreach (var dl in downloads)
                        {
                            try
                            {
                                // Attempt to match by hashString (preferred) or name
                                var matching = torrents.EnumerateArray().FirstOrDefault(t =>
                                {
                                    // First try matching by hash (most reliable)
                                    if (dl.Metadata != null && dl.Metadata.TryGetValue("TorrentHash", out var hashObj))
                                    {
                                        var downloadHash = hashObj?.ToString() ?? string.Empty;
                                        if (!string.IsNullOrEmpty(downloadHash))
                                        {
                                            var hash = t.TryGetProperty("hashString", out var h) ? h.GetString() ?? string.Empty : string.Empty;
                                            if (string.Equals(hash, downloadHash, StringComparison.OrdinalIgnoreCase))
                                                return true;
                                        }
                                    }

                                    // Fallback to exact name or normalized title match only.
                                    // No fuzzy/path-based matching to avoid cross-contamination.
                                    var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                                    if (string.Equals(name, dl.Title, StringComparison.OrdinalIgnoreCase))
                                        return true;
                                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dl.Title) &&
                                        string.Equals(TitleUtils.NormalizeTitle(name), TitleUtils.NormalizeTitle(dl.Title), StringComparison.OrdinalIgnoreCase))
                                        return true;
                                    return false;
                                });

                                if (matching.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                                {
                                    _logger.LogDebug("Could not find matching torrent for download {DownloadId} ({Title}) in Transmission", dl.Id, dl.Title);
                                    continue;
                                }

                                _logger.LogDebug("Matched download {DownloadId} to Transmission torrent", dl.Id);

                                var percent = matching.TryGetProperty("percentDone", out var p) ? p.GetDouble() : 0.0;
                                var left = matching.TryGetProperty("leftUntilDone", out var l) ? l.GetInt64() : 0L;
                                var statusCode = matching.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : 0;

                                // Map Transmission status code to status string (same as TransmissionAdapter)
                                var status = statusCode switch
                                {
                                    0 => "paused",          // TR_STATUS_STOPPED
                                    1 => "queued",          // TR_STATUS_CHECK_WAIT
                                    2 => "downloading",     // TR_STATUS_CHECK
                                    3 => "queued",          // TR_STATUS_DOWNLOAD_WAIT
                                    4 => "downloading",     // TR_STATUS_DOWNLOAD
                                    5 => "queued",          // TR_STATUS_SEED_WAIT
                                    6 => "seeding",         // TR_STATUS_SEED
                                    7 => "failed",          // TR_STATUS_ISOLATED
                                    _ => "unknown"
                                };

                                // Update database with real-time progress information
                                await UpdateDownloadProgressAsync(dl.Id, percent * 100, left, status, downloadRepository, cancellationToken);

                                // Compute and persist CanMoveFiles/CanBeRemoved (Sonarr parity)
                                try
                                {
                                    var txUploadRatio = (matching.TryGetProperty("uploadRatio", out var txRatP) || matching.TryGetProperty("upload_ratio", out txRatP)) ? txRatP.GetDouble() : 0d;
                                    var txSeedRatioMode = (matching.TryGetProperty("seedRatioMode", out var txSrmP) || matching.TryGetProperty("seed_ratio_mode", out txSrmP)) ? txSrmP.GetInt32() : 0;
                                    var txSeedRatioLimit = (matching.TryGetProperty("seedRatioLimit", out var txSrlP) || matching.TryGetProperty("seed_ratio_limit", out txSrlP)) ? txSrlP.GetDouble() : 0d;
                                    var txSeedIdleMode = (matching.TryGetProperty("seedIdleMode", out var txSimP) || matching.TryGetProperty("seed_idle_mode", out txSimP)) ? txSimP.GetInt32() : 0;
                                    var txSeedIdleLimit = (matching.TryGetProperty("seedIdleLimit", out var txSilP) || matching.TryGetProperty("seed_idle_limit", out txSilP)) ? txSilP.GetInt32() : 0;
                                    var txSecondsSeeding = (matching.TryGetProperty("secondsSeeding", out var txSsP) || matching.TryGetProperty("seconds_seeding", out txSsP)) ? txSsP.GetInt64() : 0L;

                                    var txIsStopped = statusCode == 0;
                                    var txIsSeeding = statusCode == 6;
                                    var txSeedLimitReached = TransmissionHasReachedSeedLimit(
                                        txIsStopped, txIsSeeding, txUploadRatio,
                                        txSeedRatioMode, txSeedRatioLimit,
                                        txSeedIdleMode, txSeedIdleLimit, txSecondsSeeding,
                                        txSessionSeedRatioLimited, txSessionSeedRatioLimit,
                                        txSessionIdleSeedingLimitEnabled, txSessionIdleSeedingLimit);
                                    var txCanBeRemoved = txRemoveCompletedDownloads && txSeedLimitReached;
                                    var txCanMoveFiles = txCanBeRemoved && txIsStopped;

                                    var txDbDownload = await downloadRepository.FindAsync(dl.Id);
                                    if (txDbDownload != null)
                                    {
                                        if (txDbDownload.Metadata == null) txDbDownload.Metadata = new Dictionary<string, object>();
                                        txDbDownload.Metadata["CanMoveFiles"] = txCanMoveFiles;
                                        txDbDownload.Metadata["CanBeRemoved"] = txCanBeRemoved;
                                        await downloadRepository.UpdateAsync(txDbDownload);
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogDebug(ex, "Failed to persist CanMoveFiles/CanBeRemoved for Transmission download {DownloadId}", dl.Id);
                                }

                                // Skip finalization/progress logic for downloads that are already
                                // being processed, awaiting import, or fully imported.
                                if (dl.Status == DownloadStatus.Moved ||
                                    dl.Status == DownloadStatus.Processing ||
                                    dl.Status == DownloadStatus.ImportPending)
                                {
                                    _logger.LogDebug("Skipping finalization for {Status} download {DownloadId}", dl.Status, dl.Id);
                                    continue;
                                }

                                if (status == "failed")
                                {
                                    await HandleFailedDownloadAsync(
                                        dl,
                                        client,
                                        downloadRepository,
                                        appSettings,
                                        "Transmission reported failed state",
                                        cancellationToken);
                                    continue;
                                }

                                // Check for completion using same logic as TransmissionAdapter
                                var isComplete = percent >= 1.0 && (status == "seeding" || status == "queued" || status == "paused");
                                _logger.LogInformation("PollTransmission download {DownloadId}: percent={Percent}, status={Status}, isComplete={IsComplete}", dl.Id, percent, status, isComplete);

                                if (isComplete)
                                {
                                    if (_completionCandidates.TryAdd(dl.Id, DateTime.UtcNow))
                                    {
                                        _logger.LogInformation("Download {DownloadId} observed complete candidate (Transmission). Waiting for stability window.", dl.Id);
                                        _ = BroadcastCandidateUpdateAsync(dl, true, cancellationToken);
                                        continue;
                                    }

                                    if (_completionCandidates.TryGetValue(dl.Id, out var firstSeen) &&
                                        DateTime.UtcNow - firstSeen >= _completionStableWindow)
                                    {
                                        // Build the full content path: downloadDir/name
                                        // Using just downloadDir would scan the entire download folder
                                        // and pick up unrelated files (.wv, etc.) from other downloads.
                                        var downloadDir = matching.TryGetProperty("downloadDir", out var dprop) ? dprop.GetString() ?? string.Empty : string.Empty;
                                        var torrentName = matching.TryGetProperty("name", out var nprop) ? nprop.GetString() ?? string.Empty : string.Empty;
                                        var contentPath = !string.IsNullOrEmpty(torrentName)
                                            ? CombineWithOptionalBase(downloadDir, torrentName)
                                            : downloadDir;
                                        _logger.LogInformation("Download {DownloadId} confirmed complete after stability window (Transmission). Finalizing from path: {ContentPath}", dl.Id, contentPath);
                                        await FinalizeDownloadAsync(dl, contentPath, client, cancellationToken);
                                        _completionCandidates.Remove(dl.Id);
                                    }
                                }
                                else
                                {
                                    if (_completionCandidates.Remove(dl.Id))
                                    {
                                        _ = BroadcastCandidateUpdateAsync(dl, false, cancellationToken);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Error processing download {DownloadId} while polling Transmission", dl.Id);
                            }
                        }

                        // Schedule next poll now that this client's polling completed successfully
                        _ = ScheduleNextClientPollOnSuccess(client.Id, downloads.Count);
                        return; // Successfully processed
                    }

                    // If we reach here, session-id flow failed after retries
                    _logger.LogWarning("PollTransmission failed to establish session after retries for client {ClientName}", client.Name);

                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Error polling Transmission client {ClientName}", client.Name);
                    ScheduleNextClientPollOnFailure(client.Id);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Attempt to finalize a download after it is observed complete on the client.
        /// This will try to locate the downloaded file(s) under clientPath and move or copy
        /// the best candidate to the final destination determined by the file naming service
        /// or settings.OutputPath.
        /// </summary>
        private async Task FinalizeDownloadAsync(Download download, string clientPath, DownloadClientConfiguration client, CancellationToken cancellationToken)
        {
            try
            {
                // Re-check whether the client is still enabled (it may have been disabled
                // since the polling loop started or since a retry was scheduled).
                using var preScope = _serviceScopeFactory.CreateScope();
                var preConfigService = preScope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var freshClient = await preConfigService.GetDownloadClientConfigurationAsync(client.Id);
                if (freshClient != null && !freshClient.IsEnabled)
                {
                    _logger.LogInformation(
                        "Skipping finalization for download {DownloadId} ({Title}): download client {ClientName} is disabled",
                        download.Id, download.Title, client.Name);
                    return;
                }

                _logger.LogInformation("Starting download finalization for {DownloadId}: {Title} from client {ClientName}",
                    download.Id, download.Title, client.Name);
                _logger.LogDebug("Initial client path: {ClientPath}", clientPath);

                using var scope = _serviceScopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var fileNaming = scope.ServiceProvider.GetService<IFileNamingService>();
                var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();

                // Check if this download is already being processed by the background service
                var queueService = scope.ServiceProvider.GetService<IDownloadProcessingQueueService>();
                if (queueService != null)
                {
                    var existingJobs = await queueService.GetJobsForDownloadAsync(download.Id);
                    var activeJobs = existingJobs?.Where(j => j.Status == ProcessingJobStatus.Pending ||
                                                             j.Status == ProcessingJobStatus.Processing ||
                                                             j.Status == ProcessingJobStatus.Retry).ToList();

                    if (activeJobs != null && activeJobs.Any())
                    {
                        _logger.LogInformation("Download {DownloadId} is already being processed by background service (job {JobId}), skipping duplicate finalization",
                            download.Id, activeJobs.First().Id);
                        return;
                    }

                    // Also check if download has already been moved/processed
                    if (download.Status == DownloadStatus.Moved)
                    {
                        _logger.LogInformation("Download {DownloadId} has already been processed (status: Moved), skipping duplicate finalization", download.Id);
                        return;
                    }
                }

                // Check idempotency: prevent re-importing downloads that were already successfully imported
                var historyService = scope.ServiceProvider.GetService<IDownloadHistoryService>();
                if (historyService != null && !string.IsNullOrEmpty(download.DownloadClientId))
                {
                    var alreadyImported = await historyService.IsAlreadyImportedAsync(download.Id, download.DownloadClientId);
                    if (alreadyImported)
                    {
                        _logger.LogInformation("Download {DownloadId} ({Title}) was already imported - idempotency check prevented re-import from client {ClientId}",
                            download.Id, download.Title, download.DownloadClientId);
                        download.Status = DownloadStatus.Moved;
                        await downloadRepository.UpdateAsync(download);
                        return;
                    }
                }

                var settings = await configService.GetApplicationSettingsAsync();

                // When OutputPath is not configured, fall back to the first root folder path
                if (string.IsNullOrWhiteSpace(settings.OutputPath))
                {
                    var rootFolderService = scope.ServiceProvider.GetService<IRootFolderService>();
                    if (rootFolderService != null)
                    {
                        var rootFolders = await rootFolderService.GetAllAsync();
                        if (rootFolders.Count > 0)
                        {
                            settings.OutputPath = rootFolders[0].Path;
                            _logger.LogInformation("OutputPath not configured, using first root folder: {OutputPath}", settings.OutputPath);
                        }
                    }
                }

                _logger.LogDebug("Application settings: OutputPath='{OutputPath}', EnableMetadataProcessing={EnableMetadata}, CompletedFileAction={Action}",
                    settings.OutputPath, settings.EnableMetadataProcessing, settings.CompletedFileAction);

                // V2 Pattern: Use ImportItemResolutionService to get accurate path from download client
                var importResolver = scope.ServiceProvider.GetService<IImportItemResolutionService>();
                if (importResolver == null)
                {
                    _logger.LogError("ImportItemResolutionService not available for download {DownloadId}", download.Id);
                    return;
                }

                // Build a preliminary QueueItem from what we know
                var preliminaryItem = new QueueItem
                {
                    Id = download.Id,
                    Title = download.Title ?? "Unknown",
                    Status = "completed",
                    ContentPath = clientPath,
                    DownloadClientId = client.Id
                };

                // Resolve the accurate import path via the download client adapter
                QueueItem resolvedItem;
                try
                {
                    resolvedItem = await importResolver.ResolveImportItemAsync(
                        download,
                        preliminaryItem,
                        previousAttempt: null,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to resolve import item for download {DownloadId}, using fallback path", download.Id);
                    resolvedItem = preliminaryItem;
                }

                var sourceFile = resolvedItem.ContentPath ?? string.Empty;
                _logger.LogInformation("Resolved import path for download {DownloadId}: {SourcePath}", download.Id, sourceFile);

                // If the source is empty OR neither a file nor a directory exists at the path,
                // treat it as a missing source. We need to consider directories valid here because
                // a multi-file download is represented by the directory path.
                if (string.IsNullOrEmpty(sourceFile) || (!File.Exists(sourceFile) && !Directory.Exists(sourceFile)))
                {
                    // If the background processing queue already has an active job for this
                    // download, it's likely a race: the file is being moved/processed by the
                    // background worker. Avoid logging a noisy error and let the background
                    // worker finish. Only surface an error if there is no active processing job.
                    try
                    {
                        var processingQueue = scope.ServiceProvider.GetService<IDownloadProcessingQueueService>();
                        if (processingQueue != null)
                        {
                            var jobs = await processingQueue.GetJobsForDownloadAsync(download.Id);
                            if (jobs != null && jobs.Any(j => j.Status == ProcessingJobStatus.Pending || j.Status == ProcessingJobStatus.Processing || j.Status == ProcessingJobStatus.Retry))
                            {
                                _logger.LogDebug("Download {DownloadId} appears to be currently processed by the background queue - skipping missing-source check", download.Id);
                                return;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        // Failing this diagnostic lookup shouldn't hide the underlying problem - fall through and log the error
                        _logger.LogDebug(ex, "Error while checking processing queue for download {DownloadId}", download.Id);
                    }

                    // If we get here and no processing job is active, it's likely the files are not yet
                    // present (extraction/unpack not finished). Rather than immediately erroring out
                    // we schedule a bounded retry/backoff so transient delays are handled gracefully.
                    int attempts = 0;
                    int maxRetries = 3;
                    int initialDelay = 30;

                    try
                    {
                        var appSettings = await configService.GetApplicationSettingsAsync();
                        if (appSettings != null)
                        {
                            maxRetries = Math.Max(0, appSettings.MissingSourceMaxRetries);
                            initialDelay = Math.Max(1, appSettings.MissingSourceRetryInitialDelaySeconds);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to read application settings for missing-source retry, falling back to defaults");
                    }

                    // Read or initialize attempt count
                    attempts = _missingSourceRetryAttempts.GetOrAdd(download.Id, 0);

                    if (attempts >= maxRetries)
                    {
                        _logger.LogError("Unable to locate source file for download {DownloadId} after {Attempts} attempts. Resolved path: {SourcePath}, FinalPath={FinalPath}, DownloadPath={DownloadPath}",
                            download.Id, attempts, sourceFile, download.FinalPath, download.DownloadPath);
                        try { _metrics.Increment("finalize.failed.file_not_found"); }
                        catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                        try { _metrics.Increment("finalize.retry.exhausted"); }
                        catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                        // Reset retry tracking if we have exhausted attempts
                        _missingSourceRetryAttempts.TryRemove(download.Id, out _);
                        _missingSourceRetryScheduled.TryRemove(download.Id, out _);
                        return;
                    }

                    // Ensure we only schedule one retry task per download at a time
                    var scheduled = _missingSourceRetryScheduled.GetOrAdd(download.Id, false);
                    if (scheduled)
                    {
                        _logger.LogDebug("Retry already scheduled for download {DownloadId}, skipping duplicate schedule", download.Id);
                        return;
                    }

                    // Mark as scheduled and increment attempt counter
                    _missingSourceRetryScheduled[download.Id] = true;
                    _missingSourceRetryAttempts.AddOrUpdate(download.Id, 1, (k, v) => v + 1);

                    // Compute exponential backoff delay
                    var currentAttempt = _missingSourceRetryAttempts[download.Id];
                    var delaySeconds = initialDelay * (int)Math.Pow(2, Math.Max(0, currentAttempt - 1));
                    _logger.LogInformation("Source not found for download {DownloadId}. Scheduling retry #{Attempt} in {Delay}s (resolved path: {SourcePath})", download.Id, currentAttempt, delaySeconds, sourceFile);

                    try { _metrics.Increment("finalize.retry.scheduled"); }
                    catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }

                    // Fire-and-forget retry task. Use a safe small delay and then attempt finalize again.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                            // Attempt finalization again; do not pass the original cancellation token to avoid accidental cancellation
                            await FinalizeDownloadAsync(download, clientPath, client, CancellationToken.None);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Scheduled retry for download {DownloadId} failed", download.Id);
                            try { _metrics.Increment("finalize.retry.scheduled.failed"); }
                            catch (Exception caughtEx_7) when (caughtEx_7 is not OperationCanceledException && caughtEx_7 is not OutOfMemoryException && caughtEx_7 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                        finally
                        {
                            _missingSourceRetryScheduled.TryRemove(download.Id, out _);
                        }
                    });

                    return;
                }

                // If we had scheduled attempts previously, count this as a retry-success
                try
                {
                    if (_missingSourceRetryAttempts.TryGetValue(download.Id, out var prevAttempts) && prevAttempts > 0)
                    {
                        try { _metrics.Increment("finalize.retry.success"); }
                        catch (Exception caughtEx_8) when (caughtEx_8 is not OperationCanceledException && caughtEx_8 is not OutOfMemoryException && caughtEx_8 is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }
                }
                catch (Exception caughtEx_9) when (caughtEx_9 is not OperationCanceledException && caughtEx_9 is not OutOfMemoryException && caughtEx_9 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                // Clear any retry tracking since we've located the file successfully
                _missingSourceRetryAttempts.TryRemove(download.Id, out _);
                _missingSourceRetryScheduled.TryRemove(download.Id, out _);

                // If the source is a directory (multi-file release) we don't try to read
                // file-specific properties like Length. Log a directory-specific message.
                if (Directory.Exists(sourceFile))
                {
                    _logger.LogInformation("Source directory located (multi-file release): {SourceDir}", sourceFile);
                }
                else
                {
                    var sourceFileInfo = new FileInfo(sourceFile);
                    _logger.LogInformation("Source file located: {SourceFile} ({Size:N0} bytes)", sourceFile, sourceFileInfo.Length);
                }

                // Determine destination path
                string destinationPath = string.Empty;
                try
                {
                    if (!string.IsNullOrEmpty(sourceFile) && Directory.Exists(sourceFile))
                    {
                        // When the source is a directory we try to determine the final
                        // audiobook folder under the configured OutputPath (the library).
                        // Prefer using the FileNamingService so naming patterns and
                        // subdirectory rules are respected; fall back to simple dirName.
                        var dirName = Path.GetFileName(sourceFile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "import";
                        var outRoot = settings.OutputPath;
                        if (string.IsNullOrWhiteSpace(outRoot))
                        {
                            outRoot = "./completed";
                            _logger.LogDebug("No output path configured, using default: {OutputRoot}", outRoot);
                        }

                        // For multi-file directories use a predictable folder under OutputPath
                        // instead of relying on FileNamingService which may create author-based
                        // subfolders (e.g. 'Unknown Author') in unexpected roots.
                        try
                        {
                            // Build destination using OutputPath/Author[/Series]/Title semantics
                            destinationPath = FinalizePathHelper.BuildMultiFileDestination(settings, download, dirName);
                            _logger.LogDebug("Computed directory destination for multi-file release: {DestinationPath}", destinationPath);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to compute destination folder for multi-file download, falling back to simple OutputPath destination");
                            destinationPath = Path.Join(outRoot, dirName);
                        }
                    }
                    else if (fileNaming != null)
                    {
                        _logger.LogDebug("Using file naming service to generate destination path");

                        // Always use file naming service for consistent naming
                        AudioMetadata metadata = new AudioMetadata { Title = download.Title ?? "Unknown Title" };

                        if (settings.EnableMetadataProcessing)
                        {
                            // TEMPORARY: Skip ffprobe/ffmpeg metadata extraction during finalization/import.
                            // Calling ffprobe here has been causing noisy Win32Exception logs in test environments
                            // and can be deferred to the background import/metadata processing stage. Use the
                            // download info (title) for naming now and let background processors enrich metadata.
                            _logger.LogInformation("Temporarily skipping ffprobe metadata extraction during finalization for download {DownloadId}", download.Id);
                        }
                        else
                        {
                            _logger.LogDebug("Metadata processing disabled, using download info for naming");
                        }

                        var ext = Path.GetExtension(sourceFile);
                        var generatedPath = await fileNaming.GenerateFilePathAsync(metadata, ext);

                        // Ensure the file goes directly to OutputPath (root folder) without subdirectories
                        var outRoot = settings.OutputPath;
                        if (string.IsNullOrWhiteSpace(outRoot))
                        {
                            outRoot = "./completed";
                            _logger.LogDebug("No output path configured, using default: {OutputRoot}", outRoot);
                        }

                        // Extract just the filename from the generated path (ignore any directories)
                        var generatedFileName = Path.GetFileName(generatedPath);
                        destinationPath = Path.Join(outRoot, generatedFileName);

                        _logger.LogInformation("Generated destination path: {DestinationPath}", destinationPath);
                    }
                    else
                    {
                        _logger.LogWarning("File naming service not available, using simple naming");

                        var outRoot = settings.OutputPath;
                        if (string.IsNullOrWhiteSpace(outRoot))
                        {
                            outRoot = "./completed";
                            _logger.LogDebug("No output path configured, using default: {OutputRoot}", outRoot);
                        }

                        var fileName = Path.GetFileName(sourceFile);
                        destinationPath = Path.Join(outRoot, fileName);
                        _logger.LogInformation("Generated simple destination path: {DestinationPath}", destinationPath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to generate destination path for download {DownloadId}", download.Id);

                    // Fallback to simple path in output directory
                    var outRoot = settings.OutputPath;
                    if (string.IsNullOrWhiteSpace(outRoot))
                    {
                        outRoot = "./completed";
                    }

                    var fallbackFileName = Path.GetFileName(sourceFile);
                    destinationPath = Path.Join(outRoot, fallbackFileName);
                    _logger.LogWarning("Using fallback destination path: {DestinationPath}", destinationPath);
                }

                // Ensure destination directory exists
                try
                {
                    var destDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                        _logger.LogDebug("Created destination directory: {Directory}", destDir);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to create destination directory for {DestinationPath}", destinationPath);
                    return;
                }

                // Before enqueueing, mark the download as observed complete and persist client path info
                try
                {
                    var dbDownload = await downloadRepository.FindAsync(download.Id);
                    if (dbDownload != null)
                    {
                        // Ensure DownloadPath contains the resolved source path
                        if (!string.IsNullOrEmpty(sourceFile) && dbDownload.DownloadPath != sourceFile)
                        {
                            dbDownload.DownloadPath = sourceFile;
                        }

                        dbDownload.Status = DownloadStatus.Processing;

                        await downloadRepository.UpdateAsync(dbDownload);

                        _logger.LogInformation("Marked download {DownloadId} as Completed (observed) and persisted DownloadPath: {DownloadPath}", download.Id, dbDownload.DownloadPath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to persist observed completion for download {DownloadId}", download.Id);
                }

                // Enqueue download processing job and let the processing pipeline handle moving/renaming
                try
                {
                    var processingQueueService = scope.ServiceProvider.GetService<IDownloadProcessingQueueService>();
                    if (processingQueueService != null)
                    {
                        await processingQueueService.QueueDownloadProcessingAsync(download.Id, sourceFile, client.Id);
                        _logger.LogInformation("Enqueued download {DownloadId} for processing: {Source}", download.Id, sourceFile);
                    }
                    else
                    {
                        _logger.LogWarning("Download processing queue service not available; skipping enqueue for download {DownloadId}", download.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to enqueue download {DownloadId} for processing: {Source}", download.Id, sourceFile);
                    return;
                }

                // Finalization step: processing work will update DB and broadcast when the processing job runs
                _logger.LogDebug("Download {DownloadId} enqueued for processing; final DB update will occur during processing", download.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "FinalizeDownloadAsync failed for download {DownloadId}: {Title}", download.Id, download.Title);
            }
        }

        private Task PollSABnzbdAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            IDownloadRepository downloadRepository,
            ApplicationSettings appSettings,
            CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                _logger.LogDebug("Polling SABnzbd client {ClientName}", client.Name);
                try
                {
                    var now = DateTime.UtcNow;

                    // Respect per-client poll schedules to avoid overloading SABnzbd
                    if (_nextClientPoll.TryGetValue(client.Id, out var scheduled) && now < scheduled)
                    {
                        _logger.LogDebug("Skipping SABnzbd poll for {ClientName}, next scheduled at {Next}", client.Name, scheduled);
                        return;
                    }

                    var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();

                    using var http = _httpClientFactory.CreateClient("DownloadClient");

                    // Get API key from settings
                    var apiKey = "";
                    if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                    {
                        apiKey = apiKeyObj?.ToString() ?? "";
                    }

                    if (string.IsNullOrEmpty(apiKey))
                    {
                        _logger.LogWarning("SABnzbd API key not configured for client {ClientName}", client.Name);
                        return;
                    }

                    // Poll SABnzbd queue for active downloads progress updates
                    var queueUrl = $"{baseUrl}?mode=queue&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                    // Redacted queue URL for safe diagnostics
                    _logger.LogDebug("SABnzbd poll queue URL (redacted): {Url}", LogRedaction.RedactText(queueUrl, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { apiKey })));
                    using var queueResponse = await http.GetAsync(queueUrl, cancellationToken);

                    if (queueResponse.IsSuccessStatusCode)
                    {
                        var queueJson = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
                        var queueDoc = System.Text.Json.JsonDocument.Parse(queueJson);

                        if (queueDoc.RootElement.TryGetProperty("queue", out var queue) &&
                            queue.TryGetProperty("slots", out var queueSlots) &&
                            queueSlots.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var slot in queueSlots.EnumerateArray())
                            {
                                try
                                {
                                    var nzoId = slot.TryGetProperty("nzo_id", out var nzoIdProp) ? nzoIdProp.GetString() ?? "" : "";
                                    var filename = slot.TryGetProperty("filename", out var filenameProp) ? filenameProp.GetString() ?? "" : "";
                                    // SABnzbd sometimes returns numeric values as numbers or strings.
                                    // Be defensive and accept either JSON number or JSON string.
                                    double GetDoubleValue(System.Text.Json.JsonElement el)
                                    {
                                        try
                                        {
                                            if (el.ValueKind == System.Text.Json.JsonValueKind.Number)
                                                return el.GetDouble();

                                            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                                            {
                                                var s = el.GetString();
                                                if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
                                                    return v;
                                            }
                                        }
                                        catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException)
                                        {
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }

                                        return 0.0;
                                    }

                                    var percentage = slot.TryGetProperty("percentage", out var percentageProp) ? GetDoubleValue(percentageProp) : 0.0;
                                    var mbleft = slot.TryGetProperty("mbleft", out var mbleftProp) ? GetDoubleValue(mbleftProp) : 0.0;
                                    var status = slot.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                                    // Find matching download by NZO ID
                                    var matchingDownload = downloads.FirstOrDefault(dl =>
                                    {
                                        var clientItemId = GetClientItemId(dl);
                                        return !string.IsNullOrEmpty(clientItemId) &&
                                               clientItemId.Equals(nzoId, StringComparison.OrdinalIgnoreCase);
                                    });

                                    if (matchingDownload == null && !string.IsNullOrEmpty(filename))
                                    {
                                        matchingDownload = downloads.FirstOrDefault(dl => TitleUtils.AreTitlesSimilar(dl.Title, filename));
                                    }

                                    if (matchingDownload != null)
                                    {
                                        // Calculate progress and update
                                        // percentage is provided by SABnzbd as a percent (e.g. 50.0). Our UpdateDownloadProgressAsync
                                        // expects a percentage in the 0..100 range. Use the percentage directly.
                                        var progressPercent = percentage; // 0..100

                                        // Convert sizes from MB -> bytes
                                        var amountLeft = (long)(mbleft * 1024 * 1024);

                                        // Update progress using percent and amountLeft (UpdateDownloadProgressAsync uses percent->downloaded size calculation when TotalSize is set)
                                        await UpdateDownloadProgressAsync(matchingDownload.Id, progressPercent, amountLeft, status, downloadRepository, cancellationToken);

                                        if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                                        {
                                            await HandleFailedDownloadAsync(
                                                matchingDownload,
                                                client,
                                                downloadRepository,
                                                appSettings,
                                                "SABnzbd reported failed state",
                                                cancellationToken);
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Error updating SABnzbd queue progress for slot");
                                }
                            }
                        }
                    }

                    // Get completed downloads (history) - limit to recent items
                    var historyUrl = $"{baseUrl}?mode=history&limit=100&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                    // Redacted history URL for safe diagnostics
                    _logger.LogDebug("SABnzbd history URL (redacted): {Url}", LogRedaction.RedactText(historyUrl, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { apiKey })));
                    using var historyResponse = await http.GetAsync(historyUrl, cancellationToken);

                    if (!historyResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to fetch SABnzbd history for {ClientName}: {StatusCode}", client.Name, historyResponse.StatusCode);
                        return;
                    }

                    var historyJson = await historyResponse.Content.ReadAsStringAsync(cancellationToken);
                    var historyDoc = System.Text.Json.JsonDocument.Parse(historyJson);

                    if (!historyDoc.RootElement.TryGetProperty("history", out var history) ||
                        !history.TryGetProperty("slots", out var slots) ||
                        slots.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        _logger.LogDebug("No history data found for SABnzbd client {ClientName}", client.Name);
                        return;
                    }

                    // Build a lookup of completed items for faster matching
                    // Include nzo_id when available so we can match downloads by ID as well
                    var completedItems = new List<(string Name, string Status, string Path, DateTime CompletedTime, string NzoId)>();
                    var failedItems = new List<(string Name, string Status, string Path, DateTime CompletedTime, string NzoId, string Error)>();

                    foreach (var slot in slots.EnumerateArray())
                    {
                        var name = slot.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                        var status = slot.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                        var path = slot.TryGetProperty("storage", out var pathProp) ? pathProp.GetString() ?? "" : "";
                        var nzoId = slot.TryGetProperty("nzo_id", out var nzoIdProp) ? nzoIdProp.GetString() ?? "" : "";

                        // Parse completion time
                        var completedTime = DateTime.MinValue;
                        if (slot.TryGetProperty("completed", out var completedProp))
                        {
                            var completedTimestamp = completedProp.GetInt64();
                            completedTime = DateTimeOffset.FromUnixTimeSeconds(completedTimestamp).DateTime;
                        }

                        if (!string.IsNullOrEmpty(name) &&
                            (status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                             status.Equals("Complete", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.LogInformation("SABnzbd history slot parsed: nzo_id={NzoId}, name={Name}, status={Status}, path={Path}, completed={Completed}", nzoId, LogRedaction.SanitizeText(name), LogRedaction.SanitizeText(status), LogRedaction.SanitizeFilePath(path), completedTime);

                            completedItems.Add((name, status, path, completedTime, nzoId));
                        }
                        else if (!string.IsNullOrEmpty(name) && status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                        {
                            var failMessage = slot.TryGetProperty("fail_message", out var failProp)
                                ? failProp.GetString() ?? string.Empty
                                : status;

                            failedItems.Add((name, status, path, completedTime, nzoId, failMessage));
                        }
                    }

                    _logger.LogDebug("Found {CompletedCount} completed items in SABnzbd history for client {ClientName}",
                        completedItems.Count, client.Name);

                    // Check each download against completed items
                    foreach (var dl in downloads)
                    {
                        try
                        {
                            // Skip downloads that are already being processed, awaiting import,
                            // or fully imported to avoid duplicate finalization/notifications.
                            if (dl.Status == DownloadStatus.Moved ||
                                dl.Status == DownloadStatus.Processing ||
                                dl.Status == DownloadStatus.ImportPending)
                                continue;

                            var failedMatch = failedItems.FirstOrDefault(item =>
                                (!string.IsNullOrEmpty(item.NzoId) && !string.IsNullOrEmpty(GetClientItemId(dl)) &&
                                    string.Equals(item.NzoId, GetClientItemId(dl), StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                            );

                            if (!string.IsNullOrEmpty(failedMatch.Name))
                            {
                                _logger.LogInformation("Found failed SABnzbd download: {DownloadTitle} -> {FailedName}", dl.Title, failedMatch.Name);
                                await HandleFailedDownloadAsync(
                                    dl,
                                    client,
                                    downloadRepository,
                                    appSettings,
                                    failedMatch.Error,
                                    cancellationToken);

                                if (_completionCandidates.Remove(dl.Id))
                                {
                                    _ = BroadcastCandidateUpdateAsync(dl, false, cancellationToken);
                                }
                                continue;
                            }

                            // Find matching active download by NZO ID
                            var matchingItem = completedItems.FirstOrDefault(item =>
                                // Match by NZO ID (strongest) or fall back to name/title matching
                                (!string.IsNullOrEmpty(item.NzoId) && !string.IsNullOrEmpty(GetClientItemId(dl)) &&
                                    string.Equals(item.NzoId, GetClientItemId(dl), StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                            );

                            if (!string.IsNullOrEmpty(matchingItem.Name))
                            {
                                // Record match type metrics
                                try
                                {
                                    if (!string.IsNullOrEmpty(matchingItem.NzoId) && !string.IsNullOrEmpty(GetClientItemId(dl)) && string.Equals(matchingItem.NzoId, GetClientItemId(dl), StringComparison.OrdinalIgnoreCase))
                                    {
                                        _metrics.Increment("sabnzbd.history.match.nzo");
                                    }
                                    else if (!string.IsNullOrEmpty(matchingItem.Name) && string.Equals(matchingItem.Name, dl.Title, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _metrics.Increment("sabnzbd.history.match.title_exact");
                                    }
                                    else
                                    {
                                        _metrics.Increment("sabnzbd.history.match.title_contains");
                                    }
                                }
                                catch (Exception caughtEx_11) when (caughtEx_11 is not OperationCanceledException && caughtEx_11 is not OutOfMemoryException && caughtEx_11 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                _logger.LogInformation("Found completed SABnzbd download: {DownloadTitle} -> {CompletedName} at {Path}",
                                    dl.Title, matchingItem.Name, matchingItem.Path);

                                // Check stability window
                                // Use configured stability window if available
                                TimeSpan stableWindow = _completionStableWindow;
                                try
                                {
                                    using var settingsScope = _serviceScopeFactory.CreateScope();
                                    var cfg = settingsScope.ServiceProvider.GetService<IConfigurationService>();
                                    if (cfg != null)
                                    {
                                        var appSettings = await cfg.GetApplicationSettingsAsync();
                                        if (appSettings != null && appSettings.DownloadCompletionStabilitySeconds > 0)
                                        {
                                            stableWindow = TimeSpan.FromSeconds(appSettings.DownloadCompletionStabilitySeconds);
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogDebug(ex, "Failed to read application settings for stability window, falling back to default");
                                }
                                if (_completionCandidates.TryAdd(dl.Id, DateTime.UtcNow))
                                {
                                    _logger.LogInformation("Download {DownloadId} observed as complete candidate (SABnzbd). Waiting for stability window.", dl.Id);

                                    // Update download status to Completed in database so it stops being re-added to candidates
                                    try
                                    {
                                        dl.Status = DownloadStatus.Completed;
                                        dl.Progress = 100M;
                                        await downloadRepository.UpdateAsync(dl);
                                        _logger.LogDebug("Updated download {DownloadId} status to Completed in database", dl.Id);
                                    }
                                    catch (Exception ex2) when (ex2 is not OperationCanceledException && ex2 is not OutOfMemoryException && ex2 is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex2, "Failed to update download {DownloadId} status to Completed", dl.Id);
                                    }

                                    // Broadcast candidate so UI can surface it immediately
                                    _ = BroadcastCandidateUpdateAsync(dl, true, cancellationToken);
                                    continue;
                                }

                                if (_completionCandidates.TryGetValue(dl.Id, out var firstSeen) &&
                                    DateTime.UtcNow - firstSeen >= stableWindow)
                                {
                                    _logger.LogInformation("Download {DownloadId} confirmed complete after stability window (SABnzbd). Finalizing from path: {Path}",
                                        dl.Id, matchingItem.Path);
                                    await FinalizeDownloadAsync(dl, matchingItem.Path, client, cancellationToken);
                                    _completionCandidates.Remove(dl.Id);
                                }
                            }
                            else
                            {
                                // Not found in completed items - check if it's still in queue for progress updates
                                // SABnzbd doesn't provide queue data in history API, so we can't update progress here
                                // Progress updates for SABnzbd would need to be done via the queue API
                                if (_completionCandidates.Remove(dl.Id))
                                {
                                    _logger.LogDebug("Download {DownloadId} no longer appears complete in SABnzbd, removed from candidates", dl.Id);
                                    _ = BroadcastCandidateUpdateAsync(dl, false, cancellationToken);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Error processing download {DownloadId} while polling SABnzbd", dl.Id);
                        }
                    }

                    // Schedule next poll now that this client's polling completed successfully
                    _ = ScheduleNextClientPollOnSuccess(client.Id, downloads.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error polling SABnzbd client {ClientName}", client.Name);
                    ScheduleNextClientPollOnFailure(client.Id);
                }
            }, cancellationToken);
        }

        private Task PollNZBGetAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            IDownloadRepository downloadRepository,
            ApplicationSettings appSettings,
            CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                _logger.LogDebug("Polling NZBGet client {ClientName}", client.Name);
                try
                {
                    var now = DateTime.UtcNow;

                    // Respect per-client poll schedules to avoid overloading NZBGet
                    if (_nextClientPoll.TryGetValue(client.Id, out var scheduled) && now < scheduled)
                    {
                        _logger.LogDebug("Skipping NZBGet poll for {ClientName}, next scheduled at {Next}", client.Name, scheduled);
                        return;
                    }

                    var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/jsonrpc");

                    using var http = _httpClientFactory.CreateClient("nzbget");

                    // Add basic auth if credentials provided
                    if (!string.IsNullOrEmpty(client.Username))
                    {
                        var authBytes = System.Text.Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}");
                        var authHeader = Convert.ToBase64String(authBytes);
                        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
                    }

                    // Get active downloads from status for progress updates
                    var statusRequest = new
                    {
                        method = "status",
                        id = 2
                    };

                    var statusJsonContent = System.Text.Json.JsonSerializer.Serialize(statusRequest);
                    using var statusHttpContent = new StringContent(statusJsonContent, System.Text.Encoding.UTF8, "application/json");

                    using var statusResponse = await http.PostAsync(baseUrl, statusHttpContent, cancellationToken);

                    if (statusResponse.IsSuccessStatusCode)
                    {
                        var statusJson = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
                        var statusDoc = System.Text.Json.JsonDocument.Parse(statusJson);

                        if (statusDoc.RootElement.TryGetProperty("result", out var statusResult))
                        {
                            // Get queue for active downloads
                            var queueRequest = new
                            {
                                method = "listgroups",
                                id = 3
                            };

                            var queueJsonContent = System.Text.Json.JsonSerializer.Serialize(queueRequest);
                            using var queueHttpContent = new StringContent(queueJsonContent, System.Text.Encoding.UTF8, "application/json");

                            using var queueResponse = await http.PostAsync(baseUrl, queueHttpContent, cancellationToken);

                            if (queueResponse.IsSuccessStatusCode)
                            {
                                var queueJson = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
                                var queueDoc = System.Text.Json.JsonDocument.Parse(queueJson);

                                if (queueDoc.RootElement.TryGetProperty("result", out var queueResult) && queueResult.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var group in queueResult.EnumerateArray())
                                    {
                                        try
                                        {
                                            var nzbId = group.TryGetProperty("NZBID", out var nzbIdProp) ? nzbIdProp.GetInt32() : 0;
                                            var nzbName = group.TryGetProperty("NZBName", out var nameProp) ? nameProp.GetString() ?? "" : "";
                                            var status = group.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                                            var fileSizeMB = group.TryGetProperty("FileSizeMB", out var sizeProp) ? sizeProp.GetString() ?? "" : "";
                                            var remainingSizeMB = group.TryGetProperty("RemainingSizeMB", out var remainingSizeProp) ? remainingSizeProp.GetString() ?? "" : "";
                                            // Find matching download by NZB ID
                                            var matchingDownload = downloads.FirstOrDefault(dl =>
                                            {
                                                var clientItemId = GetClientItemId(dl);
                                                return !string.IsNullOrEmpty(clientItemId) &&
                                                       clientItemId.Equals(nzbId.ToString(), StringComparison.OrdinalIgnoreCase);
                                            });

                                            if (matchingDownload == null && !string.IsNullOrEmpty(nzbName))
                                            {
                                                matchingDownload = downloads.FirstOrDefault(dl => TitleUtils.AreTitlesSimilar(dl.Title, nzbName));
                                            }

                                            if (matchingDownload != null &&
                                                double.TryParse(fileSizeMB, out var totalMB) &&
                                                double.TryParse(remainingSizeMB, out var remainingMB))
                                            {
                                                var progress = totalMB > 0 ? (totalMB - remainingMB) / totalMB : 0.0;
                                                var amountLeft = (long)(remainingMB * 1024 * 1024); // Convert MB to bytes

                                                await UpdateDownloadProgressAsync(matchingDownload.Id, progress, amountLeft, status, downloadRepository, cancellationToken);

                                                if (status.Equals("FAILURE", StringComparison.OrdinalIgnoreCase) ||
                                                    status.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    await HandleFailedDownloadAsync(
                                                        matchingDownload,
                                                        client,
                                                        downloadRepository,
                                                        appSettings,
                                                        $"NZBGet status: {status}",
                                                        cancellationToken);
                                                }
                                            }
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                        {
                                            _logger.LogWarning(ex, "Error updating NZBGet queue progress for group");
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Get completed downloads from history
                    var historyRequest = new
                    {
                        method = "history",
                        @params = new object[] { false }, // hidden = false
                        id = 1
                    };

                    var jsonContent = System.Text.Json.JsonSerializer.Serialize(historyRequest);
                    using var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
                    using var historyResponse = await http.PostAsync(baseUrl, httpContent, cancellationToken);

                    if (!historyResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to fetch NZBGet history for {ClientName}: {StatusCode}", client.Name, historyResponse.StatusCode);
                        return;
                    }

                    var historyJson = await historyResponse.Content.ReadAsStringAsync(cancellationToken);
                    var historyDoc = System.Text.Json.JsonDocument.Parse(historyJson);

                    // Check for RPC error
                    if (historyDoc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        var errorMsg = "Unknown error";
                        if (error.TryGetProperty("message", out var errorMessage))
                        {
                            errorMsg = errorMessage.GetString() ?? "Unknown error";
                        }
                        _logger.LogWarning("NZBGet RPC error for {ClientName}: {Error}", client.Name, errorMsg);
                        return;
                    }

                    if (!historyDoc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        _logger.LogDebug("No history data found for NZBGet client {ClientName}", client.Name);
                        return;
                    }

                    // Build a lookup of completed items
                    var completedItems = new List<(string Name, string Status, string DestDir, DateTime CompletedTime, string Id)>();
                    var failedItems = new List<(string Name, string Status, string DestDir, DateTime CompletedTime, string Id, string Error)>();

                    foreach (var item in result.EnumerateArray())
                    {
                        var name = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                        var status = item.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                        var destDir = item.TryGetProperty("DestDir", out var destProp) ? destProp.GetString() ?? "" : "";
                        var itemId = item.TryGetProperty("ID", out var idProp)
                            ? (idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32().ToString() : idProp.GetString() ?? string.Empty)
                            : string.Empty;

                        // Parse completion time
                        var completedTime = DateTime.MinValue;
                        if (item.TryGetProperty("HistoryTime", out var timeProp))
                        {
                            var timestamp = timeProp.GetInt64();
                            completedTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                        }

                        // NZBGet status values can include suffixed variants like
                        // SUCCESS/HEALTH or FAILURE/HEALTH. Treat all SUCCESS* as
                        // completed and FAILURE*/FAILED* as failed.
                        if (!string.IsNullOrEmpty(name) &&
                            status.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
                        {
                            completedItems.Add((name, status, destDir, completedTime, itemId));
                        }
                        else if (!string.IsNullOrEmpty(name) &&
                                 (status.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase) ||
                                  status.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase)))
                        {
                            var failMessage = item.TryGetProperty("Message", out var msgProp) ? msgProp.GetString() ?? string.Empty : string.Empty;
                            if (string.IsNullOrWhiteSpace(failMessage))
                            {
                                failMessage = item.TryGetProperty("FailReason", out var failProp) ? failProp.GetString() ?? string.Empty : status;
                            }

                            failedItems.Add((name, status, destDir, completedTime, itemId, failMessage));
                        }
                    }

                    _logger.LogDebug("Found {CompletedCount} completed items in NZBGet history for client {ClientName}",
                        completedItems.Count, client.Name);

                    // Check each download against completed items
                    foreach (var dl in downloads)
                    {
                        try
                        {
                            // Skip downloads that are already being processed, awaiting import,
                            // or fully imported to avoid duplicate finalization/notifications.
                            if (dl.Status == DownloadStatus.Moved ||
                                dl.Status == DownloadStatus.Processing ||
                                dl.Status == DownloadStatus.ImportPending)
                                continue;

                            var failedMatch = failedItems.FirstOrDefault(item =>
                                (!string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(GetClientItemId(dl)) &&
                                    string.Equals(item.Id, GetClientItemId(dl), StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                            );

                            if (!string.IsNullOrEmpty(failedMatch.Name))
                            {
                                _logger.LogInformation("Found failed NZBGet download: {DownloadTitle} -> {FailedName}", dl.Title, failedMatch.Name);
                                await HandleFailedDownloadAsync(
                                    dl,
                                    client,
                                    downloadRepository,
                                    appSettings,
                                    failedMatch.Error,
                                    cancellationToken);

                                if (_completionCandidates.Remove(dl.Id))
                                {
                                    _ = BroadcastCandidateUpdateAsync(dl, false, cancellationToken);
                                }
                                continue;
                            }

                            // Find matching completed download by name
                            var matchingItem = completedItems.FirstOrDefault(item =>
                                (!string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(GetClientItemId(dl)) &&
                                    string.Equals(item.Id, GetClientItemId(dl), StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                            );

                            if (!string.IsNullOrEmpty(matchingItem.Name))
                            {
                                _logger.LogInformation("Found completed NZBGet download: {DownloadTitle} -> {CompletedName} at {Path}",
                                    dl.Title, matchingItem.Name, matchingItem.DestDir);

                                // Check stability window
                                if (_completionCandidates.TryAdd(dl.Id, DateTime.UtcNow))
                                {
                                    _logger.LogInformation("Download {DownloadId} observed as complete candidate (NZBGet). Waiting for stability window.", dl.Id);

                                    // Update download status to Completed in database so it stops being re-added to candidates
                                    try
                                    {
                                        dl.Status = DownloadStatus.Completed;
                                        dl.Progress = 100M;
                                        await downloadRepository.UpdateAsync(dl);
                                        _logger.LogDebug("Updated download {DownloadId} status to Completed in database", dl.Id);
                                    }
                                    catch (Exception ex2) when (ex2 is not OperationCanceledException && ex2 is not OutOfMemoryException && ex2 is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex2, "Failed to update download {DownloadId} status to Completed", dl.Id);
                                    }

                                    // Broadcast candidate so UI can surface it immediately
                                    _ = BroadcastCandidateUpdateAsync(dl, true, cancellationToken);
                                    continue;
                                }

                                if (_completionCandidates.TryGetValue(dl.Id, out var firstSeen) &&
                                    DateTime.UtcNow - firstSeen >= _completionStableWindow)
                                {
                                    _logger.LogInformation("Download {DownloadId} confirmed complete after stability window (NZBGet). Finalizing from path: {Path}",
                                        dl.Id, matchingItem.DestDir);
                                    await FinalizeDownloadAsync(dl, matchingItem.DestDir, client, cancellationToken);
                                    _completionCandidates.Remove(dl.Id);
                                }
                            }
                            else
                            {
                                // Not found in completed items - remove from candidates if present
                                if (_completionCandidates.Remove(dl.Id))
                                {
                                    _logger.LogDebug("Download {DownloadId} no longer appears complete in NZBGet, removed from candidates", dl.Id);
                                    _ = BroadcastCandidateUpdateAsync(dl, false, cancellationToken);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Error processing download {DownloadId} while polling NZBGet", dl.Id);
                        }
                    }

                    // Schedule next poll now that this client's polling completed successfully
                    _ = ScheduleNextClientPollOnSuccess(client.Id, downloads.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error polling NZBGet client {ClientName}", client.Name);
                    ScheduleNextClientPollOnFailure(client.Id);
                }
            }, cancellationToken);
        }

        private async Task UpdateDownloadProgressAsync(string downloadId, double progress, long amountLeft, string clientState, IDownloadRepository downloadRepository, CancellationToken cancellationToken)
        {
            try
            {
                var download = await downloadRepository.FindAsync(downloadId);
                if (download == null) return;

                var normalizedState = (clientState ?? string.Empty).ToLowerInvariant();

                // Map client state to our DownloadStatus
                var mappedStatus = normalizedState switch
                {
                    "downloading" => DownloadStatus.Downloading,
                    "metadl" => DownloadStatus.Downloading,
                    "forceddl" => DownloadStatus.Downloading,
                    "stalleddl" => DownloadStatus.Downloading,
                    "checkingdl" => DownloadStatus.Downloading,
                    "checkingresumedata" => DownloadStatus.Downloading,
                    "moving" => DownloadStatus.Downloading,
                    "fetching" => DownloadStatus.Downloading,
                    "scanning" => DownloadStatus.Downloading,
                    "pp_queued" => DownloadStatus.Downloading,
                    "pp_processing" => DownloadStatus.Downloading,
                    "uploading" => DownloadStatus.Downloading,
                    "stalledup" => DownloadStatus.Downloading,
                    "checkingup" => DownloadStatus.Downloading,
                    "forcedup" => DownloadStatus.Downloading,
                    "stoppeddl" => DownloadStatus.Paused,
                    "stoppedup" => DownloadStatus.Paused,
                    "queueddl" => DownloadStatus.Queued,
                    "queuedup" => DownloadStatus.Queued,
                    "queued" => DownloadStatus.Queued,
                    "paused" => DownloadStatus.Paused,
                    "seeding" => DownloadStatus.Downloading,
                    "success" => DownloadStatus.Completed,
                    "error" => DownloadStatus.Failed,
                    "failed" => DownloadStatus.Failed,
                    "failure" => DownloadStatus.Failed,
                    "missingfiles" => DownloadStatus.Failed,
                    "missing_files" => DownloadStatus.Failed,
                    _ => DownloadStatus.Queued
                };

                // Calculate downloaded size from progress and total size
                long downloadedSize = download.TotalSize > 0 ? (long)(download.TotalSize * progress / 100) : 0;

                // Update download record
                download.Progress = (decimal)progress;
                download.DownloadedSize = downloadedSize;

                // Conservative guard: if the DB record is currently Failed, do not overwrite
                // the status to a non-failed value unless we have strong evidence (progress increased)
                // or the client reports Completed. This prevents transient client "error" states
                // from flipping the UI incorrectly.
                if (download.Status == DownloadStatus.Failed && mappedStatus != DownloadStatus.Failed)
                {
                    var incomingProgress = (decimal)progress;

                    // Allow transition to Completed always (finalization or client reports complete)
                    if (mappedStatus == DownloadStatus.Completed)
                    {
                        _logger.LogInformation("Allowing Failed->Completed for {DownloadId} because client reports completion", downloadId);
                        download.Status = mappedStatus;
                    }
                    else
                    {
                        // Only allow non-failed status if progress increased
                        if (incomingProgress <= download.Progress)
                        {
                            _logger.LogDebug("Skipping status overwrite for failed download {DownloadId}: incoming progress {Incoming} <= current {Current}", downloadId, incomingProgress, download.Progress);
                            // still update metadata for visibility
                            download.Metadata ??= new Dictionary<string, object>();
                            download.Metadata!["ClientState"] = clientState ?? "Unknown";
                            download.Metadata!["AmountLeft"] = amountLeft;
                            await downloadRepository.UpdateAsync(download);
                            return;
                        }

                        _logger.LogInformation("Updating Failed -> {MappedStatus} for {DownloadId} because progress increased ({Old} -> {New})", mappedStatus, downloadId, download.Progress, incomingProgress);
                        download.Status = mappedStatus;
                    }
                }
                else if (download.Status != DownloadStatus.Completed && download.Status != DownloadStatus.Moved)
                {
                    // Don't overwrite Completed/Moved status - Completed is managed by the completion
                    // detection logic, and Moved means the file is already imported (we only keep
                    // polling Moved downloads to update CanBeRemoved for deferred client removal).
                    download.Status = mappedStatus;
                }
                else
                {
                    _logger.LogDebug("Preserving {Status} status for {DownloadId} - not overwriting with client state {ClientState}", download.Status, downloadId, clientState);
                }

                // Add metadata for real-time updates
                download.Metadata ??= new Dictionary<string, object>();
                download.Metadata!["ClientState"] = clientState ?? "Unknown";
                download.Metadata!["AmountLeft"] = amountLeft;

                await downloadRepository.UpdateAsync(download);

                _logger.LogDebug("Updated download {DownloadId} progress: {Progress:F1}%, Status: {Status}, Downloaded: {Downloaded:N0} bytes",
                    downloadId, progress, mappedStatus, downloadedSize);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error updating download progress for {DownloadId}", downloadId);
            }
        }

        private static string? GetClientItemId(Download download)
        {
            if (download?.Metadata == null) return null;

            if (download.Metadata.TryGetValue("ClientDownloadId", out var clientIdObj))
            {
                var clientId = clientIdObj?.ToString();
                if (!string.IsNullOrWhiteSpace(clientId)) return clientId;
            }

            if (download.Metadata.TryGetValue("TorrentHash", out var hashObj))
            {
                var hash = hashObj?.ToString();
                if (!string.IsNullOrWhiteSpace(hash)) return hash;
            }

            return null;
        }

        private async Task HandleFailedDownloadAsync(
            Download download,
            DownloadClientConfiguration client,
            IDownloadRepository downloadRepository,
            ApplicationSettings settings,
            string? errorMessage,
            CancellationToken cancellationToken)
        {
            if (download == null) return;
            if (download.Status == DownloadStatus.Failed) return;

            var failureMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? "Download failed in client"
                : errorMessage.Trim();

            download.Status = DownloadStatus.Failed;
            download.ErrorMessage = failureMessage;
            download.CompletedAt = DateTime.UtcNow;

            if (download.Metadata == null)
            {
                download.Metadata = new Dictionary<string, object>();
            }

            download.Metadata["ClientFailureReason"] = failureMessage;

            await downloadRepository.UpdateAsync(download);

            try
            {
                await BroadcastDownloadUpdatesAsync(new List<Download> { download }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast failed download update for {DownloadId}", download.Id);
            }

            using var scope = _serviceScopeFactory.CreateScope();
            var historyService = scope.ServiceProvider.GetService<IDownloadHistoryService>();
            if (historyService != null && !string.IsNullOrWhiteSpace(download.DownloadClientId))
            {
                try
                {
                    await historyService.RecordDownloadFailedAsync(
                        download.Id,
                        download.DownloadClientId,
                        download.Title ?? "Unknown",
                        failureMessage);
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException)
                {
                    _logger.LogDebug(histEx, "Failed to record download failure history for {DownloadId}", download.Id);
                }
            }

            if (!settings.FailedDownloadHandlingEnabled)
            {
                return;
            }

            // Remove from client queue/history when handling is enabled
            try
            {
                var gateway = scope.ServiceProvider.GetService<IDownloadClientGateway>();
                var clientItemId = GetClientItemId(download) ?? download.Id;
                if (gateway != null && !string.IsNullOrWhiteSpace(clientItemId))
                {
                    await gateway.RemoveAsync(client, clientItemId, deleteFiles: false, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to remove failed download {DownloadId} from client {ClientName}", download.Id, client.Name);
            }

            if (settings.FailedDownloadAutoSearch && download.AudiobookId.HasValue)
            {
                try
                {
                    var audiobookRepository = scope.ServiceProvider.GetService<IAudiobookRepository>();
                    var audiobook = audiobookRepository != null ? await audiobookRepository.GetByIdAsync(download.AudiobookId.Value) : null;
                    if (audiobook != null && audiobook.Monitored)
                    {
                        var downloadService = scope.ServiceProvider.GetService<IDownloadService>();
                        if (downloadService != null)
                        {
                            await downloadService.SearchAndDownloadAsync(download.AudiobookId.Value);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to auto-search after failed download {DownloadId}", download.Id);
                }
            }
        }

        private async Task BroadcastDownloadUpdatesAsync(
            List<Download> currentDownloads,
            CancellationToken cancellationToken)
        {
            var changedDownloads = new List<Download>();

            // Try to get DownloadPushService from DI so we can avoid re-broadcasting
            // downloads that were recently pushed by clients.
            DownloadPushService? pushService = null;
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                pushService = scope.ServiceProvider.GetService<DownloadPushService>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Unable to resolve DownloadPushService (non-fatal)");
            }

            foreach (var download in currentDownloads)
            {
                // Check if this download has changed
                if (_lastDownloadStates.TryGetValue(download.Id, out var lastState))
                {
                    if (HasDownloadChanged(lastState, download))
                    {
                        // If this download was recently pushed by a client, skip re-broadcasting
                        if (pushService != null && pushService.WasRecentlyPushed(download.Id))
                        {
                            _logger.LogDebug("Skipping broadcast for download {DownloadId} because it was recently pushed", download.Id);
                        }
                        else
                        {
                            changedDownloads.Add(download);
                        }

                        _lastDownloadStates[download.Id] = CloneDownload(download);
                    }
                }
                else
                {
                    // New download
                    changedDownloads.Add(download);
                    _lastDownloadStates[download.Id] = CloneDownload(download);
                }
            }

            // Clean up old download states that are no longer in the list
            var currentIds = currentDownloads.Select(d => d.Id).ToHashSet();
            var keysToRemove = _lastDownloadStates.Keys.Where(k => !currentIds.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                _lastDownloadStates.Remove(key);
            }

            // Broadcast updates if there are changes
            if (changedDownloads.Any())
            {
                _logger.LogDebug("Broadcasting {Count} download updates", changedDownloads.Count);

                // Sanitize each Download before broadcasting to clients (remove DownloadPath and client-local metadata)
                var sanitized = changedDownloads.Select(d => new
                {
                    id = d.Id,
                    audiobookId = d.AudiobookId,
                    title = d.Title,
                    artist = d.Artist,
                    album = d.Album,
                    originalUrl = d.OriginalUrl,
                    status = d.Status.ToString(),
                    progress = d.Progress,
                    totalSize = d.TotalSize,
                    downloadedSize = d.DownloadedSize,
                    finalPath = d.FinalPath,
                    startedAt = d.StartedAt,
                    completedAt = d.CompletedAt,
                    errorMessage = d.ErrorMessage,
                    downloadClientId = d.DownloadClientId,
                    metadata = (d.Metadata ?? new Dictionary<string, object>()).Where(kvp => !string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                }).ToList();

                _logger.LogInformation("Broadcasting DownloadUpdate with {Count} items; sample ids: {Ids}", sanitized.Count, sanitized.Select(s => s.id).Take(5).ToArray());

                try
                {
                    await _hubContext.Clients.All.SendAsync(
                        "DownloadUpdate",
                        sanitized,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // Log a friendly warning so operator can see broadcast failures
                    _logger.LogWarning(ex, "Failed to send DownloadUpdate to SignalR clients (Count={Count}, SampleIds={Ids})", sanitized.Count, sanitized.Select(s => s.id).Take(5).ToArray());
                }
            }

            // Also send full list periodically (every 10 polls)
            if (DateTime.UtcNow.Second % 30 == 0)
            {
                // Broadcast a sanitized full list (remove DownloadPath and client-local metadata)
                var sanitizedList = currentDownloads.Select(d => new
                {
                    id = d.Id,
                    audiobookId = d.AudiobookId,
                    title = d.Title,
                    artist = d.Artist,
                    album = d.Album,
                    originalUrl = d.OriginalUrl,
                    status = d.Status.ToString(),
                    progress = d.Progress,
                    totalSize = d.TotalSize,
                    downloadedSize = d.DownloadedSize,
                    finalPath = d.FinalPath,
                    startedAt = d.StartedAt,
                    completedAt = d.CompletedAt,
                    errorMessage = d.ErrorMessage,
                    downloadClientId = d.DownloadClientId,
                    metadata = (d.Metadata ?? new Dictionary<string, object>()).Where(kvp => !string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                }).ToList();

                _logger.LogInformation("Broadcasting DownloadsList with {Count} items; sample ids: {Ids}", sanitizedList.Count, sanitizedList.Select(s => s.id).Take(5).ToArray());

                try
                {
                    await _hubContext.Clients.All.SendAsync(
                        "DownloadsList",
                        sanitizedList,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to send DownloadsList to SignalR clients (Count={Count}, SampleIds={Ids})", sanitizedList.Count, sanitizedList.Select(s => s.id).Take(5).ToArray());
                }
            }
        }

        private bool HasDownloadChanged(Download oldDownload, Download newDownload)
        {
            return oldDownload.Status != newDownload.Status ||
                   oldDownload.Progress != newDownload.Progress ||
                   oldDownload.DownloadedSize != newDownload.DownloadedSize ||
                   oldDownload.ErrorMessage != newDownload.ErrorMessage ||
                   oldDownload.CompletedAt != newDownload.CompletedAt;
        }

        private static string CombineWithOptionalBase(string? basePath, string candidatePath)
        {
            var normalizedPath = candidatePath.Trim();

            if (string.IsNullOrEmpty(normalizedPath))
            {
                return normalizedPath;
            }

            if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
            {
                return normalizedPath;
            }

            var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(normalizedBasePath)
                ? relativePath
                : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
        }

        /// <summary>
        /// Determines whether a qBittorrent torrent has reached its seed limit.
        /// Used by the qBittorrent poller to compute CanMoveFiles/CanBeRemoved per-torrent.
        /// Mirrors Sonarr's HasReachedSeedLimit logic.
        /// </summary>
        private static bool QBitHasReachedSeedLimit(
            double ratio,
            float ratioLimit,
            long? seedingTime,
            long seedingTimeLimit,
            bool globalMaxRatioEnabled,
            float globalMaxRatio,
            bool globalMaxSeedingTimeEnabled,
            long globalMaxSeedingTime)
        {
            var hasEffectiveRatioLimit =
                ratioLimit >= 0 ||
                (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio > 0);
            var hasEffectiveSeedingTimeLimit =
                seedingTimeLimit >= 0 ||
                (seedingTimeLimit <= -2 && globalMaxSeedingTimeEnabled && globalMaxSeedingTime > 0);

            if (!hasEffectiveRatioLimit && !hasEffectiveSeedingTimeLimit)
                return true;

            // Check ratio limit (per-torrent override takes precedence)
            if (ratioLimit >= 0 && ratioLimit - ratio <= 0.001)
                return true;

            if (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio - ratio <= 0.001)
                return true;

            // Check seeding time limit (per-torrent override takes precedence)
            if (seedingTimeLimit >= 0 &&
                seedingTime is long currentSeedingTime &&
                currentSeedingTime >= seedingTimeLimit * 60)
                return true;

            if (seedingTimeLimit <= -2 &&
                globalMaxSeedingTimeEnabled &&
                seedingTime is long inheritedSeedingTime &&
                inheritedSeedingTime >= globalMaxSeedingTime * 60)
                return true;

            return false;
        }

        /// <summary>
        /// Determines whether a Transmission torrent has reached its seed limit.
        /// Mirrors Sonarr's HasReachedSeedLimit logic for Transmission.
        /// </summary>
        private static bool TransmissionHasReachedSeedLimit(
            bool isStopped,
            bool isSeeding,
            double ratio,
            int seedRatioMode,
            double seedRatioLimit,
            int seedIdleMode,
            int seedIdleLimit,
            long secondsSeeding,
            bool sessionSeedRatioLimited,
            double sessionSeedRatioLimit,
            bool sessionIdleSeedingLimitEnabled,
            int sessionIdleSeedingLimit)
        {
            var hasEffectiveRatioLimit =
                (seedRatioMode == 1 && seedRatioLimit > 0) ||
                (seedRatioMode == 0 && sessionSeedRatioLimited && sessionSeedRatioLimit > 0);
            var hasEffectiveIdleLimit =
                (seedIdleMode == 1 && seedIdleLimit > 0) ||
                (seedIdleMode == 0 && sessionIdleSeedingLimitEnabled && sessionIdleSeedingLimit > 0);

            // If Transmission has no seed ratio or idle seeding limits configured,
            // the user's remove policy should not defer forever. Treat the item as removable.
            if (!hasEffectiveRatioLimit && !hasEffectiveIdleLimit)
            {
                return true;
            }

            // seedRatioMode: 0 = global, 1 = per-torrent, 2 = unlimited
            if (seedRatioMode == 1 && isStopped && ratio >= seedRatioLimit)
                return true;

            bool globalRatioExceeded = seedRatioMode == 0 && isStopped && sessionSeedRatioLimited && ratio >= sessionSeedRatioLimit;
            if (globalRatioExceeded)
                return true;

            // seedIdleMode: 0 = global, 1 = per-torrent, 2 = unlimited
            bool perTorrentIdleExceeded = seedIdleMode == 1 && (isStopped || isSeeding) && secondsSeeding > seedIdleLimit * 60;
            if (perTorrentIdleExceeded)
                return true;

            if (seedIdleMode == 0 && isStopped && sessionIdleSeedingLimitEnabled)
                return true;

            return false;
        }

        private Download CloneDownload(Download download)
        {
            return new Download
            {
                Id = download.Id,
                Title = download.Title,
                Artist = download.Artist,
                Album = download.Album,
                OriginalUrl = download.OriginalUrl,
                Status = download.Status,
                Progress = download.Progress,
                TotalSize = download.TotalSize,
                DownloadedSize = download.DownloadedSize,
                DownloadPath = download.DownloadPath,
                FinalPath = download.FinalPath,
                StartedAt = download.StartedAt,
                CompletedAt = download.CompletedAt,
                ErrorMessage = download.ErrorMessage,
                DownloadClientId = download.DownloadClientId
            };
        }
    }
}


