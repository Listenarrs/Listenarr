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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    public class DownloadImportService(
        IFileNamingService fileNamingService,
        IMetadataService metadataService,
        IFileMover fileMover,
        IAudiobookFileService audiobookFileService,
        IArchiveExtractor archiveExtractor,
        IConfigurationService configurationService,
        ILogger<DownloadImportService> logger) : IDownloadImportService
    {
        private List<TempDirectory> archiveDirectories = [];

        public async Task<List<ImportResult>> ImportDownloadFilesAsync(Audiobook audiobook, List<string> files, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(audiobook.BasePath))
            {
                throw new InvalidOperationException($"Audiobook {audiobook.Id} basePath cannot be empty or null");
            }

            var settings = await configurationService.GetApplicationSettingsAsync();

            try
            {
                var completedFileAction = settings.CompletedFileAction;

                // Extract archives if any
                if (settings.ExtractArchives)
                {
                    var archives = files
                        .Where(archiveExtractor.IsArchive)
                        .Where(file => !FileUtils.IsBlacklistedFile(file, settings.ImportBlacklistExtensions))
                        .ToList();

                    // Remove archives from the files to import
                    files = [.. files.Where(file => !archives.Contains(file))];

                    files.AddRange(await ExtractArchives(archives));

                    // We cannot hardlink to temporary files
                    if (archives.Count > 0 && completedFileAction == FileAction.HardlinkCopy)
                    {
                        completedFileAction = FileAction.Copy;
                        logger.LogWarning($"Audiobook {audiobook.Id} contains archives thus Hard link mode is impossible: Completed action switched to copy");
                    }
                }

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
                var isMultiFileBatch = plannedAudioFiles.Count > 1;
                var sourceRootPath = FileUtils.GetCommonDirectory(sourceFiles);

                // Order audio files before companion files
                var orderedFiles = plannedAudioFiles.Select(p => p.FullPath)
                    .Concat(sourceFiles.Where(f => !planByPath.ContainsKey(f)))
                    .ToList();

                try
                {
                    // Precompute audiobook and best existing quality to avoid import-order races
                    string? bestExisting = null;
                    QualityProfile? abProfile = null;

                    abProfile = audiobook.QualityProfile;

                    if (audiobook.Files != null && audiobook.Files.Count != 0)
                    {
                        foreach (var f in audiobook.Files)
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
                    }

                    foreach (var file in orderedFiles)
                    {
                        if (!FileUtils.IsAudioFile(file))
                        {
                            var hasSuccessfulAudioImport = results.Any(r =>
                                r.Success
                                && !string.IsNullOrWhiteSpace(r.FinalPath)
                                && !string.IsNullOrWhiteSpace(r.SourcePath)
                                && FileUtils.IsAudioFile(r.SourcePath!));

                            if (!hasSuccessfulAudioImport || string.IsNullOrWhiteSpace(audiobook.BasePath))
                            {
                                results.Add(ImportResult.Skipped("No successful audio import in batch"));
                                logger.LogDebug("ImportFilesFromDirectory: Skipping companion file {File} because no successful audio import was recorded for the batch", file);
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

                                var destination = CombineWithOptionalBase(audiobook.BasePath, relativePath);

                                if (!await fileMover.PerformActionOn(completedFileAction, file, destination))
                                {
                                    results.Add(ImportResult.ImportFailure(completedFileAction, file, destination));
                                    continue;
                                }
                                results.Add(ImportResult.ImportSuccess(completedFileAction, file, destination));
                            }
                            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                            {
                                results.Add(ImportResult.Exception(exception, file));
                                logger.LogWarning(exception, $"Failed companion-file import {file}");
                            }

                            continue;
                        }

                        try
                        {
                            planByPath.TryGetValue(file, out var plan);
                            diskNumbersForNaming.TryGetValue(file, out var namingDiskNumber);
                            chapterNumbersForNaming.TryGetValue(file, out var namingChapterNumber);

                            AudioMetadata? candidateMetadata = null;
                            if (settings.EnableMetadataProcessing)
                            {
                                candidateMetadata = await metadataService.ExtractFileMetadataAsync(file);
                            }

                            var candidateQuality = DetermineQualityFromMetadata(candidateMetadata, file);

                            try
                            {
                                if (audiobook.Files != null && audiobook.Files.Count != 0 && !IsQualityBetter(candidateQuality, bestExisting, abProfile))
                                {
                                    results.Add(ImportResult.Skipped($"candidate quality '{candidateQuality}' is not better than existing '{bestExisting}'"));
                                    logger.LogInformation($"Skipping import of file {file} for audiobook {audiobook.Id} because candidate quality '{candidateQuality}' is not better than existing '{bestExisting}'");
                                    continue;
                                }
                            }
                            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                            {
                                logger.LogDebug(exception, $"ImportFilesFromDirectory: Failed to evaluate quality for multi-file import {file}");
                            }

                            // Determine destination directory (prefer audiobook basepath)
                            string destDirForFile = audiobook.BasePath;

                            // Build naming metadata: prefer audiobook metadata when available, otherwise use extracted candidate metadata
                            var namingMetadata = BuildNamingMetadata(audiobook, candidateMetadata, Path.GetFileNameWithoutExtension(file));
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

                            var folderRelative = fileNamingService.ApplyNamingPattern(folderPattern, variablesForFile, treatAsFilename: false);
                            if (string.IsNullOrEmpty(audiobook.BasePath) && !string.IsNullOrWhiteSpace(folderRelative))
                            {
                                destDirForFile = CombineWithOptionalBase(destDirForFile, folderRelative);
                            }

                            var baseFilePattern = isMultiFileBatch ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

                            var ext = Path.GetExtension(file);
                            var patternHasNumberTokens = !string.IsNullOrWhiteSpace(baseFilePattern)
                                && (baseFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                                    || baseFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

                            var patternAllowsSubfolders = baseFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                                || baseFilePattern.Contains("ChapterNumber", StringComparison.OrdinalIgnoreCase)
                                || baseFilePattern.Contains('/')
                                || baseFilePattern.Contains('\\');
                            var treatAsFilename = !patternAllowsSubfolders;

                            var filename = fileNamingService.ApplyNamingPattern(baseFilePattern, variablesForFile, treatAsFilename);
                            if (!filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) filename += ext; // FIXME: Should be in ApplyNamingPattern

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
                                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                                {
                                    filename = Path.GetFileName(filename);
                                }
                            }

                            var destination = CombineWithOptionalBase(destDirForFile, filename);

                            if (!await fileMover.PerformActionOn(completedFileAction, file, destination))
                            {
                                results.Add(ImportResult.ImportFailure(completedFileAction, file, destination));
                                continue;
                            }

                            // Register audiobook file
                            var wasRegisteredToAudiobook = false;
                            try
                            {
                                // Always store absolute path for downloads - metadata extraction needs full path
                                wasRegisteredToAudiobook = await audiobookFileService.EnsureAudiobookFileAsync(audiobook, destination, "download");
                            }
                            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                            {
                                logger.LogWarning(exception, $"ImportFilesFromDirectory: Failed to create AudiobookFile for imported file {file}");
                            }

                            results.Add(ImportResult.ImportSuccess(completedFileAction, file, destination, wasRegisteredToAudiobook));
                        }
                        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                        {
                            results.Add(ImportResult.Exception(exception, file));
                            logger.LogWarning(exception, $"ImportFilesFromDirectory: Failed processing file in directory import: {file}");
                        }
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogWarning(exception, $"Failed to import files for audiobook {audiobook.Id}");
                }

                return results;
            }
            finally
            {
                await DisposeOfExtractedFiles();
            }
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
                if (metadata.BitRate.HasValue) return (metadata.BitRate.Value / 1000) + "kbps";
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

        /// <summary>
        /// Given a list of archives, extracts the files and give a list of extracted files
        /// </summary>
        /// <param name="archives">List of archives to extract</param>
        /// <returns>List of all files from all extracted archives</returns>
        /// <exception cref="IOException">Thrown if we are unable to process one archive</exception>
        private async Task<List<string>> ExtractArchives(List<string> archives)
        {
            List<string> files = [];

            foreach (var archive in archives)
            {
                try
                {
                    var archiveDirectory = await archiveExtractor.ExtractArchiveToTempDirAsync(archive);
                    if (archiveDirectory != null)
                    {
                        // Store the disposable directory
                        archiveDirectories.Add(archiveDirectory);

                        var tempDirExtracted = archiveDirectory.Path;
                        var extractedFiles = Directory.GetFiles(tempDirExtracted, "*", SearchOption.AllDirectories).ToArray();
                        if (extractedFiles != null)
                        {
                            files.AddRange([.. extractedFiles.Select(file => FileUtils.NormalizeStoredPath(file))]);
                        }
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    throw new IOException($"Unable to extract {archive}");
                }
            }

            return files;
        }

        /// <summary>
        /// Removes temporary files created while extracting files
        /// </summary>
        private async Task DisposeOfExtractedFiles()
        {
            foreach (var directory in archiveDirectories)
            {
                directory.Dispose();
            }
        }
    }
}
