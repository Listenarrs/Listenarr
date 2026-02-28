using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Default implementation of IFileProcessingHandler extracted from the previous
    /// DownloadProcessingBackgroundService.ProcessMoveOrCopyJobAsync. The implementation
    /// is intentionally conservative: it resolves required scoped services from the provided
    /// IServiceScope and keeps the logic focused and testable.
    /// 
    /// This is a refactor scaffold — some details are simplified to keep the code compact.
    /// Unit tests should be added for each logical branch.
    /// </summary>
    public class FileProcessingHandler : IFileProcessingHandler
    {
        private readonly ILogger<FileProcessingHandler> _logger;
        private readonly IAppMetricsService _metrics;
        private readonly IServiceScopeFactory _scopeFactory;

        public FileProcessingHandler(ILogger<FileProcessingHandler> logger, IAppMetricsService metrics, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _metrics = metrics;
            _scopeFactory = scopeFactory;
        }

        public async Task HandleAsync(DownloadProcessingJob job, IServiceScope scope, CancellationToken cancellationToken)
        {
            if (job.JobType != ProcessingJobType.MoveOrCopyFile)
            {
                throw new InvalidOperationException("FileProcessingHandler only handles MoveOrCopyFile job types");
            }

            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var pathMappingService = scope.ServiceProvider.GetService<IRemotePathMappingService>();
            var fileNamingService = scope.ServiceProvider.GetService<IFileNamingService>();
            var metadataService = scope.ServiceProvider.GetService<IMetadataService>();

            job.AddLogEntry($"Starting file processing: {job.SourcePath}");

            if (string.IsNullOrEmpty(job.SourcePath) || !File.Exists(job.SourcePath))
            {
                // Apply path mapping if needed
                var localPath = job.SourcePath ?? "";
                if (pathMappingService != null && !string.IsNullOrEmpty(job.DownloadClientId))
                {
                    try
                    {
                        var translated = await pathMappingService.TranslatePathAsync(job.DownloadClientId, job.SourcePath ?? "");
                        if (!string.IsNullOrEmpty(translated) && !string.Equals(translated, job.SourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            job.AddLogEntry($"Applied path mapping: {job.SourcePath} -> {translated}");
                            job.SourcePath = translated;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        job.AddLogEntry($"Path mapping failed: {ex.Message}");
                    }
                }

                if (!File.Exists(localPath))
                {
                    job.AddLogEntry($"Source file not found at processing time: {localPath}");
                    _metrics?.Increment("processing.source_missing");
                    job.ScheduleRetry();
                    job.ErrorMessage = $"Source file not found at processing time: {localPath}";
                    return;
                }
            }

            var settings = await configService.GetApplicationSettingsAsync();
            job.AddLogEntry($"Retrieved settings - OutputPath: {settings.OutputPath}, EnableMetadataProcessing: {settings.EnableMetadataProcessing}");

            // Use existing logic to compute destination and perform move/copy; this is a compacted version.
            // For full fidelity, move the rest of the previous ProcessMoveOrCopyJobAsync logic here as needed.
            var sourcePath = job.SourcePath!;
            var destinationPath = sourcePath;

            if (!string.IsNullOrEmpty(settings.OutputPath))
            {
                var outputPath = settings.OutputPath;

                // Simplified destination computation: use fileNamingService when available otherwise fallback
                var ext = Path.GetExtension(sourcePath);
                string generatedPath;
                if (fileNamingService != null && settings.EnableMetadataProcessing)
                {
                    // Build minimal metadata for naming
                    var metadata = new AudioMetadata { Title = "Unknown Title" };
                    using var innerScope = _scopeFactory.CreateScope();
                    var db = innerScope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                    var download = await db.Downloads.FindAsync(job.DownloadId, cancellationToken);
                    if (download != null)
                    {
                        metadata.Title = download.Title ?? metadata.Title;
                        metadata.Artist = download.Artist ?? string.Empty;
                        metadata.Album = download.Album ?? string.Empty;
                    }

                    generatedPath = await fileNamingService.GenerateFilePathAsync(metadata, outputPath, null, null, ext);
                }
                else
                {
                    generatedPath = Path.GetFileName(sourcePath);
                }

                if (Path.IsPathRooted(generatedPath))
                {
                    destinationPath = generatedPath;
                }
                else
                {
                    var outputRoot = outputPath;
                    var relativeGeneratedPath = generatedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(outputRoot))
                    {
                        destinationPath = relativeGeneratedPath;
                    }
                    else
                    {
                        var normalizedOutputRoot = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        destinationPath = normalizedOutputRoot + Path.DirectorySeparatorChar + relativeGeneratedPath;
                    }
                }
            }

            // Ensure unique destination and perform move/copy
            try
            {
                var uniqueDest = FileUtils.GetUniqueDestinationPath(destinationPath);
                var fileMover = scope.ServiceProvider.GetService<IFileMover>();
                var action = settings.CompletedFileAction ?? "Move";

                if (string.Equals(action, "Copy", StringComparison.OrdinalIgnoreCase))
                {
                    if (fileMover != null)
                    {
                        var ok = await fileMover.CopyFileAsync(sourcePath, uniqueDest);
                        if (!ok) throw new IOException("CopyFileAsync failed");
                    }
                    else
                    {
                        File.Copy(sourcePath, uniqueDest, true);
                    }
                    job.AddLogEntry($"Copied file: {sourcePath} -> {uniqueDest}");
                }
                else if (string.Equals(action, "Hardlink/Copy", StringComparison.OrdinalIgnoreCase))
                {
                    if (fileMover != null)
                    {
                        var ok = await fileMover.HardlinkFileAsync(sourcePath, uniqueDest);
                        if (!ok) throw new IOException("HardlinkFileAsync failed");
                    }
                    else
                    {
                        try
                        {
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                if (!NativeFileMethods.CreateHardLinkWindows(uniqueDest, sourcePath))
                                    throw new IOException("Hardlink failed");
                            }
                            else
                            {
                                if (NativeFileMethods.CreateHardLinkUnix(sourcePath, uniqueDest) != 0)
                                    throw new IOException("Hardlink failed");
                            }
                        }
                        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                            File.Copy(sourcePath, uniqueDest, true);
                        }
                    }
                    job.AddLogEntry($"Hardlinked file: {sourcePath} -> {uniqueDest}");
                }
                else
                {
                    if (fileMover != null)
                    {
                        var ok = await fileMover.MoveFileAsync(sourcePath, uniqueDest);
                        if (!ok) throw new IOException("MoveFileAsync failed");
                    }
                    else
                    {
                        File.Move(sourcePath, uniqueDest, true);
                    }
                    job.AddLogEntry($"Moved file: {sourcePath} -> {uniqueDest}");
                }

                job.DestinationPath = uniqueDest;

                // Post-process: update download record and enqueue scan if needed
                await downloadService.ProcessCompletedDownloadAsync(job.DownloadId, job.DestinationPath);
                job.AddLogEntry($"Updated download record with final path: {job.DestinationPath}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                job.AddLogEntry($"File operation failed: {ex.Message}");
                job.ErrorMessage = ex.Message;
                _logger.LogError(ex, "File operation failed for job {JobId}", job.Id);
                throw;
            }
        }
    }
}

