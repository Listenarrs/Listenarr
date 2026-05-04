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
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Application.Notification;

namespace Listenarr.Application.Audiobooks
{
    public class UnmatchedScanBackgroundService : BackgroundService
    {
        private static readonly string[] AudioExtensions = { ".m4b", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav" };
        private sealed record StemGroup(string Stem, List<string> Files);
        private sealed record GroupCandidate(string FilePath, string Stem, bool IsAncillary, string TitleKey, string AuthorKey);

        private readonly IUnmatchedScanQueueService _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<UnmatchedScanBackgroundService> _logger;
        private readonly IHubContext<SettingsHub> _hubContext;
        private readonly IFfmpegService _ffmpegService;

        public UnmatchedScanBackgroundService(
            IUnmatchedScanQueueService queue,
            IServiceScopeFactory scopeFactory,
            ILogger<UnmatchedScanBackgroundService> logger,
            IHubContext<SettingsHub> hubContext,
            IFfmpegService ffmpegService)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hubContext = hubContext;
            _ffmpegService = ffmpegService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("UnmatchedScanBackgroundService started");
            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        _logger.LogInformation("Processing unmatched scan job {JobId} for {Path}", job.Id, job.RootFolderPath);
                        _queue.UpdateJob(job.Id, "Processing");

                        var results = await ScanAsync(job.RootFolderPath, stoppingToken);

                        _queue.UpdateJob(job.Id, "Completed", results);
                        _logger.LogInformation("Unmatched scan job {JobId} completed: {Count} unmatched items", job.Id, results.Count);

                        await _hubContext.Clients.All.SendAsync(
                            "UnmatchedScanComplete",
                            new { jobId = job.Id.ToString(), count = results.Count },
                            stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (IOException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (ArgumentException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (RegexMatchTimeoutException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (DbUpdateException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (HubException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Unexpected unmatched scan job {JobId} failure", job.Id);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("UnmatchedScanBackgroundService stopping due to host shutdown");
            }
        }

        private async Task HandleJobFailureAsync(Guid jobId, Exception ex, CancellationToken stoppingToken)
        {
            _logger.LogError(ex, "Unmatched scan job {JobId} failed", jobId);
            _queue.UpdateJob(jobId, "Failed", error: ex.Message);

            await _hubContext.Clients.All.SendAsync(
                "UnmatchedScanComplete",
                new { jobId = jobId.ToString(), count = 0, error = ex.Message },
                stoppingToken);
        }

        private async Task<List<UnmatchedFileResult>> ScanAsync(string rootFolderPath, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var appSettings = await configService.GetApplicationSettingsAsync();
            var concurrency = Math.Clamp(appSettings?.UnmatchedScanConcurrency ?? 2, 1, 8);

            // Load all tracked file paths (normalized) from DB.
            // Check BOTH AudiobookFiles (multi-file imports) AND Audiobook.FilePath (single-file imports)
            // so that files already in the library are not reported as unmatched.
            var trackedFromFiles = await fileRepository.GetAllFilePathsAsync(ct);

            var allAudiobooks = await audiobookRepository.GetAllAsync();
            var trackedFromAudiobooks = allAudiobooks
                .Where(a => a.FilePath != null)
                .Select(a => a.FilePath!)
                .ToList();

            var trackedNormalized = new HashSet<string>(
                trackedFromFiles.Concat(trackedFromAudiobooks).Select(NormalizePath),
                StringComparer.OrdinalIgnoreCase);

            // Walk the root folder tree
            var candidates = CollectAudioFiles(rootFolderPath);

            // Filter to untracked files
            var unmatched = candidates
                .Where(f => !trackedNormalized.Contains(NormalizePath(f)))
                .ToList();

            // Two-level grouping:
            // 1. Group by parent directory (folder = one audiobook in the common case).
            // 2. Within each directory that has multiple files, sub-group by a normalized
            //    title stem extracted from the filename. Files that share the same stem
            //    are parts of the same audiobook. Distinct stems remain separate entries,
            //    except for ancillary tracks like "Foreword" or "Introduction", which stay
            //    attached when there is only one primary book group in the folder.
            var folderGroups = unmatched
                .GroupBy(f => Path.GetFullPath(Path.GetDirectoryName(f) ?? rootFolderPath))
                .ToList();

            // Resolve ffprobe path once for the whole scan (null = not available)
            var ffprobePath = await _ffmpegService.GetFfprobePathAsync();

            var results = new System.Collections.Concurrent.ConcurrentBag<UnmatchedFileResult>();

            // Parallel.ForEachAsync only allocates active slots and avoids creating all tasks up front.
            await Parallel.ForEachAsync(folderGroups,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (folderGroup, token) =>
                {
                    var bookFolder = folderGroup.Key;
                    var folderFiles = folderGroup.ToList();
                    IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null;

                    var groupedFiles = BuildGroupedFilesForFolder(folderFiles, bookFolder);
                    if (groupedFiles.Count > 1 && !string.IsNullOrEmpty(ffprobePath))
                    {
                        embeddedTagsByFile = await ReadEmbeddedTagsForFilesAsync(folderFiles, ffprobePath, token);
                        groupedFiles = BuildGroupedFilesForFolder(folderFiles, bookFolder, embeddedTagsByFile);
                    }

                    foreach (var files in groupedFiles)
                    {
                        var plans = MultiFileImportPlanner.BuildPlans(
                            files.Select(f => (FullPath: f, RelativePath: (string?)Path.GetRelativePath(bookFolder, f))));
                        var orderedFiles = plans.Select(p => p.FullPath).ToList();
                        var representative = orderedFiles.First();
                        var parsed = PathMetadataParser.Parse(representative, rootFolderPath);

                        PathParsedMetadata? tags = null;
                        if (embeddedTagsByFile != null && embeddedTagsByFile.TryGetValue(representative, out var cachedTags))
                        {
                            tags = cachedTags;
                        }
                        else if (!string.IsNullOrEmpty(ffprobePath))
                        {
                            tags = await PathMetadataParser.ReadEmbeddedTagsAsync(representative, ffprobePath, token);
                        }

                        if (tags != null)
                        {
                            ApplyEmbeddedTags(parsed, tags);
                        }

                        var relativeFolder = bookFolder.Length > rootFolderPath.Length
                            ? bookFolder[(rootFolderPath.Length)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            : bookFolder;

                        var totalSize = files.Sum(f =>
                        {
                            try { return new FileInfo(f).Length; } catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { return 0L; }
                        });

                        results.Add(new UnmatchedFileResult
                        {
                            FullPath = representative,
                            SourceFiles = orderedFiles,
                            RelativePath = relativeFolder,
                            BookFolder = bookFolder,
                            Size = totalSize,
                            FileCount = orderedFiles.Count,
                            Title = parsed.Title,
                            Author = parsed.Author,
                            Series = parsed.Series,
                            SeriesNumber = parsed.SeriesNumber,
                            Year = parsed.Year,
                            Narrator = parsed.Narrator,
                            Description = parsed.Description,
                            CoverPath = parsed.CoverPath,
                            Asin = parsed.Asin,
                            Format = Path.GetExtension(representative).TrimStart('.').ToUpperInvariant()
                        });
                    }
                });

            return results.OrderBy(r => r.Author).ThenBy(r => r.Series).ThenBy(r => r.Title).ToList();
        }

        internal static List<List<string>> BuildGroupedFilesForFolder(
            IEnumerable<string> files,
            string folderPath,
            IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null)
        {
            var allFiles = files
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allFiles.Count <= 1)
            {
                return new List<List<string>> { allFiles };
            }

            if (embeddedTagsByFile != null)
            {
                var metadataAwareGroups = BuildMetadataAwareGroups(allFiles, folderPath, embeddedTagsByFile);
                if (metadataAwareGroups.Count > 0)
                {
                    return metadataAwareGroups;
                }
            }

            return BuildStemGroups(allFiles, folderPath);
        }

        private static List<List<string>> BuildMetadataAwareGroups(
            IReadOnlyCollection<string> files,
            string folderPath,
            IReadOnlyDictionary<string, PathParsedMetadata> embeddedTagsByFile)
        {
            var folderKey = NormalizeGroupKey(Path.GetFileName(folderPath));
            var candidates = files
                .Select(file => CreateGroupCandidate(file, folderPath, embeddedTagsByFile))
                .ToList();

            if (!candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate.TitleKey)))
            {
                return new List<List<string>>();
            }

            var metadataGroups = new List<List<GroupCandidate>>();
            foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate.TitleKey)))
            {
                var matchingGroup = metadataGroups.FirstOrDefault(group => group.Any(existing => TitlesAndAuthorsMatch(existing, candidate)));
                if (matchingGroup != null)
                {
                    matchingGroup.Add(candidate);
                }
                else
                {
                    metadataGroups.Add(new List<GroupCandidate> { candidate });
                }
            }

            if (metadataGroups.Count == 0)
            {
                return new List<List<string>>();
            }

            var attachedFiles = new HashSet<string>(
                metadataGroups.SelectMany(group => group).Select(candidate => candidate.FilePath),
                StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates.Where(candidate => !attachedFiles.Contains(candidate.FilePath)))
            {
                var compatibleGroups = metadataGroups
                    .Where(group => group.Any(existing => AuthorsCompatible(existing.AuthorKey, candidate.AuthorKey)))
                    .ToList();

                if (compatibleGroups.Count == 1
                    && (candidate.IsAncillary
                        || string.IsNullOrWhiteSpace(candidate.Stem)
                        || string.Equals(candidate.Stem, folderKey, StringComparison.OrdinalIgnoreCase)))
                {
                    compatibleGroups[0].Add(candidate);
                    attachedFiles.Add(candidate.FilePath);
                }
            }

            var leftovers = candidates
                .Where(candidate => !attachedFiles.Contains(candidate.FilePath))
                .Select(candidate => candidate.FilePath)
                .ToList();

            var grouped = metadataGroups
                .Select(group => group.Select(candidate => candidate.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                .ToList();

            if (leftovers.Count > 0)
            {
                grouped.AddRange(BuildStemGroups(leftovers, folderPath));
            }

            return grouped;
        }

        private static List<List<string>> BuildStemGroups(IReadOnlyCollection<string> allFiles, string folderPath)
        {
            var byTitle = allFiles
                .GroupBy(f => ExtractTitleStem(f, folderPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => new StemGroup(g.Key, g.ToList()))
                .ToList();

            if (byTitle.Count <= 1)
            {
                return new List<List<string>> { allFiles.ToList() };
            }

            var primaryGroups = byTitle
                .Where(g => !IsAncillaryStem(g.Stem))
                .ToList();

            if (primaryGroups.Count == 1)
            {
                return new List<List<string>>
                {
                    byTitle.SelectMany(g => g.Files).ToList()
                };
            }

            return byTitle.Select(g => g.Files).ToList();
        }

        private static GroupCandidate CreateGroupCandidate(
            string filePath,
            string folderPath,
            IReadOnlyDictionary<string, PathParsedMetadata> embeddedTagsByFile)
        {
            embeddedTagsByFile.TryGetValue(filePath, out var tags);
            var stem = ExtractTitleStem(filePath, folderPath);
            return new GroupCandidate(
                filePath,
                stem,
                IsAncillaryStem(stem),
                FileUtils.NormalizeComparisonValue(tags?.Title),
                FileUtils.NormalizeComparisonValue(tags?.Author));
        }

        private static bool TitlesAndAuthorsMatch(GroupCandidate left, GroupCandidate right)
        {
            if (!FileUtils.ValuesOverlap(left.TitleKey, right.TitleKey))
            {
                return false;
            }

            return AuthorsCompatible(left.AuthorKey, right.AuthorKey);
        }

        private static bool AuthorsCompatible(string? leftAuthor, string? rightAuthor)
        {
            if (string.IsNullOrWhiteSpace(leftAuthor) || string.IsNullOrWhiteSpace(rightAuthor))
            {
                return true;
            }

            return FileUtils.ValuesOverlap(leftAuthor, rightAuthor);
        }

        private static async Task<IReadOnlyDictionary<string, PathParsedMetadata>> ReadEmbeddedTagsForFilesAsync(
            IEnumerable<string> files,
            string ffprobePath,
            CancellationToken ct)
        {
            var result = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                result[file] = await PathMetadataParser.ReadEmbeddedTagsAsync(file, ffprobePath, ct);
            }

            return result;
        }

        private static void ApplyEmbeddedTags(PathParsedMetadata target, PathParsedMetadata tags)
        {
            if (!string.IsNullOrEmpty(tags.Title)) target.Title = tags.Title;
            if (!string.IsNullOrEmpty(tags.Author)) target.Author = tags.Author;
            if (!string.IsNullOrEmpty(tags.Narrator)) target.Narrator = tags.Narrator;
            if (!string.IsNullOrEmpty(tags.Series)) target.Series = tags.Series;
            if (!string.IsNullOrEmpty(tags.SeriesNumber)) target.SeriesNumber = tags.SeriesNumber;
            if (!string.IsNullOrEmpty(tags.Year)) target.Year = tags.Year;
            if (!string.IsNullOrEmpty(tags.Description)) target.Description = tags.Description;
            if (!string.IsNullOrEmpty(tags.Asin)) target.Asin = tags.Asin;
        }

        private List<string> CollectAudioFiles(string rootFolderPath)
        {
            var candidates = new List<string>();
            var normalizedRoot = Path.GetFullPath(rootFolderPath);
            var dirs = new Stack<string>();
            dirs.Push(normalizedRoot);

            while (dirs.Count > 0)
            {
                var dir = dirs.Pop();
                try
                {
                    var normalizedDir = Path.GetFullPath(dir);
                    foreach (var file in Directory.EnumerateFiles(normalizedDir))
                    {
                        try
                        {
                            if (AudioExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                                candidates.Add(file);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Skipped file {File} during unmatched scan", file);
                        }
                    }
                    foreach (var sub in Directory.EnumerateDirectories(normalizedDir))
                    {
                        // Skip reparse points (symlinks, junctions) because they can point outside the root.
                        if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            _logger.LogDebug("Skipping reparse point {Dir}", sub);
                            continue;
                        }
                        var resolvedSub = Path.GetFullPath(sub);
                        var rootWithSep = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!resolvedSub.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("Skipping {Dir}: resolves outside configured root {Root}", sub, normalizedRoot);
                            continue;
                        }
                        dirs.Push(resolvedSub);
                    }
                }
                catch (IOException ioEx) { _logger.LogWarning(ioEx, "IO error scanning {Dir}", dir); }
                catch (UnauthorizedAccessException uaEx) { _logger.LogWarning(uaEx, "Access denied scanning {Dir}", dir); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Unexpected error scanning {Dir}", dir);
                }
            }

            return candidates;
        }

        /// <summary>
        /// Extracts a normalized title stem from a filename for grouping purposes.
        /// Strips leading track numbers, trailing Part/CD/Disc numbers, year and
        /// series decorations in brackets. Files that resolve to the same stem are
        /// treated as parts of the same audiobook. Returns the folder name as fallback
        /// when the stem would otherwise be empty (for example purely numeric filenames).
        /// </summary>
        private static string ExtractTitleStem(string filePath, string folderPath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);

            // Strip leading track/disc number prefix: "01 - ", "Track 01 - ", "1. "
            name = Regex.Replace(name, @"^(track\s*)?\d+[\s\-_\.]+", "", RegexOptions.IgnoreCase);
            // Strip trailing Part/CD/Disc/Chapter number: "- Part 1", "CD2", "Disc 2", "pt00"
            name = Regex.Replace(name, @"[\s\-_]*(part|cd|disc|chapter|pt)\s*\d+$", "", RegexOptions.IgnoreCase);
            // Strip 4-digit years in parens: (2020), (2021)
            name = Regex.Replace(name, @"\s*\(\d{4}\)", "");
            // Strip square bracket content: [Series 3], [Chaos Seeds 1]
            name = Regex.Replace(name, @"\s*\[.*?\]", "");
            // Plain numeric parens like (1), (2) are intentionally kept because they
            // distinguish separate books in a series.
            name = NormalizeGroupKey(name);

            // Purely numeric or empty after stripping: use the folder name so numbered-part
            // files like 1.m4b and 2.m4b still group together under their folder.
            if (string.IsNullOrEmpty(name) || Regex.IsMatch(name, @"^\d+$"))
                return NormalizeGroupKey(Path.GetFileName(folderPath));

            return name;
        }

        private static bool IsAncillaryStem(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem))
            {
                return false;
            }

            var normalized = stem.Trim();
            normalized = Regex.Replace(normalized, @"^[\s\(\[\{""'`_-]+|[\s\)\]\}""'`_-]+$", "");
            normalized = NormalizeGroupKey(normalized);

            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            return Regex.IsMatch(
                    normalized,
                    @"^(?:foreword|afterword|preface|prologue|epilogue|appendix|dedication|interlude)(?:\b.*)?$",
                    RegexOptions.IgnoreCase)
                || Regex.IsMatch(
                    normalized,
                    @"^(?:introduction|intro)(?:$|\s+by\b.*)",
                    RegexOptions.IgnoreCase)
                || Regex.IsMatch(
                    normalized,
                    @"^(?:acknowledg(?:e)?ments?|credits|opening credits|closing credits|author'?s note|note from the author|about the author|bonus chapter|bonus material|preview|sample)(?:\b.*)?$",
                    RegexOptions.IgnoreCase);
        }

        private static string NormalizeGroupKey(string value) =>
            Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
    }
}
