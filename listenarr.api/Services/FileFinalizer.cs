using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Api.Repositories;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    public class FileFinalizer : IFileFinalizer
    {
        private readonly IImportService _importService;
        private readonly IDownloadRepository _downloadRepository;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<FileFinalizer> _logger;

        public FileFinalizer(IImportService importService, IDownloadRepository downloadRepository, IServiceScopeFactory scopeFactory, ILogger<FileFinalizer> logger)
        {
            _importService = importService ?? throw new ArgumentNullException(nameof(importService));
            _downloadRepository = downloadRepository ?? throw new ArgumentNullException(nameof(downloadRepository));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<ImportResult>> ImportFilesFromDirectoryAsync(string downloadId, int? audiobookId, IEnumerable<string> files, ApplicationSettings settings)
        {
            var results = await _importService.ImportFilesFromDirectoryAsync(downloadId, audiobookId, files, settings);

            foreach (var r in results.Where(x => x != null && x.Success && !string.IsNullOrWhiteSpace(x.FinalPath)).Select(x => x!))
            {
                var finalPath = r.FinalPath!;
                try
                {
                    var tracked = await _downloadRepository.FindAsync(downloadId);
                    if (tracked != null)
                    {
                        tracked.FinalPath = finalPath;
                        await _downloadRepository.UpdateAsync(tracked);
                        _logger.LogInformation("FileFinalizer: updated FinalPath for download {DownloadId} to {FinalPath}", downloadId, finalPath);
                    }

                    // Sync into any scoped ListenArrDbContext so in-memory tracked entities match persisted value.
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var scopedDb = scope.ServiceProvider.GetService(typeof(ListenArrDbContext)) as ListenArrDbContext;
                        if (scopedDb != null)
                        {
                            var td = await scopedDb.Downloads.FindAsync(downloadId);
                            if (td != null)
                            {
                                td.FinalPath = finalPath;
                                scopedDb.Downloads.Update(td);
                                await scopedDb.SaveChangesAsync();
                            }
                        }
                    }
                    catch (Exception exSync) when (exSync is not OperationCanceledException && exSync is not OutOfMemoryException && exSync is not StackOverflowException) {
                        _logger.LogDebug(exSync, "FileFinalizer: failed to sync FinalPath into scoped ListenArrDbContext (non-fatal)");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "FileFinalizer: failed processing import result for download {DownloadId}", downloadId);
                }
            }

            return results;
        }

        public async Task<ImportResult> ImportSingleFileAsync(string downloadId, int? audiobookId, string sourcePath, ApplicationSettings settings)
        {
            var result = await _importService.ImportSingleFileAsync(downloadId, audiobookId, sourcePath, settings);

            if (result != null && result.Success && !string.IsNullOrWhiteSpace(result.FinalPath))
            {
                string? finalPath = null;
                try
                {
                    var tracked = await _downloadRepository.FindAsync(downloadId);
                    if (tracked != null)
                    {
                        finalPath = result.FinalPath!;
                        tracked.FinalPath = finalPath;
                        await _downloadRepository.UpdateAsync(tracked);
                        _logger.LogInformation("FileFinalizer: updated FinalPath for download {DownloadId} to {FinalPath}", downloadId, finalPath);
                    }

                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var scopedDb = scope.ServiceProvider.GetService(typeof(ListenArrDbContext)) as ListenArrDbContext;
                        if (scopedDb != null)
                        {
                            var td = await scopedDb.Downloads.FindAsync(downloadId);
                            if (td != null && finalPath != null)
                            {
                                td.FinalPath = finalPath;
                                scopedDb.Downloads.Update(td);
                                await scopedDb.SaveChangesAsync();
                            }
                        }
                    }
                    catch (Exception exSync) when (exSync is not OperationCanceledException && exSync is not OutOfMemoryException && exSync is not StackOverflowException) {
                        _logger.LogDebug(exSync, "FileFinalizer: failed to sync FinalPath into scoped ListenArrDbContext (non-fatal)");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "FileFinalizer: failed updating FinalPath for download {DownloadId}", downloadId);
                }
            }

            return result ?? new ImportResult { Success = false, Message = "Null result from ImportService" };
        }
    }
}

