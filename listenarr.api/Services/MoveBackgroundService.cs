using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services
{
    public class MoveBackgroundService : BackgroundService
    {
        private readonly IMoveQueueService _moveQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MoveBackgroundService> _logger;

        public MoveBackgroundService(IMoveQueueService moveQueue, IServiceScopeFactory scopeFactory, ILogger<MoveBackgroundService> logger)
        {
            _moveQueue = moveQueue ?? throw new ArgumentNullException(nameof(moveQueue));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var job in _moveQueue.Reader.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    _logger.LogInformation("Processing move job {JobId} for audiobook {AudiobookId} to {Path}", job.Id, job.AudiobookId, job.RequestedPath);
                    _moveQueue.UpdateJobStatus(job.Id, "Processing");

                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                    var audiobook = await db.Audiobooks.FindAsync(new object[] { job.AudiobookId }, stoppingToken);
                    if (audiobook == null)
                    {
                        _moveQueue.UpdateJobStatus(job.Id, "Failed", "Audiobook not found");
                        continue;
                    }

                    // Prefer an enqueued source path snapshot if provided, otherwise use the audiobook's BasePath
                    var source = job.SourcePath;
                    if (!string.IsNullOrWhiteSpace(source) && !Directory.Exists(source))
                    {
                        _logger.LogWarning("Provided source path {Source} for job {JobId} does not exist; falling back to audiobook.BasePath", source, job.Id);
                        source = null;
                    }

                    if (string.IsNullOrWhiteSpace(source))
                    {
                        source = audiobook.BasePath;
                    }

                    if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                    {
                        _moveQueue.UpdateJobStatus(job.Id, "Failed", "Source path invalid or does not exist");
                        continue;
                    }

                    var requested = job.RequestedPath ?? string.Empty;

                    string target = requested;

                    if (string.IsNullOrWhiteSpace(target))
                    {
                        _moveQueue.UpdateJobStatus(job.Id, "Failed", "Target path not provided");
                        continue;
                    }

                    // Normalize full paths
                    target = Path.GetFullPath(target);
                    source = Path.GetFullPath(source);

                    // If source == target, nothing to do
                    if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        _moveQueue.UpdateJobStatus(job.Id, "Completed");
                        continue;
                    }

                    // Ensure target parent exists
                    var targetParent = Path.GetDirectoryName(target);
                    if (string.IsNullOrEmpty(targetParent))
                    {
                        _moveQueue.UpdateJobStatus(job.Id, "Failed", "Invalid target path");
                        continue;
                    }

                    if (!Directory.Exists(targetParent)) Directory.CreateDirectory(targetParent);

                    // Check if target exists and has content - only fail if it has files/folders we'd overwrite
                    if (Directory.Exists(target))
                    {
                        var targetHasContent = Directory.EnumerateFileSystemEntries(target).Any();
                        if (targetHasContent)
                        {
                            _moveQueue.UpdateJobStatus(job.Id, "Failed", "Target directory already exists and contains files");
                            continue;
                        }
                        // Target exists but is empty - safe to proceed (will use it instead of creating new)
                        _logger.LogInformation("Target directory {Target} exists but is empty; proceeding with move", target);
                    }

                    // Create a temporary directory under the target parent
                    var tempName = Path.Combine(targetParent, Path.GetFileName(target) + ".tmp-" + job.Id.ToString("N"));

                    // Copy recursively with retries per file
                    try
                    {
                        // Only create tempName if target doesn't exist; otherwise copy directly into existing empty target
                        var useTemp = !Directory.Exists(target);
                        var copyDest = useTemp ? tempName : target;
                        
                        if (useTemp) Directory.CreateDirectory(tempName);
                        
                        var entries = Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories);
                        foreach (var entry in entries)
                        {
                            var rel = Path.GetRelativePath(source, entry);
                            var destPath = CombineWithOptionalBase(copyDest, rel);

                            if (Directory.Exists(entry))
                            {
                                if (!Directory.Exists(destPath)) Directory.CreateDirectory(destPath);
                                continue;
                            }

                            // Ensure dest directory exists
                            var ddir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(ddir) && !Directory.Exists(ddir)) Directory.CreateDirectory(ddir);

                            // Copy file with retry/backoff and preserve timestamps/attributes on success
                            var succeeded = false;
                            const int maxAttempts = 5;
                            for (int attempt = 1; attempt <= maxAttempts; attempt++)
                            {
                                try
                                {
                                    File.Copy(entry, destPath, false);

                                    // Preserve file attributes and timestamps
                                    try
                                    {
                                        var attrs = File.GetAttributes(entry);
                                        File.SetAttributes(destPath, attrs);

                                        var lastWrite = File.GetLastWriteTimeUtc(entry);
                                        var creation = File.GetCreationTimeUtc(entry);
                                        File.SetLastWriteTimeUtc(destPath, lastWrite);
                                        File.SetCreationTimeUtc(destPath, creation);
                                    }
                                    catch (Exception attrEx) when (attrEx is not OperationCanceledException && attrEx is not OutOfMemoryException && attrEx is not StackOverflowException) {
                                        _logger.LogDebug(attrEx, "Non-fatal: failed to preserve attributes for {File}", entry);
                                    }

                                    succeeded = true;
                                    break;
                                }
                                catch (IOException ioex)
                                {
                                    _logger.LogWarning(ioex, "IO error copying file {File} attempt {Attempt}", entry, attempt);

                                    // exponential backoff
                                    var delay = TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));
                                    await Task.Delay(delay, stoppingToken);
                                }
                            }

                            if (!succeeded)
                            {
                                // Increment attempt count for the DB job to surface retries
                                try
                                {
                                    var dbJob = db.MoveJobs.FirstOrDefault(j => j.Id == job.Id);
                                    if (dbJob != null)
                                    {
                                        dbJob.AttemptCount += 1;
                                        db.MoveJobs.Update(dbJob);
                                        await db.SaveChangesAsync(stoppingToken);
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                    _logger.LogWarning(ex, "Failed to increment AttemptCount for job {JobId}", job.Id);
                                }

                                throw new Exception($"Failed to copy file after {maxAttempts} attempts: {entry}");
                            }
                        }

                        // After successful copy, finalize the move
                        if (useTemp)
                        {
                            // Move temp to final target (atomic on same volume)
                            Directory.Move(tempName, target);
                        }
                        // If we copied directly to target, it's already in place

                        // Delete source directory
                        Directory.Delete(source, true);

                        // Update DB audiobook BasePath to new target
                        audiobook.BasePath = target;
                        db.Audiobooks.Update(audiobook);
                        await db.SaveChangesAsync(stoppingToken);

                        // Preserve local image path if it pointed inside the source directory
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(audiobook.ImageUrl))
                            {
                                var imageUrl = audiobook.ImageUrl;

                                // Only attempt to rewrite file-system paths (skip /api/images/ or URLs)
                                bool looksLikeFsPath = Path.IsPathRooted(imageUrl) || imageUrl.StartsWith(source, StringComparison.OrdinalIgnoreCase) || imageUrl.StartsWith(source.Replace(Path.DirectorySeparatorChar, '/'), StringComparison.OrdinalIgnoreCase);
                                if (looksLikeFsPath)
                                {
                                    try
                                    {
                                        var fullImagePath = Path.IsPathRooted(imageUrl)
                                            ? Path.GetFullPath(imageUrl)
                                            : Path.GetFullPath(CombineWithOptionalBase(source, imageUrl));
                                        if (fullImagePath.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                                        {
                                            var rel = Path.GetRelativePath(source, fullImagePath);
                                            var newImagePath = Path.GetFullPath(CombineWithOptionalBase(target, rel));

                                            // Only update if the new file actually exists after move
                                            if (System.IO.File.Exists(newImagePath))
                                            {
                                                audiobook.ImageUrl = newImagePath;
                                                db.Audiobooks.Update(audiobook);
                                                await db.SaveChangesAsync(stoppingToken);
                                                _logger.LogInformation("Updated ImageUrl for audiobook {AudiobookId} to new path after move", audiobook.Id);
                                            }
                                        }
                                    }
                                    catch (Exception innerEx) when (innerEx is not OperationCanceledException && innerEx is not OutOfMemoryException && innerEx is not StackOverflowException) {
                                        _logger.LogDebug(innerEx, "Non-fatal: failed to update ImageUrl after move for audiobook {AudiobookId}", audiobook.Id);
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogDebug(ex, "Non-fatal: error while attempting to preserve ImageUrl for audiobook {AudiobookId}", audiobook.Id);
                        }

                        // Preserve legacy single-file FilePath if it pointed inside the source directory
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
                            {
                                var fullFilePath = Path.IsPathRooted(audiobook.FilePath)
                                    ? Path.GetFullPath(audiobook.FilePath)
                                    : Path.GetFullPath(CombineWithOptionalBase(source, audiobook.FilePath));

                                if (fullFilePath.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                                {
                                    var rel = Path.GetRelativePath(source, fullFilePath);
                                    var newFilePath = Path.GetFullPath(CombineWithOptionalBase(target, rel));

                                    // Only update if the new file actually exists after move
                                    if (System.IO.File.Exists(newFilePath))
                                    {
                                        audiobook.FilePath = newFilePath;
                                        db.Audiobooks.Update(audiobook);
                                        await db.SaveChangesAsync(stoppingToken);
                                        _logger.LogInformation("Updated FilePath for audiobook {AudiobookId} to new path after move", audiobook.Id);
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogDebug(ex, "Non-fatal: failed to update FilePath after move for audiobook {AudiobookId}", audiobook.Id);
                        }

                        // Add history entry and send notifications for the move
                        try
                        {
                            using var historyScope = _scopeFactory.CreateScope();
                            var historyRepo = historyScope.ServiceProvider.GetService<IHistoryRepository>();
                            var configService = historyScope.ServiceProvider.GetService<IConfigurationService>();

                            if (historyRepo != null)
                            {
                                var historyEntry = new Listenarr.Domain.Models.History
                                {
                                    AudiobookId = audiobook.Id,
                                    AudiobookTitle = audiobook.Title,
                                    EventType = "Moved",
                                    Message = $"Moved audiobook files from {source} to {target}",
                                    Source = "Move",
                                    Timestamp = DateTime.UtcNow,
                                    NotificationSent = false,
                                    Data = System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        JobId = job.Id,
                                        Source = source,
                                        Target = target
                                    })
                                };

                                await historyRepo.AddAsync(historyEntry);
                                _logger.LogInformation("Added history entry for move job {JobId}", job.Id);

                                // Send webhook notifications if configured
                                try
                                {
                                    var notificationService = historyScope.ServiceProvider.GetService<INotificationService>();
                                    if (notificationService != null && configService != null)
                                    {
                                        var webhooks = await configService.GetWebhookConfigurationsAsync();
                                        foreach (var webhook in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Moved")))
                                        {
                                            await notificationService.SendNotificationAsync(
                                                "Moved",
                                                new
                                                {
                                                    AudiobookTitle = audiobook.Title,
                                                    Source = source,
                                                    Target = target,
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
                                    _logger.LogWarning(notifyEx, "Failed to send move notification for {JobId}", job.Id);
                                }

                                // Send toast notification
                                try
                                {
                                    var toastService = historyScope.ServiceProvider.GetService<IToastService>();
                                    if (toastService != null)
                                    {
                                        var message = !string.IsNullOrEmpty(audiobook.Title)
                                            ? $"Moved {audiobook.Title} to {target}"
                                            : $"Moved audiobook to {target}";

                                        await toastService.PublishToastAsync(
                                            "success",
                                            "Move Complete",
                                            message,
                                            timeoutMs: 5000);

                                        _logger.LogDebug("Sent toast notification for move job {JobId}", job.Id);
                                    }
                                }
                                catch (Exception toastEx) when (toastEx is not OperationCanceledException && toastEx is not OutOfMemoryException && toastEx is not StackOverflowException) {
                                    _logger.LogDebug(toastEx, "Failed to send toast notification for move job {JobId}", job.Id);
                                }

                                // Enqueue a scan job and broadcast an immediate AudiobookUpdate so detail views update promptly
                                try
                                {
                                    var scanQueue = historyScope.ServiceProvider.GetService<IScanQueueService>();
                                    if (scanQueue != null)
                                    {
                                        var scanJobId = await scanQueue.EnqueueScanAsync(audiobook.Id, null);
                                        _logger.LogInformation("Enqueued scan job {ScanJobId} for audiobook {AudiobookId} after move", scanJobId, audiobook.Id);
                                    }

                                    var hubContext = historyScope.ServiceProvider.GetService<Microsoft.AspNetCore.SignalR.IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
                                    if (hubContext != null)
                                    {
                                        // Load latest audiobook state and broadcast a full DTO so clients can update instantly without fetching
                                        try
                                        {
                                            var fresh = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == audiobook.Id);
                                            if (fresh != null)
                                            {
                                                var audiobookDtoFull = Listenarr.Api.Services.AudiobookDtoFactory.BuildFromEntity(db, fresh);
                                                await hubContext.Clients.All.SendAsync("AudiobookUpdate", audiobookDtoFull);
                                                _logger.LogInformation("Broadcasted full AudiobookUpdate for AudiobookId {AudiobookId} after move job {JobId}", audiobook.Id, job.Id);
                                            }
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                            _logger.LogWarning(ex, "Failed to broadcast full AudiobookUpdate for AudiobookId {AudiobookId} after move job {JobId}", audiobook.Id, job.Id);
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                    _logger.LogWarning(ex, "Failed to enqueue scan or broadcast AudiobookUpdate after move job {JobId}", job.Id);
                                }
                            }
                        }
                        catch (Exception historyEx) when (historyEx is not OperationCanceledException && historyEx is not OutOfMemoryException && historyEx is not StackOverflowException) {
                            _logger.LogWarning(historyEx, "Failed to add history entry or send notifications for move job {JobId}", job.Id);
                        }

                        _moveQueue.UpdateJobStatus(job.Id, "Completed");
                        _logger.LogInformation("Move job {JobId} completed: {Source} -> {Target}", job.Id, source, target);
                        // Completed move job — status updated and broadcasted where configured
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        // Cleanup any temp dir
                        try { if (Directory.Exists(tempName)) Directory.Delete(tempName, true); } catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }

                        // Increment attempt count for the job on failure
                        try
                        {
                            var dbJob = db.MoveJobs.FirstOrDefault(j => j.Id == job.Id);
                            if (dbJob != null)
                            {
                                dbJob.AttemptCount += 1;
                                db.MoveJobs.Update(dbJob);
                                await db.SaveChangesAsync(stoppingToken);
                            }
                        }
                        catch (Exception attEx) when (attEx is not OperationCanceledException && attEx is not OutOfMemoryException && attEx is not StackOverflowException) {
                            _logger.LogWarning(attEx, "Failed to increment AttemptCount for job {JobId} after failure", job.Id);
                        }

                        // Record failure in history and send a toast notification
                        try
                        {
                            using var historyScope = _scopeFactory.CreateScope();
                            var historyRepo = historyScope.ServiceProvider.GetService<IHistoryRepository>();
                            if (historyRepo != null)
                            {
                                var historyEntry = new Listenarr.Domain.Models.History
                                {
                                    AudiobookId = audiobook?.Id,
                                    AudiobookTitle = audiobook?.Title,
                                    EventType = "MoveFailed",
                                    Message = $"Move failed: {ex.Message}",
                                    Source = "Move",
                                    Timestamp = DateTime.UtcNow,
                                    NotificationSent = false,
                                    Data = System.Text.Json.JsonSerializer.Serialize(new { JobId = job.Id, Error = ex.Message })
                                };

                                await historyRepo.AddAsync(historyEntry);
                                _logger.LogInformation("Added history entry for failed move job {JobId}", job.Id);

                                try
                                {
                                    var toastService = historyScope.ServiceProvider.GetService<IToastService>();
                                    if (toastService != null)
                                    {
                                        var message = !string.IsNullOrEmpty(audiobook?.Title)
                                            ? $"Failed to move {audiobook.Title}: {ex.Message}"
                                            : $"Move failed: {ex.Message}";

                                        await toastService.PublishToastAsync("error", "Move Failed", message, timeoutMs: 15000);
                                        _logger.LogDebug("Sent toast notification for failed move job {JobId}", job.Id);
                                    }
                                }
                                catch (Exception toastEx) when (toastEx is not OperationCanceledException && toastEx is not OutOfMemoryException && toastEx is not StackOverflowException) {
                                    _logger.LogDebug(toastEx, "Failed to send toast notification for failed move job {JobId}", job.Id);
                                }
                            }
                        }
                        catch (Exception historyEx) when (historyEx is not OperationCanceledException && historyEx is not OutOfMemoryException && historyEx is not StackOverflowException) {
                            _logger.LogWarning(historyEx, "Failed to add history entry for failed move job {JobId}", job.Id);
                        }

                        _moveQueue.UpdateJobStatus(job.Id, "Failed", ex.Message);
                        _logger.LogError(ex, "Move job {JobId} failed", job.Id);
                        // Failure during move job — attempt counts updated and history recorded where configured
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Unexpected error processing move job {JobId}", job.Id);
                    try { _moveQueue.UpdateJobStatus(job.Id, "Failed", ex.Message); } catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                }
            }
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
    }
}


