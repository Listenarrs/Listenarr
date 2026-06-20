/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;
namespace Listenarr.Application.Audiobooks.Renaming
{
    public partial class RenameService
    {
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
            var variables = BuildNamingVariables(audiobook, folderPattern, filePattern, file.SequenceNumber, isMultiFile);
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

        private static Dictionary<string, object> BuildNamingVariables(Audiobook audiobook, string? folderPattern, string? filePattern, int sequenceNumber, bool isMultiFile)
        {
            var usesSubtitleToken = (!string.IsNullOrWhiteSpace(folderPattern) && folderPattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(filePattern) && filePattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0);
            var combinedTitle = !usesSubtitleToken
                && !string.IsNullOrWhiteSpace(audiobook.Subtitle)
                && !string.IsNullOrWhiteSpace(audiobook.Title)
                && !audiobook.Title.Contains(audiobook.Subtitle, StringComparison.OrdinalIgnoreCase)
                ? $"{audiobook.Title}: {audiobook.Subtitle}"
                : audiobook.Title;
            var narrator = audiobook.Narrators != null ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n))) : string.Empty;

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "Author", audiobook.Authors?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Unknown Author" },
                { "Series", audiobook.Series ?? string.Empty },
                { "Title", string.IsNullOrWhiteSpace(combinedTitle) ? "Unknown Title" : combinedTitle },
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

        private string ComputeCurrentBasePath(Audiobook audiobook)
        {
            if (!string.IsNullOrWhiteSpace(audiobook.BasePath)) return NormalizePath(audiobook.BasePath);
            var filePaths = audiobook.Files?.Where(f => !string.IsNullOrWhiteSpace(f.Path)).Select(f => f.Path!).ToList() ?? new();
            if (filePaths.Count == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath)) filePaths.Add(audiobook.FilePath);
            return ComputeCommonBasePath(filePaths);
        }

        private string ComputeCommonBasePath(IEnumerable<string> paths)
        {
            var normalized = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizePath).ToList();
            if (normalized.Count == 0) return string.Empty;
            if (normalized.Count == 1)
            {
                var single = normalized[0];
                return _fileSystem.DirectoryExists(single) ? single : NormalizePath(Path.GetDirectoryName(single) ?? single);
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
