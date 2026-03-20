using Listenarr.Api.Models;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services
{
    public class RenameService : IRenameService
    {
        private readonly IConfigurationService _configService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IFileMover _fileMover;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IHistoryRepository? _historyRepo;
        private readonly IDbContextFactory<ListenArrDbContext> _dbFactory;
        private readonly ILogger<RenameService> _logger;

        private const int MaxAudiobookIds = 500;

        public RenameService(
            IConfigurationService configService,
            IFileNamingService fileNamingService,
            IFileMover fileMover,
            IDbContextFactory<ListenArrDbContext> dbFactory,
            ILogger<RenameService> logger,
            IMoveQueueService? moveQueueService = null,
            IHistoryRepository? historyRepo = null)
        {
            _configService = configService;
            _fileNamingService = fileNamingService;
            _fileMover = fileMover;
            _dbFactory = dbFactory;
            _logger = logger;
            _moveQueueService = moveQueueService;
            _historyRepo = historyRepo;
        }

        public async Task<List<RenamePreview>> PreviewRenameAsync(int[] audiobookIds, CancellationToken ct = default)
        {
            if (audiobookIds == null || audiobookIds.Length == 0)
                return new List<RenamePreview>();

            if (audiobookIds.Length > MaxAudiobookIds)
                throw new ArgumentException($"Cannot preview more than {MaxAudiobookIds} audiobooks at once.");

            var settings = await _configService.GetApplicationSettingsAsync();
            var previews = new List<RenamePreview>();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var audiobooks = await db.Audiobooks
                .Include(a => a.Files)
                .Where(a => audiobookIds.Contains(a.Id))
                .ToListAsync(ct);

            foreach (var audiobook in audiobooks)
            {
                var preview = await BuildPreviewForAudiobook(audiobook, settings);
                previews.Add(preview);
            }

            return previews;
        }

        public async Task<List<RenameResult>> ExecuteRenameAsync(List<RenameOperation> operations, CancellationToken ct = default)
        {
            if (operations == null || operations.Count == 0)
                return new List<RenameResult>();

            var results = new List<RenameResult>();

            foreach (var op in operations)
            {
                var result = await ExecuteSingleRename(op, ct);
                results.Add(result);
            }

            return results;
        }

        private async Task<RenamePreview> BuildPreviewForAudiobook(Audiobook audiobook, ApplicationSettings settings)
        {
            var preview = new RenamePreview
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title,
                CurrentFolderPath = audiobook.BasePath,
            };

            // Build metadata for naming service
            var metadata = BuildMetadataFromAudiobook(audiobook);

            // Determine the effective output path (root folder)
            var outputPath = !string.IsNullOrWhiteSpace(audiobook.BasePath)
                ? GetRootFolderForBasePath(audiobook.BasePath, settings.OutputPath)
                : settings.OutputPath;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                preview.HasChanges = false;
                return preview;
            }

            // Compute expected folder path using the folder naming pattern
            var folderPattern = settings.FolderNamingPattern;
            if (!string.IsNullOrWhiteSpace(folderPattern))
            {
                var variables = BuildNamingVariables(audiobook, metadata);
                var expectedRelativeFolder = _fileNamingService.ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);

                // Normalize separators
                expectedRelativeFolder = expectedRelativeFolder
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);

                var expectedFolderPath = Path.Combine(outputPath, expectedRelativeFolder);
                expectedFolderPath = Path.GetFullPath(expectedFolderPath);

                preview.NewFolderPath = expectedFolderPath;

                // Compare normalized paths
                var currentNormalized = string.IsNullOrWhiteSpace(audiobook.BasePath)
                    ? string.Empty
                    : Path.GetFullPath(audiobook.BasePath);
                var newNormalized = expectedFolderPath;

                preview.FolderChanged = !string.Equals(currentNormalized, newNormalized, StringComparison.OrdinalIgnoreCase);
            }

            // Compute expected file names
            if (audiobook.Files != null && audiobook.Files.Count > 0)
            {
                bool isMultiFile = audiobook.Files.Count > 1;
                var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;
                if (string.IsNullOrWhiteSpace(filePattern))
                    filePattern = isMultiFile ? "{Title}-{DiskNumber:00}" : "{Title}";

                int diskNumber = 1;
                foreach (var file in audiobook.Files.OrderBy(f => f.Path))
                {
                    if (string.IsNullOrWhiteSpace(file.Path))
                        continue;

                    var currentFilename = Path.GetFileName(file.Path);
                    var extension = Path.GetExtension(file.Path);

                    var variables = BuildNamingVariables(audiobook, metadata);
                    if (isMultiFile)
                    {
                        variables["DiskNumber"] = diskNumber;
                    }

                    var expectedFilename = _fileNamingService.ApplyNamingPattern(filePattern, variables, treatAsFilename: true);
                    if (!expectedFilename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        expectedFilename += extension;

                    bool changed = !string.Equals(currentFilename, expectedFilename, StringComparison.OrdinalIgnoreCase);

                    // Determine expected full path (in the new folder if folder is changing, else current dir)
                    var targetFolder = preview.FolderChanged && !string.IsNullOrWhiteSpace(preview.NewFolderPath)
                        ? preview.NewFolderPath
                        : Path.GetDirectoryName(file.Path) ?? string.Empty;
                    var newFullPath = Path.Combine(targetFolder, expectedFilename);

                    // If the full path differs from current, mark as changed
                    bool fullPathChanged = !string.Equals(
                        Path.GetFullPath(file.Path),
                        Path.GetFullPath(newFullPath),
                        StringComparison.OrdinalIgnoreCase);

                    preview.FileRenames.Add(new FileRenamePreview
                    {
                        FileId = file.Id,
                        CurrentPath = file.Path,
                        NewPath = newFullPath,
                        CurrentFilename = currentFilename,
                        NewFilename = expectedFilename,
                        Changed = fullPathChanged,
                    });

                    diskNumber++;
                }
            }

            preview.HasChanges = preview.FolderChanged || preview.FileRenames.Any(f => f.Changed);
            return preview;
        }

        private async Task<RenameResult> ExecuteSingleRename(RenameOperation op, CancellationToken ct)
        {
            var result = new RenameResult { AudiobookId = op.AudiobookId };

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var audiobook = await db.Audiobooks
                    .Include(a => a.Files)
                    .FirstOrDefaultAsync(a => a.Id == op.AudiobookId, ct);

                if (audiobook == null)
                {
                    result.Success = false;
                    result.Error = "Audiobook not found.";
                    return result;
                }

                // Handle folder move via MoveQueueService (background, robust)
                bool folderMoved = false;
                bool basePathUpdatedInPlace = false;
                if (!string.IsNullOrWhiteSpace(op.NewFolderPath) &&
                    !string.Equals(audiobook.BasePath, op.NewFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Validate: new folder path must not contain path traversal
                    var normalizedNew = Path.GetFullPath(op.NewFolderPath);
                    if (normalizedNew.Contains("..", StringComparison.Ordinal))
                    {
                        result.Success = false;
                        result.Error = "Invalid destination path.";
                        return result;
                    }

                    // If the target directory already exists and has files, the files are already
                    // in the right place — just update the BasePath and file paths in the database.
                    if (Directory.Exists(normalizedNew) && Directory.EnumerateFiles(normalizedNew, "*", SearchOption.AllDirectories).Any())
                    {
                        var oldBase = audiobook.BasePath ?? string.Empty;
                        audiobook.BasePath = normalizedNew;

                        // Update AudiobookFile.Path records to reflect the new base path
                        if (audiobook.Files != null)
                        {
                            foreach (var file in audiobook.Files)
                            {
                                if (!string.IsNullOrWhiteSpace(file.Path) &&
                                    file.Path.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase))
                                {
                                    file.Path = normalizedNew + file.Path.Substring(oldBase.Length);
                                }
                            }
                        }

                        await db.SaveChangesAsync(ct);
                        basePathUpdatedInPlace = true;
                        _logger.LogInformation("Target folder already exists with files for audiobook {Id}; updated BasePath and file paths to {Path}",
                            audiobook.Id, normalizedNew);
                    }
                    else if (_moveQueueService != null)
                    {
                        await _moveQueueService.EnqueueMoveAsync(audiobook.Id, normalizedNew, audiobook.BasePath);
                        folderMoved = true;
                        _logger.LogInformation("Enqueued folder move for audiobook {Id}: {Old} → {New}",
                            audiobook.Id, audiobook.BasePath, normalizedNew);
                    }
                    else
                    {
                        _logger.LogWarning("MoveQueueService not available, skipping folder move for audiobook {Id}", audiobook.Id);
                    }
                }

                // Handle individual file renames (synchronous, within same directory)
                // Run file renames when: no background move was enqueued, OR the base path
                // was updated in place (files are at target but may need renaming).
                if ((!folderMoved || basePathUpdatedInPlace) && op.FileRenames != null && op.FileRenames.Count > 0)
                {
                    foreach (var fileOp in op.FileRenames)
                    {
                        var fileResult = new FileRenameResultItem
                        {
                            FileId = fileOp.FileId,
                            PreviousPath = fileOp.CurrentPath,
                            NewPath = fileOp.NewPath,
                        };

                        try
                        {
                            // Validate source exists
                            if (!File.Exists(fileOp.CurrentPath))
                            {
                                fileResult.Success = false;
                                fileResult.Error = "Source file not found.";
                                result.RenamedFiles.Add(fileResult);
                                continue;
                            }

                            // Validate target doesn't collide with a different file
                            if (File.Exists(fileOp.NewPath) &&
                                !string.Equals(Path.GetFullPath(fileOp.CurrentPath), Path.GetFullPath(fileOp.NewPath), StringComparison.OrdinalIgnoreCase))
                            {
                                fileResult.Success = false;
                                fileResult.Error = "Target file already exists.";
                                result.RenamedFiles.Add(fileResult);
                                continue;
                            }

                            // Ensure target directory exists
                            var targetDir = Path.GetDirectoryName(fileOp.NewPath);
                            if (!string.IsNullOrWhiteSpace(targetDir))
                                Directory.CreateDirectory(targetDir);

                            var moved = await _fileMover.MoveFileAsync(fileOp.CurrentPath, fileOp.NewPath);
                            if (moved)
                            {
                                // Update DB record
                                var dbFile = audiobook.Files?.FirstOrDefault(f => f.Id == fileOp.FileId);
                                if (dbFile != null)
                                {
                                    dbFile.Path = fileOp.NewPath;
                                }

                                fileResult.Success = true;
                                _logger.LogInformation("Renamed file {FileId}: {Old} → {New}",
                                    fileOp.FileId, fileOp.CurrentPath, fileOp.NewPath);
                            }
                            else
                            {
                                fileResult.Success = false;
                                fileResult.Error = "File move operation failed.";
                            }
                        }
                        catch (Exception ex)
                        {
                            fileResult.Success = false;
                            fileResult.Error = ex.Message;
                            _logger.LogError(ex, "Failed to rename file {FileId}", fileOp.FileId);
                        }

                        result.RenamedFiles.Add(fileResult);
                    }

                    await db.SaveChangesAsync(ct);
                }

                result.Success = folderMoved || basePathUpdatedInPlace || result.RenamedFiles.All(f => f.Success);

                // Create a single history entry summarizing the organize operation
                if (result.Success)
                {
                    try
                    {
                        if (_historyRepo != null)
                        {
                            var parts = new List<string>();
                            if (folderMoved || basePathUpdatedInPlace) parts.Add("folder organized");
                            var renamedCount = result.RenamedFiles.Count(f => f.Success);
                            if (renamedCount > 0) parts.Add($"{renamedCount} file(s) renamed");
                            var summary = parts.Count > 0 ? string.Join(", ", parts) : "files organized";

                            await _historyRepo.AddAsync(new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title,
                                EventType = "Organized",
                                Message = summary,
                                Source = "Organize",
                                Timestamp = DateTime.UtcNow,
                            });
                        }
                    }
                    catch (Exception histEx)
                    {
                        _logger.LogWarning(histEx, "Failed to create history entry for organize on audiobook {Id}", audiobook.Id);
                    }
                }

                if (folderMoved)
                {
                    // When folder is moving, file renames will happen as part of the move.
                    // Update BasePath optimistically; MoveBackgroundService will finalize.
                    _logger.LogInformation("Audiobook {Id} folder move queued — file renames deferred to move service.", op.AudiobookId);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                _logger.LogError(ex, "Failed to execute rename for audiobook {Id}", op.AudiobookId);
            }

            return result;
        }

        /// <summary>
        /// Given an audiobook BasePath and the global OutputPath, determine which root folder
        /// the audiobook belongs to. If BasePath starts with OutputPath, use OutputPath as root.
        /// Otherwise return OutputPath (the naming pattern will expand from there).
        /// </summary>
        private static string GetRootFolderForBasePath(string basePath, string? outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                return string.Empty;

            var normalizedBase = Path.GetFullPath(basePath);
            var normalizedOutput = Path.GetFullPath(outputPath);

            // If the audiobook lives under the configured output path, use output path as root
            if (normalizedBase.StartsWith(normalizedOutput, StringComparison.OrdinalIgnoreCase))
                return normalizedOutput;

            // Otherwise use the output path anyway — renames will organize into the correct structure
            return normalizedOutput;
        }

        private static AudioMetadata BuildMetadataFromAudiobook(Audiobook audiobook)
        {
            var author = (audiobook.Authors != null && audiobook.Authors.Any())
                ? string.Join(", ", audiobook.Authors)
                : "Unknown Author";

            return new AudioMetadata
            {
                Title = audiobook.Title ?? "Unknown Title",
                Artist = author,
                AlbumArtist = author,
                Album = audiobook.Title ?? "Unknown Title",
                Series = audiobook.Series,
                SeriesPosition = !string.IsNullOrWhiteSpace(audiobook.SeriesNumber) && decimal.TryParse(audiobook.SeriesNumber, out var sp) ? sp : null,
                Year = !string.IsNullOrWhiteSpace(audiobook.PublishYear) && int.TryParse(audiobook.PublishYear, out var year) ? year : null,
            };
        }

        private static Dictionary<string, object> BuildNamingVariables(Audiobook audiobook, AudioMetadata metadata)
        {
            return new Dictionary<string, object>
            {
                { "Author", metadata.Artist ?? "Unknown Author" },
                { "Series", string.IsNullOrWhiteSpace(metadata.Series) ? string.Empty : metadata.Series },
                { "Title", metadata.Title ?? "Unknown Title" },
                { "SeriesNumber", metadata.SeriesPosition?.ToString() ?? string.Empty },
                { "Year", metadata.Year?.ToString() ?? string.Empty },
                { "Quality", audiobook.Quality ?? string.Empty },
                { "DiskNumber", string.Empty },
                { "ChapterNumber", string.Empty },
            };
        }
    }
}
