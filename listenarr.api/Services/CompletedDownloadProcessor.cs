using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Listenarr.Application.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    public class CompletedDownloadProcessor : ICompletedDownloadProcessor
    {
        private readonly Listenarr.Api.Repositories.IDownloadRepository _downloadRepository;
        private readonly IFileFinalizer _fileFinalizer;
        private readonly IConfigurationService _configurationService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IImportService _importService;
        private readonly IArchiveExtractor _archiveExtractor;
        private readonly IHubBroadcaster? _hubBroadcaster;
        private readonly IHubContext<Listenarr.Api.Hubs.DownloadHub> _hubContext;
        private readonly IDownloadQueueService _downloadQueueService;
        private readonly IDownloadHistoryService? _downloadHistoryService;
        private readonly ILogger<CompletedDownloadProcessor> _logger;
        private readonly IAppMetricsService _metrics;

        public CompletedDownloadProcessor(
            Listenarr.Api.Repositories.IDownloadRepository downloadRepository,
            IFileFinalizer fileFinalizer,
            IConfigurationService configurationService,
            IServiceScopeFactory serviceScopeFactory,
            IImportService importService,
            IArchiveExtractor archiveExtractor,
            IDownloadQueueService downloadQueueService,
            IHubContext<Listenarr.Api.Hubs.DownloadHub> hubContext,
            ILogger<CompletedDownloadProcessor> logger,
            IHubBroadcaster? hubBroadcaster = null,
            IAppMetricsService? metrics = null,
            IDownloadHistoryService? downloadHistoryService = null)
        {
            _downloadRepository = downloadRepository;
            _fileFinalizer = fileFinalizer;
            _configurationService = configurationService;
            _serviceScopeFactory = serviceScopeFactory;
            _importService = importService;
            _archiveExtractor = archiveExtractor;
            _downloadQueueService = downloadQueueService;
            _hubContext = hubContext;
            _hubBroadcaster = hubBroadcaster;
            _logger = logger;
            _metrics = metrics ?? new NoopAppMetricsService();
            _downloadHistoryService = downloadHistoryService;
        }

        public async Task ProcessCompletedDownloadAsync(string downloadId, string finalPath)
        {
            _logger.LogInformation("ProcessCompletedDownloadAsync called for {DownloadId} (finalPath: {FinalPath})", downloadId, finalPath);

            try
            {
                var download = await _downloadRepository.FindAsync(downloadId);
                if (download == null)
                {
                    _logger.LogWarning("ProcessCompletedDownloadAsync: download record not found: {DownloadId}", downloadId);
                }
                else
                {
                    if (!TryTransitionStatus(download, DownloadStatus.ImportPending, "PreImport"))
                    {
                        return;
                    }
                    await _downloadRepository.UpdateAsync(download);
                    _logger.LogInformation("Marked download {DownloadId} as ImportPending (pre-import)", downloadId);

                    // Broadcast queue update immediately after marking as Completed so UI updates
                    try
                    {
                        await Task.Delay(100); // Brief delay for DB commit
                        var queueAfterComplete = await _downloadQueueService.GetQueueAsync();
                        if (_hubBroadcaster != null)
                        {
                            await _hubBroadcaster.BroadcastQueueUpdateAsync(queueAfterComplete);
                            _logger.LogDebug("Broadcasted QueueUpdate after marking {DownloadId} as Completed", downloadId);
                        }
                    }
                    catch (Exception broadcastEx) when (broadcastEx is not OperationCanceledException && broadcastEx is not OutOfMemoryException && broadcastEx is not StackOverflowException) {
                        _logger.LogDebug(broadcastEx, "Failed to broadcast after marking as Completed");
                    }

                    try
                    {
                        var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                        using var scopeSync = scopeFactoryToUse.CreateScope();
                        var scopedDb = scopeSync.ServiceProvider.GetService<ListenArrDbContext>();
                        if (scopedDb != null)
                        {
                            var local = await scopedDb.Downloads.FindAsync(downloadId);
                            if (local != null)
                            {
                                local.Status = DownloadStatus.ImportPending;
                                _logger.LogDebug("Synchronized ImportPending status into scoped ListenArrDbContext for {DownloadId}", downloadId);
                            }
                        }
                    }
                    catch (Exception syncEx) when (syncEx is not OperationCanceledException && syncEx is not OutOfMemoryException && syncEx is not StackOverflowException) {
                        _logger.LogDebug(syncEx, "Failed to synchronize status into scoped ListenArrDbContext (non-fatal)");
                    }
                }

                var importToastSent = false;
                ApplicationSettings settings = new ApplicationSettings();
                try
                {
                    settings = await _configurationService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "ProcessCompletedDownloadAsync: Failed to load application settings, using defaults");
                    settings = new ApplicationSettings();
                }

                if (string.IsNullOrWhiteSpace(finalPath))
                {
                    _logger.LogWarning("ProcessCompletedDownloadAsync: finalPath is empty for download {DownloadId}", downloadId);
                }
                else
                {
                    if (System.IO.Directory.Exists(finalPath))
                    {
                        try
                        {
                            var files = System.IO.Directory.GetFiles(finalPath, "*", System.IO.SearchOption.AllDirectories)
                                .Where(f => FileUtils.IsAudioFile(f))
                                .ToArray();
                            if (files != null && files.Length > 0)
                            {
                                var importResults = await _fileFinalizer.ImportFilesFromDirectoryAsync(downloadId, download?.AudiobookId, files, settings);
                                _logger.LogInformation("FileFinalizer.ImportFilesFromDirectoryAsync returned {Count} results for download {DownloadId}", importResults?.Count ?? 0, downloadId);
                            // if any successful imports returned final paths, set Download.FinalPath to the first one
                            try
                            {
                                var finalFromDirectory = importResults?.FirstOrDefault(r => r != null && r.Success && !string.IsNullOrWhiteSpace(r.FinalPath))?.FinalPath;
                                if (!string.IsNullOrWhiteSpace(finalFromDirectory))
                                {
                                    var tracked = await _downloadRepository.FindAsync(downloadId);
                                    if (tracked != null)
                                    {
                                        tracked.FinalPath = finalFromDirectory;
                                        tracked.Status = DownloadStatus.Moved;
                                        await _downloadRepository.UpdateAsync(tracked);
                                        _logger.LogInformation("Updated download {DownloadId} FinalPath to directory import result: {FinalPath}", downloadId, finalFromDirectory);
                                        
                                        // Record successful import in history for idempotency
                                        if (_downloadHistoryService != null && !string.IsNullOrEmpty(tracked.DownloadClientId))
                                        {
                                            try
                                            {
                                                await _downloadHistoryService.RecordImportedAsync(
                                                    tracked.Id,
                                                    tracked.DownloadClientId,
                                                    tracked.Title ?? "Unknown",
                                                    audiobookId: null);  // Audiobook ID is int in Download, but Guid in DownloadHistory
                                                _logger.LogInformation("Recorded successful import in history for download {DownloadId}", downloadId);
                                            }
                                            catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException) {
                                                _logger.LogWarning(histEx, "Failed to record import in history for download {DownloadId} (non-critical)", downloadId);
                                            }
                                        }
                                    }

                                    try
                                    {
                                        var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                                        using var afScope = scopeFactoryToUse.CreateScope();
                                        var scopedDb2 = afScope.ServiceProvider.GetService<ListenArrDbContext>();
                                        if (scopedDb2 != null)
                                        {
                                            var local2 = await scopedDb2.Downloads.FindAsync(downloadId);
                                            if (local2 != null)
                                            {
                                                local2.FinalPath = finalFromDirectory;
                                                local2.Status = DownloadStatus.Moved;
                                                _logger.LogDebug("Synchronized FinalPath into scoped ListenArrDbContext for {DownloadId}", downloadId);
                                            }
                                        }
                                    }
                                    catch (Exception sync2Ex) when (sync2Ex is not OperationCanceledException && sync2Ex is not OutOfMemoryException && sync2Ex is not StackOverflowException) {
                                        _logger.LogDebug(sync2Ex, "Failed to synchronize FinalPath into scoped ListenArrDbContext (non-fatal)");
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                _logger.LogDebug(ex, "Failed to update FinalPath from directory import results (non-fatal)");
                            }

                            // Update audiobook BasePath so future scans target the correct library
                            // directory instead of falling back to the global OutputPath.
                            try
                            {
                                if (download?.AudiobookId != null && importResults != null)
                                {
                                    var successPaths = importResults
                                        .Where(r => r != null && r.Success && !string.IsNullOrWhiteSpace(r.FinalPath))
                                        .Select(r => r.FinalPath!)
                                        .ToList();

                                    if (successPaths.Count > 0)
                                    {
                                        var dirs = successPaths
                                            .Select(p => System.IO.Path.GetDirectoryName(p) ?? p)
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .ToList();

                                        string? commonDir;
                                        if (dirs.Count == 1)
                                        {
                                            commonDir = dirs[0];
                                        }
                                        else
                                        {
                                            // Find common ancestor directory of all imported files
                                            var first = dirs[0];
                                            var minLen = dirs.Min(d => d.Length);
                                            int ci = 0;
                                            while (ci < minLen && dirs.All(d => char.ToUpperInvariant(d[ci]) == char.ToUpperInvariant(first[ci])))
                                                ci++;
                                            var prefix = first.Substring(0, ci);
                                            var lastSep = prefix.LastIndexOfAny(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar });
                                            commonDir = lastSep >= 0 ? prefix.Substring(0, lastSep) : prefix;
                                        }

                                        if (!string.IsNullOrWhiteSpace(commonDir))
                                        {
                                            var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                                            using var bpScope = scopeFactoryToUse.CreateScope();
                                            var bpDb = bpScope.ServiceProvider.GetService<ListenArrDbContext>();
                                            if (bpDb != null)
                                            {
                                                var audiobook = await bpDb.Audiobooks.FindAsync(download.AudiobookId.Value);
                                                if (audiobook != null && !commonDir.Equals(audiobook.BasePath, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    audiobook.BasePath = commonDir;
                                                    await bpDb.SaveChangesAsync();
                                                    _logger.LogInformation("Updated audiobook {AudiobookId} BasePath after directory import: {BasePath}", download.AudiobookId, commonDir);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception bpEx) when (bpEx is not OperationCanceledException && bpEx is not OutOfMemoryException && bpEx is not StackOverflowException) {
                                _logger.LogDebug(bpEx, "Failed to update audiobook BasePath after directory import (non-fatal)");
                            }

                            // Process archives inside the directory (extract and import)
                            if (settings.ExtractArchives)
                            {
                                var archiveFiles = files.Where(f => _archiveExtractor.IsArchive(f)).ToList();
                                foreach (var archivePath in archiveFiles)
                                {
                                    string? tempDirExtracted = null;
                                    try
                                    {
                                        tempDirExtracted = await _archiveExtractor.ExtractArchiveToTempDirAsync(archivePath);
                                        if (!string.IsNullOrWhiteSpace(tempDirExtracted) && System.IO.Directory.Exists(tempDirExtracted))
                                        {
                                            var extractedFiles = System.IO.Directory.GetFiles(tempDirExtracted, "*", System.IO.SearchOption.AllDirectories)
                                                .Where(f => FileUtils.IsAudioFile(f))
                                                .ToArray();
                                            if (extractedFiles != null && extractedFiles.Length > 0)
                                            {
                                                var extractedResults = await _fileFinalizer.ImportFilesFromDirectoryAsync(downloadId, download?.AudiobookId, extractedFiles, settings);
                                                _logger.LogInformation("Imported {Count} files extracted from archive {Archive} for download {DownloadId}", extractedResults?.Count ?? 0, archivePath, downloadId);

                                                var finalFromExtracted = extractedResults?.FirstOrDefault(r => r != null && r.Success && !string.IsNullOrWhiteSpace(r.FinalPath))?.FinalPath;
                                                if (!string.IsNullOrWhiteSpace(finalFromExtracted))
                                                {
                                                    var tracked = await _downloadRepository.FindAsync(downloadId);
                                                    if (tracked != null)
                                                    {
                                                        tracked.FinalPath = finalFromExtracted;
                                                        tracked.Status = DownloadStatus.Moved;
                                                        await _downloadRepository.UpdateAsync(tracked);
                                                        _logger.LogInformation("Updated download {DownloadId} FinalPath to extracted import result: {FinalPath}", downloadId, finalFromExtracted);
                                                        
                                                        // Record successful import in history for idempotency
                                                        if (_downloadHistoryService != null && !string.IsNullOrEmpty(tracked.DownloadClientId))
                                                        {
                                                            try
                                                            {
                                                                await _downloadHistoryService.RecordImportedAsync(
                                                                    tracked.Id,
                                                                    tracked.DownloadClientId,
                                                                    tracked.Title ?? "Unknown",
                                                                    audiobookId: null);
                                                                _logger.LogInformation("Recorded successful import in history for download {DownloadId}", downloadId);
                                                            }
                                                            catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException) {
                                                                _logger.LogWarning(histEx, "Failed to record import in history for download {DownloadId} (non-critical)", downloadId);
                                                            }
                                                        }
                                                    }

                                                    try
                                                    {
                                                        var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                                                        using var afScope = scopeFactoryToUse.CreateScope();
                                                        var scopedDb2 = afScope.ServiceProvider.GetService<ListenArrDbContext>();
                                                        if (scopedDb2 != null)
                                                        {
                                                            var local2 = await scopedDb2.Downloads.FindAsync(downloadId);
                                                            if (local2 != null)
                                                            {
                                                                local2.FinalPath = finalFromExtracted;
                                                                local2.Status = DownloadStatus.Moved;
                                                                _logger.LogDebug("Synchronized FinalPath into scoped ListenArrDbContext for {DownloadId}", downloadId);
                                                            }
                                                        }
                                                    }
                                                    catch (Exception sync2Ex) when (sync2Ex is not OperationCanceledException && sync2Ex is not OutOfMemoryException && sync2Ex is not StackOverflowException) {
                                                        _logger.LogDebug(sync2Ex, "Failed to synchronize FinalPath into scoped ListenArrDbContext (non-fatal)");
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                        _logger.LogWarning(ex, "Failed to extract/import archive {Archive} for download {DownloadId}", archivePath, downloadId);
                                    }
                                    finally
                                    {
                                        if (!string.IsNullOrWhiteSpace(tempDirExtracted) && System.IO.Directory.Exists(tempDirExtracted))
                                        {
                                            try { System.IO.Directory.Delete(tempDirExtracted, true); } catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                            }
                                        }
                                    }
                                }
                            }                            }
                            else
                            {
                                _logger.LogInformation("ProcessCompletedDownloadAsync: directory {FinalPath} contains no files to import (DownloadId: {DownloadId})", finalPath, downloadId);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogError(ex, "ProcessCompletedDownloadAsync: failed to import files from directory {FinalPath} for download {DownloadId}", finalPath, downloadId);
                        }
                    }
                    else
                    {
                        try
                        {
                            // If configured, and the file is an archive, extract and import contained files
                            if (settings.ExtractArchives && _archiveExtractor.IsArchive(finalPath))
                            {
                                string? tempExtractDir = null;
                                try
                                {
                                    tempExtractDir = await _archiveExtractor.ExtractArchiveToTempDirAsync(finalPath);
                                    if (!string.IsNullOrWhiteSpace(tempExtractDir) && System.IO.Directory.Exists(tempExtractDir))
                                    {
                                        var extractedFiles = System.IO.Directory.GetFiles(tempExtractDir, "*", System.IO.SearchOption.AllDirectories)
                                            .Where(f => FileUtils.IsAudioFile(f))
                                            .ToArray();
                                        if (extractedFiles != null && extractedFiles.Length > 0)
                                        {
                                            var extractedResults = await _fileFinalizer.ImportFilesFromDirectoryAsync(downloadId, download?.AudiobookId, extractedFiles, settings);
                                            _logger.LogInformation("Imported {Count} files extracted from archive {Archive} for download {DownloadId}", extractedResults?.Count ?? 0, finalPath, downloadId);

                                            var finalFromExtracted = extractedResults?.FirstOrDefault(r => r != null && r.Success && !string.IsNullOrWhiteSpace(r.FinalPath))?.FinalPath;
                                            if (!string.IsNullOrWhiteSpace(finalFromExtracted))
                                            {
                                                var tracked = await _downloadRepository.FindAsync(downloadId);
                                                if (tracked != null)
                                                {
                                                    tracked.FinalPath = finalFromExtracted;
                                                    tracked.Status = DownloadStatus.Moved;
                                                    await _downloadRepository.UpdateAsync(tracked);
                                                    _logger.LogInformation("Updated download {DownloadId} FinalPath to extracted import result: {FinalPath}", downloadId, finalFromExtracted);
                                                    
                                                    // Record successful import in history for idempotency
                                                    if (_downloadHistoryService != null && !string.IsNullOrEmpty(tracked.DownloadClientId))
                                                    {
                                                        try
                                                        {
                                                            await _downloadHistoryService.RecordImportedAsync(
                                                                tracked.Id,
                                                                tracked.DownloadClientId,
                                                                tracked.Title ?? "Unknown",
                                                                audiobookId: null);
                                                            _logger.LogInformation("Recorded successful import in history for download {DownloadId}", downloadId);
                                                        }
                                                        catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException) {
                                                            _logger.LogWarning(histEx, "Failed to record import in history for download {DownloadId} (non-critical)", downloadId);
                                                        }
                                                    }
                                                }

                                                try
                                                {
                                                    var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                                                    using var afScope = scopeFactoryToUse.CreateScope();
                                                    var scopedDb2 = afScope.ServiceProvider.GetService<ListenArrDbContext>();
                                                    if (scopedDb2 != null)
                                                    {
                                                        var local2 = await scopedDb2.Downloads.FindAsync(downloadId);
                                                        if (local2 != null)
                                                        {
                                                            local2.FinalPath = finalFromExtracted;
                                                            local2.Status = DownloadStatus.Moved;
                                                            _logger.LogDebug("Synchronized FinalPath into scoped ListenArrDbContext for {DownloadId}", downloadId);
                                                        }
                                                    }
                                                }
                                                catch (Exception sync2Ex) when (sync2Ex is not OperationCanceledException && sync2Ex is not OutOfMemoryException && sync2Ex is not StackOverflowException) {
                                                    _logger.LogDebug(sync2Ex, "Failed to synchronize FinalPath into scoped ListenArrDbContext (non-fatal)");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                    _logger.LogError(ex, "Failed to extract/import archive {FinalPath} for download {DownloadId}", finalPath, downloadId);
                                }
                                finally
                                {
                                    if (!string.IsNullOrWhiteSpace(tempExtractDir) && System.IO.Directory.Exists(tempExtractDir))
                                    {
                                        try { System.IO.Directory.Delete(tempExtractDir, true); } catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var importResult = await _fileFinalizer.ImportSingleFileAsync(downloadId, download?.AudiobookId, finalPath, settings);
                                _logger.LogInformation("FileFinalizer.ImportSingleFileAsync result for download {DownloadId}: Success={Success}, FinalPath={FinalPath}", downloadId, importResult?.Success, importResult?.FinalPath);

                                if (importResult != null && importResult.Success && !string.IsNullOrWhiteSpace(importResult.FinalPath))
                                {
                                    try
                                    {
                                        var tracked = await _downloadRepository.FindAsync(downloadId);
                                        if (tracked != null)
                                        {
                                            tracked.FinalPath = importResult.FinalPath;
                                            tracked.Status = DownloadStatus.Moved;
                                            await _downloadRepository.UpdateAsync(tracked);
                                            _logger.LogInformation("Updated download {DownloadId} FinalPath to import result: {FinalPath}", downloadId, importResult.FinalPath);
                                            
                                            // Record successful import in history for idempotency
                                            if (_downloadHistoryService != null && !string.IsNullOrEmpty(tracked.DownloadClientId))
                                            {
                                                try
                                                {
                                                    await _downloadHistoryService.RecordImportedAsync(
                                                        tracked.Id,
                                                        tracked.DownloadClientId,
                                                        tracked.Title ?? "Unknown",
                                                        audiobookId: null);
                                                    _logger.LogInformation("Recorded successful import in history for download {DownloadId}", downloadId);
                                                }
                                                catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException) {
                                                    _logger.LogWarning(histEx, "Failed to record import in history for download {DownloadId} (non-critical)", downloadId);
                                                }
                                            }
                                        }

                                        try
                                        {
                                            var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                                            using var scopeSync2 = scopeFactoryToUse.CreateScope();
                                            var scopedDb2 = scopeSync2.ServiceProvider.GetService<ListenArrDbContext>();
                                            if (scopedDb2 != null)
                                            {
                                                var local2 = await scopedDb2.Downloads.FindAsync(downloadId);
                                                if (local2 != null)
                                                {
                                                    local2.FinalPath = importResult.FinalPath;
                                                    local2.Status = DownloadStatus.Moved;
                                                    _logger.LogDebug("Synchronized FinalPath into scoped ListenArrDbContext for {DownloadId}", downloadId);
                                                }
                                            }
                                        }
                                        catch (Exception sync2Ex) when (sync2Ex is not OperationCanceledException && sync2Ex is not OutOfMemoryException && sync2Ex is not StackOverflowException) {
                                            _logger.LogDebug(sync2Ex, "Failed to synchronize FinalPath into scoped ListenArrDbContext (non-fatal)");
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                        _logger.LogWarning(ex, "Failed to update Download.FinalPath after import for {DownloadId}", downloadId);
                                    }
                                }

                                if (importResult != null && importResult.Success && !string.IsNullOrWhiteSpace(importResult.FinalPath))
                                {
                                    try
                                    {
                                        var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                                        using var afScope = scopeFactoryToUse.CreateScope();

                                        int? candidateBitrate = null;
                                        try
                                        {
                                            var metadataSvc = afScope.ServiceProvider.GetService<IMetadataService>();
                                            if (metadataSvc != null)
                                            {
                                                var meta = await metadataSvc.ExtractFileMetadataAsync(importResult.FinalPath);
                                                candidateBitrate = meta?.Bitrate;
                                            }
                                        }
                                        catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) {
                                            candidateBitrate = null;
                                        }

                                        int? maxExistingBitrate = null;
                                        try
                                        {
                                            var scopedDb = afScope.ServiceProvider.GetService<ListenArrDbContext>();
                                            if (scopedDb != null && download != null && download.AudiobookId != null)
                                            {
                                                var existing = await scopedDb.AudiobookFiles
                                                    .Where(f => f.AudiobookId == download.AudiobookId && f.Bitrate.HasValue)
                                                    .Select(f => f.Bitrate!.Value)
                                                    .ToListAsync();
                                                if (existing.Any()) maxExistingBitrate = existing.Max();
                                            }
                                        }
                                        catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) {
                                            maxExistingBitrate = null;
                                        }

                                        if (maxExistingBitrate.HasValue && candidateBitrate.HasValue && maxExistingBitrate.Value >= candidateBitrate.Value)
                                        {
                                            _logger.LogInformation("Skipping registration of imported file for audiobook {AudiobookId} because existing quality {Existing} >= candidate {Candidate}", download?.AudiobookId, maxExistingBitrate.Value, candidateBitrate.Value);
                                        }
                                        else
                                        {
                                            var audioFileService = afScope.ServiceProvider.GetService<IAudioFileService>()
                                                ?? ActivatorUtilities.CreateInstance<AudioFileService>(afScope.ServiceProvider,
                                                    scopeFactoryToUse,
                                                    afScope.ServiceProvider.GetService<ILogger<AudioFileService>>() ?? new Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioFileService>(),
                                                    afScope.ServiceProvider.GetRequiredService<IMemoryCache>(),
                                                    afScope.ServiceProvider.GetRequiredService<MetadataExtractionLimiter>());

                                            if (download?.AudiobookId != null)
                                            {
                                                // Always store absolute path for downloads - metadata extraction needs full path
                                                var created = await audioFileService.EnsureAudiobookFileAsync(download.AudiobookId.Value, importResult.FinalPath, "download");
                                                if (created)
                                                {
                                                    _logger.LogInformation("Registered imported file to audiobook {AudiobookId}: {Path}", download.AudiobookId, importResult.FinalPath);
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                        _logger.LogWarning(ex, "ProcessCompletedDownloadAsync: failed to register imported single file to audiobook for download {DownloadId}", downloadId);
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogError(ex, "ProcessCompletedDownloadAsync: failed to import single file {FinalPath} for download {DownloadId}", finalPath, downloadId);
                        }
                    }
                }

                try
                {
                    var postImport = await _downloadRepository.FindAsync(downloadId);
                    if (postImport != null &&
                        postImport.Status == DownloadStatus.ImportPending)
                    {
                        // Download is still ImportPending after all import attempts.
                        // This can happen when:
                        //  - No importable files were found at all (FinalPath empty)
                        //  - Import was rejected by quality check (FinalPath may be pre-set from prior import)
                        // Mark as ImportBlocked to prevent infinite re-enqueue loops.
                        var reason = string.IsNullOrWhiteSpace(postImport.FinalPath)
                            ? "No importable files were found after download completion. Manual interaction is required."
                            : "Import was not successful (possible quality rejection or duplicate). Manual interaction is required.";

                        await MarkImportFailureAsync(
                            downloadId,
                            "NoImportableFiles",
                            reason,
                            forceBlock: true);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to evaluate post-import state for download {DownloadId}", downloadId);
                }

                // Add history entry and send notifications after successful import
                try
                {
                    var downloadForHistory = await _downloadRepository.FindAsync(downloadId);
                    if (downloadForHistory != null && downloadForHistory.Status == DownloadStatus.Moved && !string.IsNullOrWhiteSpace(downloadForHistory.FinalPath))
                    {
                        var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                        using var historyScope = scopeFactoryToUse.CreateScope();
                        var historyRepo = historyScope.ServiceProvider.GetService<IHistoryRepository>();
                        var configService = historyScope.ServiceProvider.GetService<IConfigurationService>();
                        
                        if (historyRepo != null)
                        {
                            // Determine client name if available
                            string clientName = "Unknown";
                            if (configService != null && !string.IsNullOrWhiteSpace(downloadForHistory.DownloadClientId))
                            {
                                var clientConfig = await configService.GetDownloadClientConfigurationAsync(downloadForHistory.DownloadClientId);
                                if (clientConfig != null)
                                {
                                    clientName = clientConfig.Name;
                                }
                            }
                            
                            var historyEntry = new Listenarr.Domain.Models.History
                            {
                                AudiobookId = downloadForHistory.AudiobookId,
                                AudiobookTitle = downloadForHistory.Title,
                                EventType = "Imported",
                                Message = $"Automatically imported from {clientName}",
                                Source = "AutoImport",
                                Timestamp = DateTime.UtcNow,
                                NotificationSent = false,
                                Data = System.Text.Json.JsonSerializer.Serialize(new { 
                                    DownloadId = downloadForHistory.Id,
                                    ClientName = clientName,
                                    FinalPath = downloadForHistory.FinalPath
                                })
                            };
                            await historyRepo.AddAsync(historyEntry);
                            _logger.LogInformation("Added history entry for automatic import of {DownloadId}", downloadId);
                            
                            // Send notification
                            try
                            {
                                var notificationService = historyScope.ServiceProvider.GetService<INotificationService>();
                                if (notificationService != null && configService != null)
                                {
                                    var webhooks = await configService.GetWebhookConfigurationsAsync();
                                    foreach (var webhook in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Imported")))
                                    {
                                        await notificationService.SendNotificationAsync(
                                            "Imported",
                                            new {
                                                AudiobookTitle = downloadForHistory.Title,
                                                DownloadClient = clientName,
                                                FilePath = downloadForHistory.FinalPath,
                                                Timestamp = DateTime.UtcNow
                                            },
                                            webhook.Url,
                                            webhook.Triggers
                                        );
                                    }
                                    
                                    // Mark notification as sent
                                    historyEntry.NotificationSent = true;
                                    await historyRepo.UpdateAsync(historyEntry);
                                }
                            }
                            catch (Exception notifyEx) when (notifyEx is not OperationCanceledException && notifyEx is not OutOfMemoryException && notifyEx is not StackOverflowException) {
                                _logger.LogWarning(notifyEx, "Failed to send import notification for {DownloadId}", downloadId);
                            }
                            
                            // Send toast notification for successful import
                            try
                            {
                                var toastService = historyScope.ServiceProvider.GetService<IToastService>();
                                if (toastService != null)
                                {
                                    // Get the actual audiobook name from the library
                                    string audiobookName = "your library";
                                    if (downloadForHistory.AudiobookId.HasValue)
                                    {
                                        try
                                        {
                                            var audiobookRepo = historyScope.ServiceProvider.GetService<IAudiobookRepository>();
                                            if (audiobookRepo != null)
                                            {
                                                var audiobook = await audiobookRepo.GetByIdAsync(downloadForHistory.AudiobookId.Value);
                                                if (audiobook != null && !string.IsNullOrEmpty(audiobook.Title))
                                                {
                                                    audiobookName = audiobook.Title;
                                                }
                                            }
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                            _logger.LogDebug(ex, "Failed to fetch audiobook name for notification");
                                        }
                                    }
                                    
                                    var downloadName = !string.IsNullOrEmpty(downloadForHistory.Title) ? downloadForHistory.Title : "Download";
                                    var message = $"{downloadName} has been imported into {audiobookName}";
                                    
                                    if (!importToastSent)
                                    {
                                        await toastService.PublishToastAsync(
                                            "success", 
                                            "Import Complete", 
                                            message,
                                            timeoutMs: 5000);
                                        importToastSent = true;
                                        _logger.LogDebug("Sent toast notification for imported download {DownloadId}", downloadId);
                                    }
                                }
                            }
                            catch (Exception toastEx) when (toastEx is not OperationCanceledException && toastEx is not OutOfMemoryException && toastEx is not StackOverflowException) {
                                _logger.LogDebug(toastEx, "Failed to send toast notification for {DownloadId}", downloadId);
                            }
                        }
                    }
                }
                catch (Exception historyEx) when (historyEx is not OperationCanceledException && historyEx is not OutOfMemoryException && historyEx is not StackOverflowException) {
                    _logger.LogWarning(historyEx, "Failed to add history entry or send notifications for {DownloadId}", downloadId);
                }

                // Cleanup from download client if configured
                try
                {
                    // Reload download to ensure it wasn't deleted by concurrent operations
                    var downloadForCleanup = await _downloadRepository.FindAsync(downloadId);
                    
                    _logger.LogDebug("Cleanup section: download is {IsNull}, DownloadClientId={ClientId}", 
                        downloadForCleanup == null ? "NULL" : "NOT NULL", 
                        downloadForCleanup?.DownloadClientId ?? "NULL");
                        
                    if (downloadForCleanup != null && !string.IsNullOrWhiteSpace(downloadForCleanup.DownloadClientId))
                    {
                        var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                        using var cleanupScope = scopeFactoryToUse.CreateScope();
                        var configService = cleanupScope.ServiceProvider.GetService<IConfigurationService>();
                        var downloadClientGateway = cleanupScope.ServiceProvider.GetService<IDownloadClientGateway>();
                        
                        _logger.LogDebug("Cleanup: configService={ConfigService}, gateway={Gateway}", 
                            configService == null ? "NULL" : "OK", 
                            downloadClientGateway == null ? "NULL" : "OK");
                        
                        if (configService != null && downloadClientGateway != null)
                        {
                            var clientConfig = await configService.GetDownloadClientConfigurationAsync(downloadForCleanup.DownloadClientId);
                            _logger.LogInformation("Cleanup: clientConfig={IsNull}, RemoveCompletedDownloads={Setting}", 
                                clientConfig == null ? "NULL" : "Found", 
                                clientConfig?.RemoveCompletedDownloads ?? "NULL");

                            // Skip cleanup if the download client is disabled
                            if (clientConfig != null && !clientConfig.IsEnabled)
                            {
                                _logger.LogDebug("Skipping post-import cleanup for download {DownloadId}: client {ClientName} is disabled",
                                    downloadForCleanup.Id, clientConfig.Name);
                            }
                            else if (clientConfig != null && !string.IsNullOrEmpty(clientConfig.RemoveCompletedDownloads) && 
                                clientConfig.RemoveCompletedDownloads != "none")
                            {
                                // Sonarr parity: Mark item as imported (e.g., change torrent category) before removal.
                                // This ensures the torrent is properly categorized even if removal is deferred.
                                string clientIdForMark = downloadForCleanup.Id;
                                if (downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("TorrentHash", out var markHashObj))
                                {
                                    var markHash = markHashObj?.ToString();
                                    if (!string.IsNullOrEmpty(markHash))
                                        clientIdForMark = markHash;
                                }
                                try
                                {
                                    await downloadClientGateway.MarkItemAsImportedAsync(clientConfig, clientIdForMark);
                                }
                                catch (Exception markEx) when (markEx is not OperationCanceledException && markEx is not OutOfMemoryException && markEx is not StackOverflowException)
                                {
                                    _logger.LogDebug(markEx, "Failed to mark download {DownloadId} as imported in client (non-fatal)", downloadForCleanup.Id);
                                }

                                // Sonarr parity: Check CanBeRemoved flag before attempting removal.
                                // If the torrent hasn't reached its seed limit, defer removal to the next cycle.
                                bool canBeRemoved = true; // Default true for usenet clients
                                if (downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("CanBeRemoved", out var canRemoveObj))
                                {
                                    canBeRemoved = canRemoveObj is bool b ? b : (canRemoveObj is System.Text.Json.JsonElement je ? je.GetBoolean() : bool.TryParse(canRemoveObj?.ToString(), out var parsed) && parsed);
                                }

                                if (!canBeRemoved)
                                {
                                    _logger.LogInformation("Download {DownloadId} cannot be removed yet (CanBeRemoved=false, torrent still seeding). Deferring removal to next cycle.",
                                        downloadForCleanup.Id);
                                    // Don't remove - let the monitor service update CanBeRemoved on the next poll
                                    // when the torrent eventually reaches its seed limit
                                }
                                else
                                {
                                bool deleteFiles = clientConfig.RemoveCompletedDownloads == "remove_and_delete";
                                
                                // Get the actual client-specific ID (torrent hash for qBittorrent/Transmission, droneId for NZBGet, etc.)
                                string clientId = downloadForCleanup.Id;
                                
                                if ((clientConfig.Type.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                                     clientConfig.Type.Equals("transmission", StringComparison.OrdinalIgnoreCase)) && 
                                    downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("TorrentHash", out var hashObj))
                                {
                                    var torrentHash = hashObj?.ToString();
                                    if (!string.IsNullOrEmpty(torrentHash))
                                    {
                                        clientId = torrentHash;
                                        _logger.LogDebug("Using torrent hash {Hash} instead of download ID for {ClientType} removal", torrentHash, clientConfig.Type);
                                    }
                                }
                                else if (clientConfig.Type.Equals("nzbget", StringComparison.OrdinalIgnoreCase) && 
                                         downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("TorrentHash", out var droneIdObj))
                                {
                                    // For NZBGet, TorrentHash actually contains the droneId (GUID)
                                    var droneId = droneIdObj?.ToString();
                                    if (!string.IsNullOrEmpty(droneId))
                                    {
                                        clientId = droneId;
                                        _logger.LogDebug("Using droneId {DroneId} instead of download ID for NZBGet removal", droneId);
                                    }
                                }
                                
                                var removed = await downloadClientGateway.RemoveAsync(clientConfig, clientId, deleteFiles);
                                
                                if (removed)
                                {
                                    _logger.LogInformation("Removed download {DownloadId} from client {ClientName} (deleteFiles={DeleteFiles})", 
                                        downloadForCleanup.Id, clientConfig.Name, deleteFiles);
                                    
                                    // Log to history
                                    var historyRepo = cleanupScope.ServiceProvider.GetService<IHistoryRepository>();
                                    if (historyRepo != null)
                                    {
                                        var historyEntry = new Listenarr.Domain.Models.History
                                        {
                                            AudiobookId = downloadForCleanup.AudiobookId,
                                            AudiobookTitle = downloadForCleanup.Title,
                                            EventType = "Imported",
                                            Message = $"Automatically imported and removed from {clientConfig.Name}. Files deleted: {deleteFiles}",
                                            Source = "AutoImport",
                                            Timestamp = DateTime.UtcNow,
                                            NotificationSent = false,
                                            Data = System.Text.Json.JsonSerializer.Serialize(new { 
                                                DownloadId = downloadForCleanup.Id,
                                                ClientName = clientConfig.Name,
                                                ClientType = clientConfig.Type,
                                                FilesDeleted = deleteFiles,
                                                FinalPath = downloadForCleanup.FinalPath
                                            })
                                        };
                                        await historyRepo.AddAsync(historyEntry);
                                        _logger.LogInformation("Added history entry for automatic import of {DownloadId}", downloadId);
                                        
                                        // Send notification
                                        try
                                        {
                                            var notificationService = cleanupScope.ServiceProvider.GetService<INotificationService>();
                                            if (notificationService != null)
                                            {
                                                var webhooks = await configService.GetWebhookConfigurationsAsync();
                                                foreach (var webhook in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Imported")))
                                                {
                                                    await notificationService.SendNotificationAsync(
                                                        "Imported",
                                                        new {
                                                            AudiobookTitle = downloadForCleanup.Title,
                                                            DownloadClient = clientConfig.Name,
                                                            FilePath = downloadForCleanup.FinalPath,
                                                            RemovedFromClient = true,
                                                            FilesDeleted = deleteFiles,
                                                            Timestamp = DateTime.UtcNow
                                                        },
                                                        webhook.Url,
                                                        webhook.Triggers
                                                    );
                                                }
                                                
                                                // Mark notification as sent
                                                historyEntry.NotificationSent = true;
                                                await historyRepo.UpdateAsync(historyEntry);
                                            }
                                        }
                                        catch (Exception notifyEx) when (notifyEx is not OperationCanceledException && notifyEx is not OutOfMemoryException && notifyEx is not StackOverflowException) {
                                            _logger.LogWarning(notifyEx, "Failed to send import notification for {DownloadId}", downloadId);
                                        }
                                    }
                                    
                                    // Send toast notification for successful import
                                    try
                                    {
                                        var toastService = cleanupScope.ServiceProvider.GetService<IToastService>();
                                        if (toastService != null)
                                        {
                                            // Get the actual audiobook name from the library
                                            string audiobookName = "your library";
                                            if (downloadForCleanup.AudiobookId.HasValue)
                                            {
                                                try
                                                {
                                                    var audiobookRepo = cleanupScope.ServiceProvider.GetService<IAudiobookRepository>();
                                                    if (audiobookRepo != null)
                                                    {
                                                        var audiobook = await audiobookRepo.GetByIdAsync(downloadForCleanup.AudiobookId.Value);
                                                        if (audiobook != null && !string.IsNullOrEmpty(audiobook.Title))
                                                        {
                                                            audiobookName = audiobook.Title;
                                                        }
                                                    }
                                                }
                                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                                    _logger.LogDebug(ex, "Failed to fetch audiobook name for notification");
                                                }
                                            }
                                            
                                            var downloadName = !string.IsNullOrEmpty(downloadForCleanup.Title) ? downloadForCleanup.Title : "Download";
                                            var message = clientConfig.RemoveCompletedDownloads == "remove_and_delete" 
                                                ? $"{downloadName} has been imported into {audiobookName} and files deleted"
                                                : $"{downloadName} has been imported into {audiobookName}";
                                            
                                            if (!importToastSent)
                                            {
                                                await toastService.PublishToastAsync(
                                                    "success", 
                                                    "Import Complete", 
                                                    message,
                                                    timeoutMs: 5000); // Auto-dismiss after 5 seconds
                                                importToastSent = true;
                                                _logger.LogDebug("Sent toast notification for imported download {DownloadId}", downloadId);
                                            }
                                        }
                                    }
                                    catch (Exception toastEx) when (toastEx is not OperationCanceledException && toastEx is not OutOfMemoryException && toastEx is not StackOverflowException) {
                                        _logger.LogDebug(toastEx, "Failed to send toast notification for {DownloadId}", downloadId);
                                    }
                                    
                                    // Delete the download record from database after successful cleanup
                                    try
                                    {
                                        var dbContext = cleanupScope.ServiceProvider.GetService<ListenArrDbContext>();
                                        if (dbContext != null)
                                        {
                                            var downloadToDelete = await dbContext.Downloads.FindAsync(downloadId);
                                            if (downloadToDelete != null)
                                            {
                                                dbContext.Downloads.Remove(downloadToDelete);
                                                await dbContext.SaveChangesAsync();
                                                _logger.LogInformation("Deleted download {DownloadId} from database after successful cleanup", downloadId);
                                                
                                                // Small delay to ensure database changes are visible to other contexts
                                                await Task.Delay(100);
                                                
                                                // Broadcast queue update after deletion so frontend sees the updated state
                                                try
                                                {
                                                    var currentQueue = await _downloadQueueService.GetQueueAsync();
                                                    if (_hubBroadcaster != null)
                                                    {
                                                        await _hubBroadcaster.BroadcastQueueUpdateAsync(currentQueue);
                                                        _logger.LogDebug("Broadcasted QueueUpdate after deleting download {DownloadId}", downloadId);
                                                    }
                                                }
                                                catch (Exception broadcastEx) when (broadcastEx is not OperationCanceledException && broadcastEx is not OutOfMemoryException && broadcastEx is not StackOverflowException) {
                                                    _logger.LogDebug(broadcastEx, "Failed to broadcast QueueUpdate after deletion");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception deleteEx) when (deleteEx is not OperationCanceledException && deleteEx is not OutOfMemoryException && deleteEx is not StackOverflowException) {
                                        _logger.LogWarning(deleteEx, "Failed to delete download {DownloadId} from database", downloadId);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("Failed to remove download {DownloadId} from client {ClientName}", 
                                        download!.Id, clientConfig.Name);
                                }
                                } // end else (canBeRemoved)
                            }
                        }
                    }
                }
                catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException && cleanupEx is not OutOfMemoryException && cleanupEx is not StackOverflowException) {
                    _logger.LogError(cleanupEx, "Error during post-import cleanup for {DownloadId}", downloadId);
                }

                try
                {
                    var currentQueue = await _downloadQueueService.GetQueueAsync();
                    if (_hubBroadcaster != null)
                    {
                        await _hubBroadcaster.BroadcastQueueUpdateAsync(currentQueue);
                        _logger.LogInformation("Broadcasted QueueUpdate via IHubBroadcaster after processing download {DownloadId}", downloadId);
                    }
                    else
                    {
                        await _hubContext.Clients.All.SendAsync("QueueUpdate", currentQueue);
                        try
                        {
                            var clientProxy = _hubContext?.Clients?.All;
                            if (clientProxy != null)
                            {
                                await clientProxy.SendCoreAsync("QueueUpdate", new object[] { currentQueue }, System.Threading.CancellationToken.None);
                            }
                        }
                        catch (Exception exInner) when (exInner is not OperationCanceledException && exInner is not OutOfMemoryException && exInner is not StackOverflowException) {
                            _logger.LogDebug(exInner, "Direct SendCoreAsync for QueueUpdate failed (non-fatal)");
                        }

                        _logger.LogInformation("Broadcasted QueueUpdate after processing download {DownloadId}", downloadId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to broadcast QueueUpdate after processing download {DownloadId}", downloadId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Unexpected error in ProcessCompletedDownloadAsync for {DownloadId}", downloadId);

                await MarkImportFailureAsync(
                    downloadId,
                    "UnhandledImportError",
                    ex.Message ?? "Unexpected import processing error",
                    ex,
                    forceBlock: false);
            }
        }

        private async Task MarkImportFailureAsync(
            string downloadId,
            string reason,
            string message,
            Exception? exception = null,
            bool forceBlock = false)
        {
            try
            {
                var download = await _downloadRepository.FindAsync(downloadId);
                if (download == null)
                {
                    return;
                }

                download.ImportAttempts = download.ImportAttempts + 1;
                const int MaxImportAttempts = 3;

                var shouldBlock = forceBlock || download.ImportAttempts >= MaxImportAttempts;
                download.ErrorMessage = message;

                var targetStatus = shouldBlock ? DownloadStatus.ImportBlocked : DownloadStatus.ImportPending;
                if (!TryTransitionStatus(download, targetStatus, "MarkImportFailure"))
                {
                    return;
                }

                if (shouldBlock)
                {
                    download.ImportBlockReason = reason;
                    download.ImportBlockMessages ??= new List<string>();
                    if (!download.ImportBlockMessages.Contains(message))
                    {
                        download.ImportBlockMessages.Add(message);
                    }
                }

                await _downloadRepository.UpdateAsync(download);

                if (_downloadHistoryService != null && !string.IsNullOrEmpty(download.DownloadClientId))
                {
                    var detail = message;
                    if (exception != null && exception.InnerException != null)
                    {
                        detail += $" | Inner: {exception.InnerException.Message}";
                    }

                    await _downloadHistoryService.RecordImportFailedAsync(
                        download.Id,
                        download.DownloadClientId,
                        download.Title ?? "Unknown",
                        detail);
                }

                if (shouldBlock)
                {
                    _logger.LogWarning(
                        "Download {DownloadId} import blocked (Reason: {Reason}, Attempts: {Attempts})",
                        downloadId,
                        reason,
                        download.ImportAttempts);

                    var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                    using var scope = scopeFactoryToUse.CreateScope();

                    var toastService = scope.ServiceProvider.GetService<IToastService>();
                    if (toastService != null)
                    {
                        var title = string.IsNullOrWhiteSpace(download.Title) ? "Download" : download.Title;
                        await toastService.PublishToastAsync(
                            "warning",
                            "Manual Interaction Required",
                            $"{title} could not be imported automatically and has been blocked.",
                            timeoutMs: 8000);
                    }

                    var historyRepo = scope.ServiceProvider.GetService<IHistoryRepository>();
                    if (historyRepo != null)
                    {
                        await historyRepo.AddAsync(new Listenarr.Domain.Models.History
                        {
                            AudiobookId = download.AudiobookId,
                            AudiobookTitle = download.Title,
                            EventType = "ImportBlocked",
                            Message = message,
                            Source = "AutoImport",
                            Timestamp = DateTime.UtcNow,
                            NotificationSent = false,
                            Data = JsonSerializer.Serialize(new
                            {
                                DownloadId = download.Id,
                                Reason = reason,
                                Attempts = download.ImportAttempts
                            })
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist import failure details for download {DownloadId}", downloadId);
            }
        }

        private bool TryTransitionStatus(Download download, DownloadStatus targetStatus, string transitionSource)
        {
            if (download == null)
            {
                return false;
            }

            var currentStatus = download.Status;
            if (IsValidStatusTransition(currentStatus, targetStatus))
            {
                download.Status = targetStatus;
                return true;
            }

            _logger.LogWarning(
                "Rejected invalid download status transition for {DownloadId}: {FromStatus} -> {ToStatus} (Source: {Source})",
                download.Id,
                currentStatus,
                targetStatus,
                transitionSource);

            try
            {
                _metrics.Increment("download.transition.rejected.invalid");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to emit invalid transition metric for download {DownloadId}", download.Id);
            }

            return false;
        }

        private static bool IsValidStatusTransition(DownloadStatus fromStatus, DownloadStatus toStatus)
        {
            if (fromStatus == toStatus)
            {
                return true;
            }

            return toStatus switch
            {
                DownloadStatus.ImportPending => fromStatus is DownloadStatus.Queued
                    or DownloadStatus.Downloading
                    or DownloadStatus.Paused
                    or DownloadStatus.Processing
                    or DownloadStatus.Completed,

                DownloadStatus.ImportBlocked => fromStatus is DownloadStatus.ImportPending
                    or DownloadStatus.Processing
                    or DownloadStatus.Completed
                    or DownloadStatus.Downloading,

                DownloadStatus.Moved => fromStatus is DownloadStatus.ImportPending
                    or DownloadStatus.Completed
                    or DownloadStatus.Processing
                    or DownloadStatus.Downloading,

                _ => true,
            };
        }
    }
}

