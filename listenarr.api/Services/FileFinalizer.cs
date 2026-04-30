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

            foreach (var finalPath in results.Where(x => x != null && x.Success && !string.IsNullOrWhiteSpace(x.FinalPath)).Select(x => x!.FinalPath!))
            {
                try
                {
                    var tracked = await _downloadRepository.FindAsync(downloadId);
                    if (tracked != null)
                    {
                        tracked.FinalPath = finalPath;
                        await _downloadRepository.UpdateAsync(tracked);
                        _logger.LogInformation("FileFinalizer: updated FinalPath for download {DownloadId} to {FinalPath}", downloadId, finalPath);
                    }

                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
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

                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "FileFinalizer: failed updating FinalPath for download {DownloadId}", downloadId);
                }
            }

            return result ?? new ImportResult { Success = false, Message = "Null result from ImportService" };
        }
    }
}

