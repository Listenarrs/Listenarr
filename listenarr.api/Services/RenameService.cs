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
using Listenarr.Domain.Utils;

namespace Listenarr.Api.Services
{
    public class RenameService : IRenameService
    {
        private const int MaxAudiobookIds = 500;

        private readonly IConfigurationService _configService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IFileMover _fileMover;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly ILogger<RenameService> _logger;
        private readonly IRootFolderService? _rootFolderService;
        private readonly IHistoryRepository? _historyRepository;

        public RenameService(
            IConfigurationService configService,
            IFileNamingService fileNamingService,
            IFileMover fileMover,
            IAudiobookRepository audiobookRepository,
            ILogger<RenameService> logger,
            IRootFolderService? rootFolderService = null,
            IHistoryRepository? historyRepository = null)
        {
            _configService = configService;
            _fileNamingService = fileNamingService;
            _fileMover = fileMover;
            _audiobookRepository = audiobookRepository;
            _logger = logger;
            _rootFolderService = rootFolderService;
            _historyRepository = historyRepository;
        }

        public async Task<List<RenamePreview>> PreviewRenameAsync(int[] audiobookIds, CancellationToken ct = default)
        {
            if (audiobookIds == null || audiobookIds.Length == 0) return new();
            if (audiobookIds.Length > MaxAudiobookIds) throw new ArgumentException($"Cannot preview more than {MaxAudiobookIds} audiobooks at once.");

            var settings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            var rootFolders = await LoadRootFoldersAsync();

            var audiobooks = await _audiobookRepository.GetByIdsWithFilesAsync(audiobookIds, ct);

            return audiobooks.Select(a => BuildPreview(a, settings, rootFolders)).ToList();
        }

        public async Task<List<RenameResult>> ExecuteRenameAsync(List<RenameOperation> operations, CancellationToken ct = default)
        {
            if (operations == null || operations.Count == 0) return new();
            if (operations.Count > MaxAudiobookIds) throw new ArgumentException($"Cannot execute more than {MaxAudiobookIds} rename operations at once.");

            var settings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            var rootFolders = await LoadRootFoldersAsync();
            var results = new List<RenameResult>();
            foreach (var op in operations) results.Add(await ExecuteSingleAsync(op, settings, rootFolders, ct));
            return results;
        }

        private RenamePreview BuildPreview(Audiobook audiobook, ApplicationSettings settings, List<RootFolder> rootFolders)
        {
            var preview = new RenamePreview
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title,
                CurrentFolderPath = ComputeCurrentBasePath(audiobook)
            };

            var files = GetFileEntries(audiobook);
            if (files.Count == 0)
            {
                preview.NewFolderPath = preview.CurrentFolderPath;
                return preview;
            }

            var namingBase = ResolveNamingBasePath(preview.CurrentFolderPath, settings, rootFolders);
            var isMultiFile = files.Count > 1;
            var expectedPaths = new List<string>();

            foreach (var file in files)
            {
                var expectedPath = BuildExpectedPath(audiobook, file, settings, namingBase.BasePath, namingBase.IsCustomBasePath, isMultiFile);
                expectedPaths.Add(expectedPath);
                preview.FileRenames.Add(new FileRenamePreview
                {
                    FileId = file.FileId,
                    CurrentPath = file.CurrentPath,
                    NewPath = expectedPath,
                    CurrentFilename = Path.GetFileName(file.CurrentPath),
                    NewFilename = Path.GetFileName(expectedPath),
                    Changed = !PathsEqual(file.CurrentPath, expectedPath)
                });
            }

            preview.NewFolderPath = ComputeCommonBasePath(expectedPaths);
            preview.FolderChanged = !PathsEqual(preview.CurrentFolderPath, preview.NewFolderPath);
            preview.HasChanges = preview.FolderChanged || preview.FileRenames.Any(f => f.Changed);
            return preview;
        }

        private async Task<RenameResult> ExecuteSingleAsync(RenameOperation operation, ApplicationSettings settings, List<RootFolder> rootFolders, CancellationToken ct)
        {
            var result = new RenameResult { AudiobookId = operation.AudiobookId };
            try
            {
                var audiobook = await _audiobookRepository.GetByIdAsync(operation.AudiobookId);
                if (audiobook == null)
                {
                    result.Success = false;
                    result.Error = "Audiobook not found.";
                    return result;
                }

                var currentBasePath = ComputeCurrentBasePath(audiobook);
                var allowedRoots = BuildAllowedRoots(settings, rootFolders, currentBasePath);
                var anySucceeded = false;
                var hasFileOperations = operation.FileRenames != null && operation.FileRenames.Count > 0;

                foreach (var fileOp in operation.FileRenames ?? new())
                {
                    var fileResult = await ExecuteFileRenameAsync(audiobook, fileOp, allowedRoots);
                    result.RenamedFiles.Add(fileResult);
                    anySucceeded |= fileResult.Success;
                }

                if ((operation.FileRenames == null || operation.FileRenames.Count == 0)
                    && !string.IsNullOrWhiteSpace(operation.NewFolderPath)
                    && !PathsEqual(currentBasePath, operation.NewFolderPath))
                {
                    var dirMove = await ExecuteDirectoryMoveAsync(audiobook, operation.NewFolderPath!, allowedRoots);
                    result.Success = dirMove.Success;
                    result.Error = dirMove.Error;
                    anySucceeded |= dirMove.Success;
                }

                if (result.RenamedFiles.Count > 0)
                {
                    result.Success = result.RenamedFiles.All(f => f.Success);
                    if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                    {
                        result.Error = "One or more file organize operations failed.";
                    }
                }
                else if (!result.Success)
                {
                    result.Success = anySucceeded;
                }

                if (anySucceeded)
                {
                    var shouldTrustRequestedBasePath = !string.IsNullOrWhiteSpace(operation.NewFolderPath)
                        && (!hasFileOperations || result.Success);
                    UpdateAudiobookPathSummary(audiobook, shouldTrustRequestedBasePath ? operation.NewFolderPath : null);
                    await _audiobookRepository.SaveChangesAsync(ct);
                    await AddHistoryAsync(audiobook, result);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to execute organize operation for audiobook {AudiobookId}", operation.AudiobookId);
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private async Task<FileRenameResultItem> ExecuteFileRenameAsync(Audiobook audiobook, FileRenameOperation fileOperation, IReadOnlyCollection<string> allowedRoots)
        {
            var source = NormalizePath(fileOperation.CurrentPath);
            var dest = NormalizePath(fileOperation.NewPath);
            var item = new FileRenameResultItem { FileId = fileOperation.FileId, PreviousPath = source, NewPath = dest };
            var trackedSourcePath = string.Empty;
            AudiobookFile? dbFile = null;

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
            {
                item.Success = false;
                item.Error = "File organize operation is missing a source or destination path.";
                return item;
            }

            if (fileOperation.FileId == 0)
            {
                trackedSourcePath = NormalizePath(audiobook.FilePath);
                if (string.IsNullOrWhiteSpace(trackedSourcePath))
                {
                    item.Success = false;
                    item.Error = "Legacy file organize operation does not match a tracked audiobook file.";
                    return item;
                }
            }
            else
            {
                dbFile = audiobook.Files?.FirstOrDefault(f => f.Id == fileOperation.FileId);
                if (dbFile == null)
                {
                    item.Success = false;
                    item.Error = "File does not belong to this audiobook.";
                    return item;
                }

                trackedSourcePath = NormalizePath(dbFile.Path);
                if (string.IsNullOrWhiteSpace(trackedSourcePath))
                {
                    item.Success = false;
                    item.Error = "Tracked audiobook file path is missing.";
                    return item;
                }
            }

            if (!PathsEqual(source, trackedSourcePath))
            {
                item.Success = false;
                item.Error = "Source path does not match the tracked audiobook file.";
                return item;
            }

            if (!IsPathWithinAllowedRoots(source, allowedRoots) || !IsPathWithinAllowedRoots(dest, allowedRoots))
            {
                item.Success = false;
                item.Error = "File path is outside the allowed library roots.";
                return item;
            }

            if (!File.Exists(source))
            {
                item.Success = false;
                item.Error = "Source file not found.";
                return item;
            }

            if (File.Exists(dest) && !PathsEqual(source, dest))
            {
                item.Success = false;
                item.Error = "Target file already exists.";
                return item;
            }

            try
            {
                var targetDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrWhiteSpace(targetDir)) Directory.CreateDirectory(targetDir);

                if (!PathsEqual(source, dest))
                {
                    var moved = await _fileMover.MoveFileAsync(source, dest);
                    if (!moved)
                    {
                        item.Success = false;
                        item.Error = "File move operation failed.";
                        return item;
                    }
                }

                if (dbFile != null) dbFile.Path = dest;
                else if (fileOperation.FileId == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath)) audiobook.FilePath = dest;

                item.Success = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to organize file {FileId} for audiobook {AudiobookId}", fileOperation.FileId, audiobook.Id);
                item.Success = false;
                item.Error = ex.Message;
            }

            return item;
        }

        private async Task<(bool Success, string? Error)> ExecuteDirectoryMoveAsync(Audiobook audiobook, string newFolderPath, IReadOnlyCollection<string> allowedRoots)
        {
            var currentBase = ComputeCurrentBasePath(audiobook);
            if (string.IsNullOrWhiteSpace(currentBase))
            {
                audiobook.BasePath = NormalizePath(newFolderPath);
                return (true, null);
            }

            var normalizedCurrent = NormalizePath(currentBase);
            var normalizedNew = NormalizePath(newFolderPath);
            if (!IsPathWithinAllowedRoots(normalizedCurrent, allowedRoots) || !IsPathWithinAllowedRoots(normalizedNew, allowedRoots))
                return (false, "Destination path is outside the allowed library roots.");
            if (!Directory.Exists(normalizedCurrent))
            {
                audiobook.BasePath = normalizedNew;
                return (true, null);
            }
            if (Directory.Exists(normalizedNew) && Directory.EnumerateFileSystemEntries(normalizedNew).Any())
                return (false, "Target folder already exists and is not empty.");

            var parent = Path.GetDirectoryName(normalizedNew);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

            var moved = await _fileMover.MoveDirectoryAsync(normalizedCurrent, normalizedNew);
            if (!moved) return (false, "Folder move operation failed.");

            audiobook.BasePath = normalizedNew;
            if (audiobook.Files != null)
            {
                foreach (var file in audiobook.Files.Where(f => !string.IsNullOrWhiteSpace(f.Path)
                    && (PathsEqual(f.Path, normalizedCurrent) || FileUtils.IsPathInsideOf(f.Path!, normalizedCurrent))))
                {
                    var relative = Path.GetRelativePath(normalizedCurrent, file.Path!);
                    file.Path = CombineRelativePath(normalizedNew, relative);
                }
            }
            if (!string.IsNullOrWhiteSpace(audiobook.FilePath)
                && (PathsEqual(audiobook.FilePath, normalizedCurrent) || FileUtils.IsPathInsideOf(audiobook.FilePath, normalizedCurrent)))
            {
                var relative = Path.GetRelativePath(normalizedCurrent, audiobook.FilePath);
                audiobook.FilePath = CombineRelativePath(normalizedNew, relative);
            }

            return (true, null);
        }

        private async Task AddHistoryAsync(Audiobook audiobook, RenameResult result)
        {
            if (_historyRepository == null) return;
            try
            {
                var fileCount = result.RenamedFiles.Count(f => f.Success);
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(audiobook.BasePath)) parts.Add("folder organized");
                if (fileCount > 0) parts.Add($"{fileCount} file(s) renamed");
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title,
                    EventType = "Organized",
                    Message = parts.Count == 0 ? "Files organized" : string.Join(", ", parts),
                    Source = "Organize",
                    Timestamp = DateTime.UtcNow
                }, default);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to write organize history for audiobook {AudiobookId}", audiobook.Id);
            }
        }

        private void UpdateAudiobookPathSummary(Audiobook audiobook, string? requestedBasePath)
        {
            var filePaths = audiobook.Files?.Where(f => !string.IsNullOrWhiteSpace(f.Path)).Select(f => f.Path!).ToList() ?? new();
            if (filePaths.Count == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath)) filePaths.Add(audiobook.FilePath);

            audiobook.BasePath = !string.IsNullOrWhiteSpace(requestedBasePath) ? NormalizePath(requestedBasePath) : ComputeCommonBasePath(filePaths);
            if (filePaths.Count == 0) return;

            var primary = filePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
            audiobook.FilePath = primary;
            if (audiobook.Files != null)
            {
                var primaryFile = audiobook.Files.FirstOrDefault(f => PathsEqual(f.Path, primary));
                if (primaryFile != null && primaryFile.Size > 0) audiobook.FileSize = primaryFile.Size;
            }
        }

        private static List<PreviewFileEntry> GetFileEntries(Audiobook audiobook)
        {
            var entries = new List<PreviewFileEntry>();
            if (audiobook.Files != null && audiobook.Files.Count > 0)
            {
                var ordered = audiobook.Files.Where(f => !string.IsNullOrWhiteSpace(f.Path)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var file = ordered[i];
                    entries.Add(new PreviewFileEntry(file.Id, NormalizePath(file.Path!), Path.GetExtension(file.Path!) ?? ".m4b", i + 1));
                }
                return entries;
            }

            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
                entries.Add(new PreviewFileEntry(0, NormalizePath(audiobook.FilePath), Path.GetExtension(audiobook.FilePath) ?? ".m4b", 1));
            return entries;
        }

        private string BuildExpectedPath(Audiobook audiobook, PreviewFileEntry file, ApplicationSettings settings, string basePath, bool isCustomBasePath, bool isMultiFile)
        {
            var folderPattern = settings.FolderNamingPattern;
            var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;
            var variables = BuildNamingVariables(audiobook, file.SequenceNumber, isMultiFile);
            var patternHasNumberTokens = !string.IsNullOrWhiteSpace(filePattern)
                && (filePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0 || filePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

            string relativePath;
            if (string.IsNullOrWhiteSpace(folderPattern))
            {
                var legacyPattern = string.IsNullOrWhiteSpace(filePattern) ? "{Author}/{Title}/{Title}" : filePattern;
                relativePath = _fileNamingService.ApplyNamingPattern(legacyPattern, variables, false);
            }
            else if (isCustomBasePath)
            {
                var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
                relativePath = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, !PatternAllowsSubfolders(effectiveFilePattern));
            }
            else
            {
                var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
                var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, false);
                var fileRelative = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, !PatternAllowsSubfolders(effectiveFilePattern));
                if (isMultiFile && !patternHasNumberTokens) fileRelative = FileUtils.AppendSequenceSuffix(fileRelative, file.SequenceNumber);
                relativePath = string.IsNullOrWhiteSpace(folderRelative) ? fileRelative : CombineWithOptionalBase(folderRelative, fileRelative);
            }

            if ((string.IsNullOrWhiteSpace(folderPattern) || isCustomBasePath) && isMultiFile && !patternHasNumberTokens)
                relativePath = FileUtils.AppendSequenceSuffix(relativePath, file.SequenceNumber);
            if (!relativePath.EndsWith(file.Extension, StringComparison.OrdinalIgnoreCase)) relativePath += file.Extension;

            return string.IsNullOrWhiteSpace(basePath) ? NormalizePath(relativePath) : NormalizePath(CombineWithOptionalBase(basePath, relativePath));
        }

        private static Dictionary<string, object> BuildNamingVariables(Audiobook audiobook, int sequenceNumber, bool isMultiFile)
        {
            var narrator = audiobook.Narrators != null ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n))) : string.Empty;

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "Author", audiobook.Authors?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Unknown Author" },
                { "Series", audiobook.Series ?? string.Empty },
                { "Title", string.IsNullOrWhiteSpace(audiobook.Title) ? "Unknown Title" : audiobook.Title },
                { "Subtitle", audiobook.Subtitle ?? string.Empty },
                { "Edition", audiobook.Edition ?? string.Empty },
                { "Narrator", narrator },
                { "Publisher", audiobook.Publisher ?? string.Empty },
                { "Language", audiobook.Language ?? string.Empty },
                { "Asin", audiobook.Asin ?? string.Empty },
                { "SeriesNumber", audiobook.SeriesNumber ?? string.Empty },
                { "Year", audiobook.PublishYear ?? string.Empty },
                { "Quality", audiobook.Quality ?? string.Empty },
                { "DiskNumber", isMultiFile ? sequenceNumber : string.Empty },
                { "ChapterNumber", isMultiFile ? sequenceNumber : string.Empty }
            };
        }

        private async Task<List<RootFolder>> LoadRootFoldersAsync()
        {
            if (_rootFolderService == null) return new();
            try { return await _rootFolderService.GetAllAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load root folders for organize preview; falling back to application output path");
                return new();
            }
        }

        private static (string BasePath, bool IsCustomBasePath) ResolveNamingBasePath(string? currentBasePath, ApplicationSettings settings, List<RootFolder> rootFolders)
        {
            if (string.IsNullOrWhiteSpace(currentBasePath))
            {
                var defaultRoot = rootFolders.FirstOrDefault(r => r.IsDefault)?.Path;
                return (NormalizePath(!string.IsNullOrWhiteSpace(defaultRoot) ? defaultRoot : settings.OutputPath), false);
            }

            var normalizedCurrent = NormalizePath(currentBasePath);
            var matchingRoot = rootFolders.Where(r => IsSamePathOrWithin(normalizedCurrent, NormalizePath(r.Path)))
                .OrderByDescending(r => NormalizePath(r.Path).Length).FirstOrDefault();
            if (matchingRoot != null) return (NormalizePath(matchingRoot.Path), false);
            if (!string.IsNullOrWhiteSpace(settings.OutputPath) && IsSamePathOrWithin(normalizedCurrent, NormalizePath(settings.OutputPath)))
                return (NormalizePath(settings.OutputPath), false);
            return (normalizedCurrent, true);
        }

        private static IReadOnlyCollection<string> BuildAllowedRoots(ApplicationSettings settings, List<RootFolder> rootFolders, string? currentBasePath)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(settings.OutputPath)) roots.Add(NormalizePath(settings.OutputPath));
            foreach (var root in rootFolders.Where(r => !string.IsNullOrWhiteSpace(r.Path))) roots.Add(NormalizePath(root.Path));
            if (!string.IsNullOrWhiteSpace(currentBasePath)) roots.Add(NormalizePath(currentBasePath));
            return roots.ToList();
        }

        private static bool IsPathWithinAllowedRoots(string path, IReadOnlyCollection<string> allowedRoots)
            => !string.IsNullOrWhiteSpace(path) && allowedRoots.Any(root => IsSamePathOrWithin(path, root));

        private static string ComputeCurrentBasePath(Audiobook audiobook)
        {
            if (!string.IsNullOrWhiteSpace(audiobook.BasePath)) return NormalizePath(audiobook.BasePath);
            var filePaths = audiobook.Files?.Where(f => !string.IsNullOrWhiteSpace(f.Path)).Select(f => f.Path!).ToList() ?? new();
            if (filePaths.Count == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath)) filePaths.Add(audiobook.FilePath);
            return ComputeCommonBasePath(filePaths);
        }

        private static string ComputeCommonBasePath(IEnumerable<string> paths)
        {
            var normalized = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizePath).ToList();
            if (normalized.Count == 0) return string.Empty;
            if (normalized.Count == 1)
            {
                var single = normalized[0];
                return Directory.Exists(single) ? single : NormalizePath(Path.GetDirectoryName(single) ?? single);
            }

            var common = FileUtils.GetCommonDirectory(normalized);
            return string.IsNullOrWhiteSpace(common) ? string.Empty : NormalizePath(common);
        }

        private static string CombineWithOptionalBase(string basePath, string relativePath)
        {
            var safeRelative = relativePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(basePath)) return safeRelative;
            if (Path.IsPathRooted(safeRelative)) return safeRelative;
            return Path.Join(basePath, safeRelative);
        }

        private static string CombineRelativePath(string basePath, string relativePath)
        {
            var safeRelative = relativePath ?? string.Empty;
            if (Path.IsPathRooted(safeRelative))
            {
                var root = Path.GetPathRoot(safeRelative);
                if (!string.IsNullOrWhiteSpace(root) && safeRelative.Length >= root.Length)
                {
                    safeRelative = safeRelative[root.Length..];
                }
            }

            safeRelative = safeRelative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(safeRelative))
            {
                var root = Path.GetPathRoot(safeRelative);
                if (!string.IsNullOrWhiteSpace(root) && safeRelative.Length >= root.Length)
                {
                    safeRelative = safeRelative[root.Length..];
                }

                safeRelative = safeRelative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return NormalizePath(Path.Join(basePath, safeRelative));
        }

        private static string NormalizePath(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : FileUtils.NormalizeStoredPath(path);

        private static bool PathsEqual(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                && string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

        private static bool IsSamePathOrWithin(string childPath, string rootPath)
            => PathsEqual(childPath, rootPath) || FileUtils.IsPathInsideOf(childPath, rootPath);

        private static bool PatternAllowsSubfolders(string pattern)
            => pattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || pattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || pattern.IndexOf('/') >= 0
                || pattern.IndexOf('\\') >= 0;

        private sealed record PreviewFileEntry(int FileId, string CurrentPath, string Extension, int SequenceNumber);
    }
}
