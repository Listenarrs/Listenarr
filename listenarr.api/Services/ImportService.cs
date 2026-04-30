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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Domain.Utils;
using static Listenarr.Api.Services.FileMover;
using Listenarr.Api.Services.Metadata;

namespace Listenarr.Api.Services
{
    public class ImportService : IImportService
    {
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IServiceScopeFactory _scopeFactory;
        public IServiceScopeFactory ScopeFactory => _scopeFactory;
        private readonly IFileNamingService _fileNamingService;
        private readonly IMetadataService? _metadataService;
        private readonly IFileMover _fileMover;
        private readonly ILogger<ImportService> _logger;

        public ImportService(
            IAudiobookRepository audiobookRepository,
            IServiceScopeFactory scopeFactory,
            IFileNamingService fileNamingService,
            IMetadataService? metadataService,
            IFileMover fileMover,
            ILogger<ImportService>? logger = null)
        {
            _audiobookRepository = audiobookRepository;
            _scopeFactory = scopeFactory;
            _fileNamingService = fileNamingService;
            _metadataService = metadataService;
            _fileMover = fileMover;
            _logger = logger ?? NullLogger<ImportService>.Instance;
        }

        // Compatibility overload: older tests and callers used to pass ILogger as the fifth parameter.
        public ImportService(
            IAudiobookRepository audiobookRepository,
            IServiceScopeFactory scopeFactory,
            IFileNamingService fileNamingService,
            IMetadataService? metadataService,
            ILogger<ImportService>? logger = null)
            : this(audiobookRepository, scopeFactory, fileNamingService, metadataService, new NullFileMover(), logger)
        {
        }

        public async Task<ImportResult> ImportSingleFileAsync(string downloadId, int? audiobookId, string sourcePath, ApplicationSettings settings, CancellationToken ct = default)
        {
            var result = new ImportResult { SourcePath = sourcePath };

            try
            {
                if (!FileUtils.IsAudioFile(sourcePath))
                {
                    result.Success = false;
                    result.SkippedReason = "source file is not a supported audio file";
                    result.Message = result.SkippedReason;
                    _logger.LogWarning("ImportSingleFile: refusing non-audio file {File}", sourcePath);
                    return result;
                }

                // Build initial metadata context
                var metadata = new AudioMetadata
                {
                    Title = Path.GetFileNameWithoutExtension(sourcePath) ?? "Unknown Title"
                };

                AudioMetadata? extractedMetadata = null;
                if (_metadataService != null && File.Exists(sourcePath))
                {
                    try
                    {
                        extractedMetadata = await _metadataService.ExtractFileMetadataAsync(sourcePath);
                        if (extractedMetadata != null)
                        {
                            metadata.Title = FirstNonEmpty(metadata.Title, extractedMetadata.Title);
                            metadata.Artist = FirstNonEmpty(metadata.Artist, extractedMetadata.Artist, extractedMetadata.AlbumArtist);
                            metadata.Album = FirstNonEmpty(metadata.Album, extractedMetadata.Album);
                            metadata.SeriesPosition ??= extractedMetadata.SeriesPosition;
                            metadata.TrackNumber ??= extractedMetadata.TrackNumber;
                            metadata.DiscNumber ??= extractedMetadata.DiscNumber;
                            metadata.Year ??= extractedMetadata.Year;
                            metadata.BitRate ??= extractedMetadata.BitRate;
                            metadata.Format ??= extractedMetadata.Format;
                            _logger.LogDebug("ImportSingleFile: merged extracted metadata for {File}", sourcePath);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "ImportSingleFile: failed to extract metadata from {File}, using defaults", sourcePath);
                    }
                }

                // If download references an audiobook, prefer DB metadata for the shared book fields,
                // but keep file-specific numbering/quality data from the extracted source file.
                AudioMetadata? namingMetadata = null;
                if (audiobookId != null)
                {
                    try
                    {
                        var audiobook = await _audiobookRepository.GetByIdAsync(audiobookId.Value);
                        if (audiobook != null)
                        {
                            namingMetadata = BuildNamingMetadata(audiobook, extractedMetadata, metadata.Title);
                            _logger.LogDebug("ImportSingleFile: Using audiobook metadata for naming (Download {DownloadId}): {Title} by {Artist}", LogRedaction.SanitizeText(downloadId), LogRedaction.SanitizeText(namingMetadata.Title), LogRedaction.SanitizeText(namingMetadata.Artist));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "ImportSingleFile: failed to load audiobook metadata for naming (Download {DownloadId})", downloadId);
                    }
                }

                var metadataForNaming = namingMetadata ?? BuildNamingMetadata(null, extractedMetadata, metadata.Title);

                // If linked to an audiobook, prevent importing worse quality than existing files
                if (audiobookId != null)
                {
                    try
                    {
                        var ab = await _audiobookRepository.GetByIdAsync(audiobookId.Value);

                        if (ab != null && ab.Files != null && ab.Files.Any())
                        {
                            var abProfile = ab.QualityProfile;
                            string? bestExisting = null;

                            foreach (var f in ab.Files)
                            {
                                try
                                {
                                    string q = string.Empty;
                                    if (!string.IsNullOrEmpty(f.Format)) q = f.Format;
                                    if (f.Bitrate.HasValue)
                                    {
                                        var kb = f.Bitrate.Value / 1000;
                                        if (kb >= 320) q = "MP3 320kbps";
                                        else if (kb >= 256) q = "MP3 256kbps";
                                        else if (kb >= 192) q = "MP3 192kbps";
                                        else if (kb >= 128) q = "MP3 128kbps";
                                    }
                                    if (string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(f.Path)) q = DetermineQualityFromMetadata(null, f.Path);

                                    if (string.IsNullOrEmpty(bestExisting)) bestExisting = q;
                                    else if (!string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(bestExisting) && abProfile != null && IsQualityBetter(q, bestExisting, abProfile))
                                    {
                                        bestExisting = q;
                                    }
                                }
                                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                            }

                            var candidateQuality = DetermineQualityFromMetadata(metadata, sourcePath);
                            if (!IsQualityBetter(candidateQuality, bestExisting, abProfile))
                            {
                                result.Success = false;
                                result.SkippedReason = $"candidate quality '{candidateQuality}' is not better than existing '{bestExisting}'";
                                result.Message = result.SkippedReason;
                                _logger.LogInformation("ImportSingleFile: Skipping import of file {File} for audiobook {AudiobookId} because candidate quality '{Candidate}' is not better than existing '{Existing}'", LogRedaction.SanitizeText(sourcePath), ab.Id, candidateQuality, bestExisting);
                                return result;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "ImportSingleFile: Failed to evaluate quality for {File}", sourcePath);
                    }
                }

                // Folder/file naming patterns
                var folderPattern = settings.FolderNamingPattern;
                var isMultiFile = metadataForNaming.DiscNumber.HasValue || metadataForNaming.TrackNumber.HasValue;
                var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

                // build variables
                var variables = new Dictionary<string, object>
                {
                    { "Author", metadataForNaming.Artist ?? "Unknown Author" },
                    { "Series", string.IsNullOrWhiteSpace(metadataForNaming.Series) ? string.Empty : metadataForNaming.Series },
                    { "Title", metadataForNaming.Title ?? "Unknown Title" },
                    { "Subtitle", string.IsNullOrWhiteSpace(metadataForNaming.Subtitle) ? string.Empty : metadataForNaming.Subtitle },
                    { "Edition", string.IsNullOrWhiteSpace(metadataForNaming.Edition) ? string.Empty : metadataForNaming.Edition },
                    { "Narrator", string.IsNullOrWhiteSpace(metadataForNaming.Narrator) ? string.Empty : metadataForNaming.Narrator },
                    { "Publisher", string.IsNullOrWhiteSpace(metadataForNaming.Publisher) ? string.Empty : metadataForNaming.Publisher },
                    { "Language", string.IsNullOrWhiteSpace(metadataForNaming.Language) ? string.Empty : metadataForNaming.Language },
                    { "Asin", string.IsNullOrWhiteSpace(metadataForNaming.Asin) ? string.Empty : metadataForNaming.Asin },
                    { "SeriesNumber", metadataForNaming.SeriesPosition?.ToString() ?? metadataForNaming.TrackNumber?.ToString() ?? string.Empty },
                    { "Year", metadataForNaming.Year?.ToString() ?? string.Empty },
                    { "Quality", (metadataForNaming.BitRate.HasValue ? $"{metadataForNaming.BitRate}kbps" : null) ?? metadataForNaming.Format ?? string.Empty },
                    { "DiskNumber", metadataForNaming.DiscNumber?.ToString() ?? string.Empty },
                    { "ChapterNumber", metadataForNaming.TrackNumber?.ToString() ?? string.Empty }
                };

                string basePathForFile = settings.OutputPath; // default
                string filenamePattern = filePattern;
                var usingAudiobookBasePath = false;

                if (audiobookId != null && namingMetadata != null)
                {
                    try
                    {
                        var ab = await _audiobookRepository.GetByIdAsync(audiobookId.Value);
                        if (ab != null && !string.IsNullOrWhiteSpace(ab.BasePath))
                        {
                            basePathForFile = FileUtils.NormalizeStoredPath(ab.BasePath); // custom/base path
                            usingAudiobookBasePath = true;
                            _logger.LogDebug("ImportSingleFile: using audiobook base path for download {DownloadId}: {BasePath}", downloadId, basePathForFile);
                            // For audiobook base path, default to filename-only unless the user explicitly configures a file pattern
                            filenamePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
                        }
                        else if (!string.IsNullOrWhiteSpace(folderPattern))
                        {
                            var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);
                            if (!string.IsNullOrWhiteSpace(folderRelative))
                            {
                                basePathForFile = CombineWithOptionalBase(basePathForFile, folderRelative);
                            }
                        }
                    }
                    catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                    { /* ignore */
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(folderPattern))
                {
                    var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);
                    if (!string.IsNullOrWhiteSpace(folderRelative))
                    {
                        basePathForFile = CombineWithOptionalBase(basePathForFile, folderRelative);
                    }
                }

                if (string.IsNullOrWhiteSpace(basePathForFile)) basePathForFile = "./completed";

                if (string.IsNullOrWhiteSpace(folderPattern) && string.IsNullOrWhiteSpace(filenamePattern))
                {
                    // Legacy fallback
                    filenamePattern = "{Author}/{Series}/{Title}";
                }
                else if (string.IsNullOrWhiteSpace(filenamePattern))
                {
                    filenamePattern = "{Title}";
                }

                var patternAllowsSubfolders = filenamePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                    || filenamePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                    || filenamePattern.IndexOf('/') >= 0
                    || filenamePattern.IndexOf('\\') >= 0;

                // When importing into an explicit audiobook base path, always treat the naming output as filename-only.
                // This prevents re-appending author/title folder segments when base path already contains them.
                var treatAsFilename = usingAudiobookBasePath || filenamePattern == "{Title}" || !patternAllowsSubfolders;

                var filename = _fileNamingService.ApplyNamingPattern(filenamePattern, variables, treatAsFilename);
                var ext = Path.GetExtension(sourcePath);
                if (!filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) filename += ext;

                if (!patternAllowsSubfolders)
                {
                    try { filename = Path.GetFileName(filename); }
                    catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { filename = Path.GetFileName(sourcePath); }
                }

                var destinationPath = CombineWithOptionalBase(basePathForFile, filename);

                // Ensure destination directory exists
                var destDir = Path.GetDirectoryName(destinationPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                // Perform file operation
                try
                {
                    var initialDest = Path.Join(destDir, Path.GetFileName(sourcePath));

                    // If the source file is already at the destination (e.g., the background
                    // processing job already moved/hardlinked it), skip the file operation
                    // entirely to avoid creating a duplicate.
                    var sourceAlreadyAtDest = string.Equals(
                        Path.GetFullPath(sourcePath),
                        Path.GetFullPath(initialDest),
                        StringComparison.OrdinalIgnoreCase);

                    string uniqueInitial;
                    if (sourceAlreadyAtDest)
                    {
                        _logger.LogInformation("ImportSingleFile: source is already at destination {Dest}, skipping file operation", initialDest);
                        uniqueInitial = sourcePath;
                    }
                    else
                    {
                        uniqueInitial = FileUtils.GetUniqueDestinationPath(initialDest);

                        var action = settings.CompletedFileAction ?? "Move";
                        if (string.Equals(action, "Copy", StringComparison.OrdinalIgnoreCase))
                        {
                            var ok = await _fileMover.CopyFileAsync(sourcePath, uniqueInitial);
                            if (ok) result.WasCopied = true;
                        }
                        else if (string.Equals(action, "Hardlink/Copy", StringComparison.OrdinalIgnoreCase))
                        {
                            var ok = await _fileMover.HardlinkFileAsync(sourcePath, uniqueInitial);
                            if (!ok)
                            {
                                _logger.LogWarning("ImportSingleFile: Hardlink failed for {Source}, attempting copy fallback", sourcePath);
                                ok = await _fileMover.CopyFileAsync(sourcePath, uniqueInitial);
                            }

                            if (ok)
                            {
                                result.WasCopied = true;
                            }
                            else
                            {
                                throw new IOException("Hardlink/Copy failed");
                            }
                        }
                        else
                        {
                            var ok = await _fileMover.MoveFileAsync(sourcePath, uniqueInitial);
                            if (ok) result.WasMoved = true;
                        }
                    }

                    // Now apply filename pattern
                    var uniqueFinal = destinationPath;
                    if (!string.Equals(Path.GetFullPath(uniqueInitial), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                    {
                        uniqueFinal = FileUtils.GetUniqueDestinationPath(destinationPath);
                        try
                        {
                            var ok = await _fileMover.MoveFileAsync(uniqueInitial, uniqueFinal);
                            if (!ok)
                            {
                                _logger.LogWarning("ImportSingleFile: failed to rename {Source} -> {Dest}", uniqueInitial, uniqueFinal);
                                uniqueFinal = uniqueInitial;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "ImportSingleFile: failed to rename {Source} -> {Dest}", uniqueInitial, uniqueFinal);
                            uniqueFinal = uniqueInitial; // fallback
                        }
                    }

                    result.FinalPath = uniqueFinal;
                    result.Success = true;

                    if (audiobookId.HasValue)
                    {
                        await EnsureAudiobookBasePathAsync(
                            audiobookId.Value,
                            Path.GetDirectoryName(uniqueFinal) ?? basePathForFile,
                            ct);
                    }

                    // Note: single-file imports do not register the audiobook file immediately here.
                    // Registration and any quality gating is handled by the caller (DownloadService)
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                    _logger.LogWarning(ex, "ImportSingleFile: failed file operation for {File}", sourcePath);
                }

            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                result.Success = false;
                result.Message = ex.Message;
                _logger.LogWarning(ex, "ImportSingleFile: unexpected failure for {File}", sourcePath);
            }

            return result;

            static string FirstNonEmpty(params string?[] candidates)
            {
                foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
                    return c!;
                return string.Empty;
            }
        }

        public async Task<List<ImportResult>> ImportFilesFromDirectoryAsync(string downloadId, int? audiobookId, IEnumerable<string> files, ApplicationSettings settings, CancellationToken ct = default)
        {
            var results = new List<ImportResult>();
            var folderPattern = settings.FolderNamingPattern;
            var sourceFiles = files
                .Where(file => !FileUtils.IsBlacklistedFile(file, settings.ImportBlacklistExtensions))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var plannedAudioFiles = MultiFileImportPlanner.BuildPlans(
                sourceFiles
                    .Where(FileUtils.IsAudioFile)
                    .Select(f => (f, (string?)null)));
            var planByPath = plannedAudioFiles.ToDictionary(p => p.FullPath, StringComparer.OrdinalIgnoreCase);
            var diskNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plannedAudioFiles, p => p.DiskNumberHint);
            var chapterNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plannedAudioFiles, p => p.ChapterNumberHint);
            var orderedFiles = plannedAudioFiles.Select(p => p.FullPath)
                .Concat(sourceFiles.Where(f => !planByPath.ContainsKey(f)))
                .ToList();
            var isMultiFileBatch = plannedAudioFiles.Count > 1;
            var sourceRootPath = FileUtils.GetCommonDirectory(sourceFiles);
            var completedFileAction = settings.CompletedFileAction ?? "Move";

            try
            {
                // Precompute audiobook and best existing quality to avoid import-order races
                Audiobook? batchAudiobook = null;
                string? bestExisting = null;
                QualityProfile? abProfile = null;
                string? batchDestinationRoot = null;

                if (audiobookId != null)
                {
                    try
                    {
                        batchAudiobook = await _audiobookRepository.GetByIdAsync(audiobookId.Value);

                        abProfile = batchAudiobook?.QualityProfile;
                        batchDestinationRoot = string.IsNullOrWhiteSpace(batchAudiobook?.BasePath) ? null : batchAudiobook!.BasePath;

                        if (batchAudiobook != null && batchAudiobook.Files != null && batchAudiobook.Files.Any())
                        {
                            foreach (var f in batchAudiobook.Files)
                            {
                                try
                                {
                                    string q = string.Empty;
                                    if (!string.IsNullOrEmpty(f.Format)) q = f.Format;
                                    if (f.Bitrate.HasValue)
                                    {
                                        var kb = f.Bitrate.Value / 1000;
                                        if (kb >= 320) q = "MP3 320kbps";
                                        else if (kb >= 256) q = "MP3 256kbps";
                                        else if (kb >= 192) q = "MP3 192kbps";
                                        else if (kb >= 128) q = "MP3 128kbps";
                                    }
                                    if (string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(f.Path)) q = DetermineQualityFromMetadata(null, f.Path);

                                    if (string.IsNullOrEmpty(bestExisting)) bestExisting = q;
                                    else if (!string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(bestExisting) && abProfile != null && IsQualityBetter(q, bestExisting, abProfile))
                                    {
                                        bestExisting = q;
                                    }
                                }
                                catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "ImportFilesFromDirectory: Failed to load audiobook for batch quality evaluation (DownloadId: {DownloadId})", downloadId);
                    }
                }

                foreach (var file in orderedFiles)
                {
                    var res = new ImportResult { SourcePath = file };

                    if (!FileUtils.IsAudioFile(file))
                    {
                        var hasSuccessfulAudioImport = results.Any(r =>
                            r.Success
                            && !string.IsNullOrWhiteSpace(r.FinalPath)
                            && !string.IsNullOrWhiteSpace(r.SourcePath)
                            && FileUtils.IsAudioFile(r.SourcePath!));

                        if (!hasSuccessfulAudioImport || string.IsNullOrWhiteSpace(batchDestinationRoot))
                        {
                            res.Success = false;
                            res.SkippedReason = "no successful audio import in batch";
                            results.Add(res);
                            _logger.LogDebug("ImportFilesFromDirectory: Skipping companion file {File} because no successful audio import was recorded for the batch", file);
                            continue;
                        }

                        try
                        {
                            var relativePath = !string.IsNullOrWhiteSpace(sourceRootPath)
                                ? Path.GetRelativePath(sourceRootPath, file)
                                : Path.GetFileName(file);
                            if (relativePath.StartsWith("..", StringComparison.Ordinal))
                            {
                                relativePath = Path.GetFileName(file);
                            }

                            var finalCompanionPath = CombineWithOptionalBase(batchDestinationRoot, relativePath);
                            var companionDir = Path.GetDirectoryName(finalCompanionPath);
                            if (!string.IsNullOrWhiteSpace(companionDir))
                            {
                                Directory.CreateDirectory(companionDir);
                            }

                            finalCompanionPath = FileUtils.GetUniqueDestinationPath(finalCompanionPath);

                            if (string.Equals(completedFileAction, "Copy", StringComparison.OrdinalIgnoreCase))
                            {
                                var ok = await _fileMover.CopyFileAsync(file, finalCompanionPath);
                                if (!ok)
                                {
                                    throw new IOException("Copy failed");
                                }

                                res.WasCopied = true;
                            }
                            else if (string.Equals(completedFileAction, "Hardlink/Copy", StringComparison.OrdinalIgnoreCase))
                            {
                                var ok = await _fileMover.HardlinkFileAsync(file, finalCompanionPath);
                                if (!ok)
                                {
                                    _logger.LogWarning("ImportFilesFromDirectory: Hardlink failed for companion file {Source}, attempting copy fallback", file);
                                    ok = await _fileMover.CopyFileAsync(file, finalCompanionPath);
                                }

                                if (!ok)
                                {
                                    throw new IOException("Hardlink/Copy failed");
                                }

                                res.WasCopied = true;
                            }
                            else
                            {
                                var ok = await _fileMover.MoveFileAsync(file, finalCompanionPath);
                                if (!ok)
                                {
                                    throw new IOException("Move failed");
                                }

                                res.WasMoved = true;
                            }

                            res.FinalPath = finalCompanionPath;
                            res.Success = true;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            res.Success = false;
                            res.Message = ex.Message;
                            _logger.LogWarning(ex, "ImportFilesFromDirectory: Failed companion-file import for {File}", file);
                        }

                        results.Add(res);
                        continue;
                    }

                    try
                    {
                        planByPath.TryGetValue(file, out var plan);
                        diskNumbersForNaming.TryGetValue(file, out var namingDiskNumber);
                        chapterNumbersForNaming.TryGetValue(file, out var namingChapterNumber);
                        var candidateMetadata = (AudioMetadata?)null;
                        if (_metadataService != null)
                        {
                            try { candidateMetadata = await _metadataService.ExtractFileMetadataAsync(file); } catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException) { candidateMetadata = null; }
                        }

                        var candidateQuality = DetermineQualityFromMetadata(candidateMetadata, file);

                        // If linked to audiobook, decide whether to import based on quality profile
                        if (audiobookId != null && batchAudiobook != null)
                        {
                            try
                            {
                                if (batchAudiobook.Files != null && batchAudiobook.Files.Any() && !IsQualityBetter(candidateQuality, bestExisting, abProfile))
                                {
                                    res.Success = false;
                                    res.SkippedReason = $"candidate quality '{candidateQuality}' is not better than existing '{bestExisting}'";
                                    results.Add(res);
                                    _logger.LogInformation("ImportFilesFromDirectory: Skipping import of file {File} for audiobook {AudiobookId} because candidate quality '{Candidate}' is not better than existing '{Existing}'", file, batchAudiobook.Id, candidateQuality, bestExisting);
                                    continue; // skip importing this file
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogDebug(ex, "ImportFilesFromDirectory: Failed to evaluate quality for multi-file import {File}", file);
                            }
                        }

                        // Determine destination directory (prefer audiobook basepath)
                        string destDirForFile = string.Empty;
                        Audiobook? abForNaming = null;
                        if (audiobookId != null)
                        {
                            try
                            {
                                abForNaming = await _audiobookRepository.GetByIdAsync(audiobookId.Value);
                                if (abForNaming != null && !string.IsNullOrWhiteSpace(abForNaming.BasePath)) destDirForFile = FileUtils.NormalizeStoredPath(abForNaming.BasePath);
                            }
                            catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException) { destDirForFile = string.Empty; }
                        }
                        if (string.IsNullOrWhiteSpace(destDirForFile)) destDirForFile = settings.OutputPath ?? "./completed";

                        // Build naming metadata: prefer audiobook metadata when available, otherwise use extracted candidate metadata
                        var namingMetadata = BuildNamingMetadata(abForNaming, candidateMetadata, Path.GetFileNameWithoutExtension(file));
                        var effectiveDiskNumber = namingDiskNumber > 0 ? namingDiskNumber : (namingMetadata.DiscNumber ?? plan?.DiskNumberHint);
                        var effectiveChapterNumber = namingChapterNumber > 0 ? namingChapterNumber : (namingMetadata.TrackNumber ?? plan?.ChapterNumberHint);
                        if (isMultiFileBatch)
                        {
                            effectiveDiskNumber ??= effectiveChapterNumber;
                            effectiveChapterNumber ??= effectiveDiskNumber;
                        }
                        var stableSuffixNumber = effectiveChapterNumber ?? effectiveDiskNumber ?? plan?.SequenceNumber;

                        // Build variables for naming patterns (used for both folder and file patterns)
                        var variablesForFile = new Dictionary<string, object>
                        {
                            { "Author", namingMetadata.Artist ?? "Unknown Author" },
                            { "Series", string.IsNullOrWhiteSpace(namingMetadata.Series) ? string.Empty : namingMetadata.Series },
                            { "Title", namingMetadata.Title ?? Path.GetFileNameWithoutExtension(file) },
                            { "Subtitle", string.IsNullOrWhiteSpace(namingMetadata.Subtitle) ? string.Empty : namingMetadata.Subtitle },
                            { "Edition", string.IsNullOrWhiteSpace(namingMetadata.Edition) ? string.Empty : namingMetadata.Edition },
                            { "Narrator", string.IsNullOrWhiteSpace(namingMetadata.Narrator) ? string.Empty : namingMetadata.Narrator },
                            { "Publisher", string.IsNullOrWhiteSpace(namingMetadata.Publisher) ? string.Empty : namingMetadata.Publisher },
                            { "Language", string.IsNullOrWhiteSpace(namingMetadata.Language) ? string.Empty : namingMetadata.Language },
                            { "Asin", string.IsNullOrWhiteSpace(namingMetadata.Asin) ? string.Empty : namingMetadata.Asin },
                            { "SeriesNumber", namingMetadata.SeriesPosition?.ToString() ?? effectiveChapterNumber?.ToString() ?? string.Empty },
                            { "Year", namingMetadata.Year?.ToString() ?? string.Empty },
                            { "Quality", (namingMetadata.BitRate.HasValue ? $"{namingMetadata.BitRate}kbps" : null) ?? namingMetadata.Format ?? string.Empty },
                            { "DiskNumber", effectiveDiskNumber?.ToString() ?? string.Empty },
                            { "ChapterNumber", effectiveChapterNumber?.ToString() ?? string.Empty }
                        };

                        if ((abForNaming == null || string.IsNullOrWhiteSpace(abForNaming.BasePath)) && !string.IsNullOrWhiteSpace(folderPattern))
                        {
                            var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variablesForFile, treatAsFilename: false);
                            if (!string.IsNullOrWhiteSpace(folderRelative))
                            {
                                destDirForFile = CombineWithOptionalBase(destDirForFile, folderRelative);
                            }
                        }

                        batchDestinationRoot ??= destDirForFile;

                        // Ensure destination directory exists (create if missing)
                        // For directory imports we create the destination directory when possible so multi-file releases
                        // can be imported into a new library folder. If creation fails, skip this file and record a warning.
                        if (string.IsNullOrWhiteSpace(destDirForFile))
                        {
                            res.Success = false;
                            res.Message = "Destination directory not configured";
                            res.SkippedReason = destDirForFile;
                            _logger.LogWarning("ImportFilesFromDirectory: Destination directory not configured for multi-file import: {Source}", file);
                            results.Add(res);
                            continue;
                        }

                        try
                        {
                            // Ensure the directory exists (create if necessary)
                            Directory.CreateDirectory(destDirForFile);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            res.Success = false;
                            res.Message = "Destination directory does not exist and could not be created";
                            res.SkippedReason = destDirForFile;
                            _logger.LogWarning(ex, "ImportFilesFromDirectory: Failed to create destination directory for multi-file import: {DestDir}. Keeping source file: {Source}", destDirForFile, file);
                            results.Add(res);
                            continue;
                        }

                        var isMultiFile = isMultiFileBatch || effectiveDiskNumber.HasValue || effectiveChapterNumber.HasValue;
                        var baseFilePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;
                        var filenamePattern = abForNaming != null
                            ? (string.IsNullOrWhiteSpace(baseFilePattern) ? "{Title}" : baseFilePattern)
                            : baseFilePattern;
                        if (string.IsNullOrWhiteSpace(folderPattern) && string.IsNullOrWhiteSpace(filenamePattern))
                            filenamePattern = "{Author}/{Series}/{Title}";
                        else if (string.IsNullOrWhiteSpace(filenamePattern))
                            filenamePattern = "{Title}";

                        var ext = Path.GetExtension(file);
                        var patternHasNumberTokens = !string.IsNullOrWhiteSpace(filenamePattern)
                            && (filenamePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                                || filenamePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

                        var patternAllowsSubfolders = filenamePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                            || filenamePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                            || filenamePattern.IndexOf('/') >= 0
                            || filenamePattern.IndexOf('\\') >= 0;
                        var treatAsFilename = abForNaming != null || !patternAllowsSubfolders;

                        var filename = _fileNamingService.ApplyNamingPattern(filenamePattern, variablesForFile, treatAsFilename);
                        if (isMultiFile && !patternHasNumberTokens && stableSuffixNumber.HasValue)
                        {
                            filename = FileUtils.AppendSequenceSuffix(filename, stableSuffixNumber.Value);
                        }
                        if (!filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) filename += ext;

                        if (!patternAllowsSubfolders)
                        {
                            try
                            {
                                var forced = Path.GetFileName(filename);
                                var invalid = Path.GetInvalidFileNameChars();
                                var sb = new System.Text.StringBuilder();
                                foreach (var c in forced)
                                {
                                    sb.Append(invalid.Contains(c) ? '_' : c);
                                }
                                filename = sb.ToString();
                            }
                            catch (Exception caughtEx_7) when (caughtEx_7 is not OperationCanceledException && caughtEx_7 is not OutOfMemoryException && caughtEx_7 is not StackOverflowException)
                            {
                                filename = Path.GetFileName(filename);
                            }
                        }

                        var destPathForFile = CombineWithOptionalBase(destDirForFile, filename);

                        // After generating the target filename, we'll still place the file into
                        // the destination directory first (original filename) then apply
                        // the naming pattern on that destination file so that the file
                        // exists in the destination before any renaming occurs.
                        var initialDest = Path.Join(destDirForFile, Path.GetFileName(file));
                        var uniqueInitial = FileUtils.GetUniqueDestinationPath(initialDest);

                        if (string.Equals(completedFileAction, "Copy", StringComparison.OrdinalIgnoreCase))
                        {
                            var ok = await _fileMover.CopyFileAsync(file, uniqueInitial);
                            if (ok)
                            {
                                _logger.LogInformation("ImportFilesFromDirectory: Copied file {Source} -> {Dest}", file, uniqueInitial);
                                res.WasCopied = true;
                            }
                        }
                        else if (string.Equals(completedFileAction, "Hardlink/Copy", StringComparison.OrdinalIgnoreCase))
                        {
                            var ok = await _fileMover.HardlinkFileAsync(file, uniqueInitial);
                            if (!ok)
                            {
                                _logger.LogWarning("ImportFilesFromDirectory: Hardlink failed for {Source}, attempting copy fallback", file);
                                ok = await _fileMover.CopyFileAsync(file, uniqueInitial);
                            }

                            if (ok)
                            {
                                _logger.LogInformation("ImportFilesFromDirectory: Hardlinked/copied file {Source} -> {Dest}", file, uniqueInitial);
                                res.WasCopied = true;
                            }
                            else
                            {
                                throw new IOException("Hardlink/Copy failed");
                            }
                        }
                        else
                        {
                            var ok = await _fileMover.MoveFileAsync(file, uniqueInitial);
                            if (ok)
                            {
                                _logger.LogInformation("ImportFilesFromDirectory: Moved file {Source} -> {Dest}", file, uniqueInitial);
                                res.WasMoved = true;
                            }
                        }

                        // Now apply the filename pattern on the destination copy/move
                        var uniqueFinal = FileUtils.GetUniqueDestinationPath(destPathForFile);

                        // If the final name differs from the initial unique path, move/rename it
                        if (!string.Equals(Path.GetFullPath(uniqueInitial), Path.GetFullPath(uniqueFinal), StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var ok = await _fileMover.MoveFileAsync(uniqueInitial, uniqueFinal);
                                if (ok)
                                {
                                    _logger.LogInformation("ImportFilesFromDirectory: Renamed/Moved destination file {Source} -> {Final}", uniqueInitial, uniqueFinal);
                                }
                                else
                                {
                                    _logger.LogWarning("ImportFilesFromDirectory: Failed to apply naming/rename on multi-file import for {File}", uniqueInitial);
                                    uniqueFinal = uniqueInitial;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "ImportFilesFromDirectory: Failed to apply naming/rename on multi-file import for {File}", uniqueInitial);
                                uniqueFinal = uniqueInitial;
                            }
                        }

                        res.FinalPath = uniqueFinal;
                        res.Success = true;

                        if (audiobookId.HasValue)
                        {
                            await EnsureAudiobookBasePathAsync(audiobookId.Value, destDirForFile, ct);
                        }

                        // Register audiobook file if linked
                        if (audiobookId != null)
                        {
                            try
                            {
                                using var afScope = _scopeFactory.CreateScope();
                                var audioFileService = afScope.ServiceProvider.GetService<IAudioFileService>()
                                    ?? ActivatorUtilities.CreateInstance<AudioFileService>(afScope.ServiceProvider,
                                        _scopeFactory,
                                        afScope.ServiceProvider.GetService<ILogger<AudioFileService>>() ?? new Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioFileService>(),
                                        afScope.ServiceProvider.GetRequiredService<IMemoryCache>(),
                                        afScope.ServiceProvider.GetRequiredService<MetadataExtractionLimiter>());

                                // Always store absolute path for downloads - metadata extraction needs full path
                                var created = await audioFileService.EnsureAudiobookFileAsync(audiobookId.Value, res.FinalPath, "download");
                                res.WasRegisteredToAudiobook = created;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "ImportFilesFromDirectory: Failed to create AudiobookFile for imported file {File}", file);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        res.Success = false;
                        res.Message = ex.Message;
                        _logger.LogWarning(ex, "ImportFilesFromDirectory: Failed processing file in directory import: {File}", file);
                    }

                    results.Add(res);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "ImportFilesFromDirectory: Failed to import files from directory for download {DownloadId}", downloadId);
            }

            if (string.Equals(completedFileAction, "Move", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(sourceRootPath))
            {
                FileUtils.DeleteEmptyDirectories(sourceRootPath);
            }

            return results;
        }

        public Task<ImportResult> ReprocessExistingFileAsync(string downloadId, int? audiobookId, string sourcePath, ApplicationSettings settings, CancellationToken ct = default)
        {
            // For reprocessing we can reuse ImportSingleFileAsync semantics
            return ImportSingleFileAsync(downloadId, audiobookId, sourcePath, settings, ct);
        }

        private static AudioMetadata BuildNamingMetadata(Audiobook? audiobook, AudioMetadata? extractedMetadata, string fallbackTitle)
        {
            if (audiobook != null)
            {
                var author = (audiobook.Authors != null && audiobook.Authors.Any())
                    ? string.Join(", ", audiobook.Authors)
                    : FirstNonEmpty(ChooseAuthorFromMetadata(extractedMetadata), "Unknown Author");

                return new AudioMetadata
                {
                    Title = FirstNonEmpty(audiobook.Title, extractedMetadata?.Title, fallbackTitle, "Unknown Title"),
                    Subtitle = FirstNonEmpty(audiobook.Subtitle, extractedMetadata?.Subtitle),
                    Edition = FirstNonEmpty(audiobook.Edition, extractedMetadata?.Edition),
                    Artist = author,
                    AlbumArtist = author,
                    Album = FirstNonEmpty(extractedMetadata?.Album, audiobook.Title, fallbackTitle),
                    Narrator = (audiobook.Narrators != null && audiobook.Narrators.Any())
                        ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n)))
                        : extractedMetadata?.Narrator,
                    Publisher = FirstNonEmpty(audiobook.Publisher, extractedMetadata?.Publisher),
                    Language = FirstNonEmpty(audiobook.Language, extractedMetadata?.Language),
                    Asin = FirstNonEmpty(audiobook.Asin, extractedMetadata?.Asin),
                    Series = FirstNonEmpty(audiobook.Series, extractedMetadata?.Series),
                    SeriesPosition = !string.IsNullOrWhiteSpace(audiobook.SeriesNumber) && decimal.TryParse(audiobook.SeriesNumber, out var sp)
                        ? sp
                        : (extractedMetadata?.SeriesPosition),
                    Year = !string.IsNullOrWhiteSpace(audiobook.PublishYear) && int.TryParse(audiobook.PublishYear, out var year)
                        ? year
                        : extractedMetadata?.Year,
                    TrackNumber = extractedMetadata?.TrackNumber,
                    DiscNumber = extractedMetadata?.DiscNumber,
                    BitRate = extractedMetadata?.BitRate,
                    Format = extractedMetadata?.Format
                };
            }

            if (extractedMetadata != null)
            {
                if (string.IsNullOrWhiteSpace(extractedMetadata.Title))
                {
                    extractedMetadata.Title = fallbackTitle;
                }

                if (string.IsNullOrWhiteSpace(extractedMetadata.Artist))
                {
                    extractedMetadata.Artist = FirstNonEmpty(ChooseAuthorFromMetadata(extractedMetadata), "Unknown Author");
                }

                if (string.IsNullOrWhiteSpace(extractedMetadata.AlbumArtist))
                {
                    extractedMetadata.AlbumArtist = extractedMetadata.Artist;
                }

                return extractedMetadata;
            }

            return new AudioMetadata
            {
                Title = fallbackTitle,
                Artist = "Unknown Author",
                AlbumArtist = "Unknown Author"
            };
        }

        private static string ChooseAuthorFromMetadata(AudioMetadata? metadata)
        {
            if (metadata == null)
            {
                return string.Empty;
            }

            var primary = NonNarratorAuthorCandidate(metadata.Artist, metadata.Narrator);
            var alternate = NonNarratorAuthorCandidate(metadata.AlbumArtist, metadata.Narrator);

            if (string.IsNullOrWhiteSpace(primary))
            {
                return alternate;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Title) &&
                (primary.IndexOf(metadata.Title, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (!string.IsNullOrWhiteSpace(metadata.Series) && string.Equals(primary, metadata.Series, StringComparison.OrdinalIgnoreCase)) ||
                 string.Equals(primary, metadata.Title, StringComparison.OrdinalIgnoreCase)))
            {
                return string.IsNullOrWhiteSpace(alternate) ? primary : alternate;
            }

            return primary;
        }

        private static string NonNarratorAuthorCandidate(string? candidate, string? narrator)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            var trimmedCandidate = candidate.Trim();
            if (!string.IsNullOrWhiteSpace(narrator) &&
                string.Equals(trimmedCandidate, narrator.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return trimmedCandidate;
        }

        private async Task EnsureAudiobookBasePathAsync(int audiobookId, string? candidateBasePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(candidateBasePath))
            {
                return;
            }

            try
            {
                var normalizedCandidate = Path.GetFullPath(candidateBasePath);
                normalizedCandidate = FileUtils.NormalizeStoredPath(normalizedCandidate);

                var audiobook = await _audiobookRepository.GetByIdAsync(audiobookId);
                if (audiobook == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(audiobook.BasePath))
                {
                    var normalizedExisting = FileUtils.NormalizeStoredPath(audiobook.BasePath);
                    var matchesExisting = string.Equals(normalizedExisting, normalizedCandidate, StringComparison.OrdinalIgnoreCase);
                    var candidateWithinExisting = FileUtils.IsPathInsideOf(normalizedCandidate, normalizedExisting);
                    if (matchesExisting || candidateWithinExisting)
                    {
                        if (!string.Equals(audiobook.BasePath, normalizedExisting, StringComparison.Ordinal))
                        {
                            audiobook.BasePath = normalizedExisting;
                            await _audiobookRepository.UpdateAsync(audiobook);
                        }

                        return;
                    }
                }

                audiobook.BasePath = normalizedCandidate;
                await _audiobookRepository.UpdateAsync(audiobook);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to update audiobook {AudiobookId} BasePath to {BasePath} before file association", audiobookId, candidateBasePath);
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

        // Local helpers - aligned with DownloadService helper behavior
        private static string DetermineQualityFromMetadata(AudioMetadata? metadata, string path)
        {
            if (metadata != null)
            {
                if (!string.IsNullOrEmpty(metadata.Format)) return metadata.Format;
                if (metadata.BitRate.HasValue) return metadata.BitRate.Value + "kbps";
            }

            // Best-effort from filename (bitrate patterns)
            var name = Path.GetFileName(path) ?? string.Empty;
            if (name.IndexOf("320", StringComparison.OrdinalIgnoreCase) >= 0) return "MP3 320kbps";
            if (name.IndexOf("256", StringComparison.OrdinalIgnoreCase) >= 0) return "MP3 256kbps";
            if (name.IndexOf("192", StringComparison.OrdinalIgnoreCase) >= 0) return "MP3 192kbps";
            if (name.IndexOf("128", StringComparison.OrdinalIgnoreCase) >= 0) return "MP3 128kbps";

            // Fallback: determine format from file extension
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext))
            {
                switch (ext.TrimStart('.').ToUpperInvariant())
                {
                    case "M4B": return "M4B";
                    case "M4A": return "M4A";
                    case "MP3": return "MP3";
                    case "FLAC": return "FLAC";
                    case "OGG": return "OGG";
                    case "OPUS": return "OPUS";
                    case "WMA": return "WMA";
                    case "AAC": return "AAC";
                    case "WV": return "WV";
                    default: break;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Returns true if the candidate quality is acceptable (not a confirmed downgrade).
        /// Only blocks import when both qualities have numeric bitrates and the candidate is strictly lower.
        /// Same quality, unknown quality, or non-comparable formats are all allowed.
        /// </summary>
        private static bool IsQualityBetter(string? candidate, string? existing, QualityProfile? profile)
        {
            // When candidate or existing quality is unknown, allow the import rather than blocking
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(existing) || profile == null) return true;

            // Extract numeric bitrate if present.
            // Look for groups of 2+ consecutive digits to avoid picking up single
            // digits embedded in format names (e.g. "4" from M4B, "3" from MP3).
            bool TryParse(string q, out int kb)
            {
                kb = 0;
                var match = System.Text.RegularExpressions.Regex.Match(q, @"\d{2,}");
                if (match.Success && int.TryParse(match.Value, out var d))
                {
                    kb = d;
                    return true;
                }
                return false;
            }

            // When both have numeric bitrates, only block if candidate is strictly lower
            if (TryParse(candidate, out var candKb) && TryParse(existing, out var exKb))
            {
                return candKb >= exKb;
            }

            // For non-numeric formats (M4B, FLAC, etc.): allow the import.
            // Same format is a reimport (not a downgrade), and we can't reliably
            // rank different format names against each other.
            return true;
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
            {
                return candidate!;
            }

            return string.Empty;
        }
    }
}

// Simple no-op/fallback file mover used for compatibility in tests when DI IFileMover isn't provided.
internal class NullFileMover : IFileMover
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkWin(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    public Task<bool> CopyDirectoryAsync(string sourceDir, string destDir)
    {
        try
        {
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file);
                var normalizedDestDir = destDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var relativePath = rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dest = string.IsNullOrEmpty(normalizedDestDir)
                    ? relativePath
                    : normalizedDestDir + Path.DirectorySeparatorChar + relativePath;
                var d = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                File.Copy(file, dest, true);
            }
            return Task.FromResult(true);
        }
        catch (Exception caughtEx_8) when (caughtEx_8 is not OperationCanceledException && caughtEx_8 is not OutOfMemoryException && caughtEx_8 is not StackOverflowException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> CopyFileAsync(string sourceFile, string destFile)
    {
        try
        {
            var d = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
            File.Copy(sourceFile, destFile, true);
            return Task.FromResult(true);
        }
        catch (Exception caughtEx_9) when (caughtEx_9 is not OperationCanceledException && caughtEx_9 is not OutOfMemoryException && caughtEx_9 is not StackOverflowException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> HardlinkFileAsync(string sourceFile, string destFile)
    {
        try
        {
            var d = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
            if (File.Exists(destFile)) File.Delete(destFile);
            try
            {
                // Try P/Invoke hardlink
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    if (!CreateHardLinkWin(destFile, sourceFile, IntPtr.Zero))
                        throw new IOException("Hardlink failed");
                }
                else
                {
                    if (link(sourceFile, destFile) != 0)
                        throw new IOException("Hardlink failed");
                }
                return Task.FromResult(true);
            }
            catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException)
            {
                // Fallback to copy
                File.Copy(sourceFile, destFile, true);
                return Task.FromResult(true);
            }
        }
        catch (Exception caughtEx_11) when (caughtEx_11 is not OperationCanceledException && caughtEx_11 is not OutOfMemoryException && caughtEx_11 is not StackOverflowException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> MoveDirectoryAsync(string sourceDir, string destDir)
    {
        try
        {
            if (Directory.Exists(destDir))
            {
                // fallback: copy contents then delete
                var ok = CopyDirectoryAsync(sourceDir, destDir).GetAwaiter().GetResult();
                if (ok) Directory.Delete(sourceDir, true);
                return Task.FromResult(ok);
            }

            Directory.Move(sourceDir, destDir);
            return Task.FromResult(true);
        }
        catch (Exception caughtEx_12) when (caughtEx_12 is not OperationCanceledException && caughtEx_12 is not OutOfMemoryException && caughtEx_12 is not StackOverflowException)
        {
            try
            {
                var ok = CopyDirectoryAsync(sourceDir, destDir).GetAwaiter().GetResult();
                if (ok) Directory.Delete(sourceDir, true);
                return Task.FromResult(ok);
            }
            catch (Exception caughtEx_13) when (caughtEx_13 is not OperationCanceledException && caughtEx_13 is not OutOfMemoryException && caughtEx_13 is not StackOverflowException)
            {
                return Task.FromResult(false);
            }
        }
    }

    public Task<bool> MoveFileAsync(string sourceFile, string destFile)
    {
        try
        {
            var d = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
            File.Move(sourceFile, destFile);
            return Task.FromResult(true);
        }
        catch (Exception caughtEx_14) when (caughtEx_14 is not OperationCanceledException && caughtEx_14 is not OutOfMemoryException && caughtEx_14 is not StackOverflowException)
        {
            try
            {
                File.Copy(sourceFile, destFile, true);
                File.Delete(sourceFile);
                return Task.FromResult(true);
            }
            catch (Exception caughtEx_15) when (caughtEx_15 is not OperationCanceledException && caughtEx_15 is not OutOfMemoryException && caughtEx_15 is not StackOverflowException)
            {
                return Task.FromResult(false);
            }
        }
    }

    public async Task PerformActionOn(FileAction action, string source, string? destination = null, HashSet<string>? usedDestinations = null)
    {
        if (destination == null) return;

        if (action != FileAction.None)
        {
            destination = FileUtils.GetUniqueDestinationPath(destination, File.Exists, usedDestinations);
            usedDestinations?.Add(destination);
        }

        switch (action)
        {
            case FileAction.Move:
                await MoveFileAsync(source, destination);
                return;
            case FileAction.Copy:
                await CopyFileAsync(source, destination);
                return;
            case FileAction.HardlinkCopy:
                await HardlinkFileAsync(source, destination);
                return;
        }
    }

}
