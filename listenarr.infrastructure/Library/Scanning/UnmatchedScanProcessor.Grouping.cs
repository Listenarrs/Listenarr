/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class UnmatchedScanProcessor
    {
        internal static List<List<string>> BuildGroupedFilesForFolder(
            IEnumerable<string> files,
            string folderPath,
            FileSystemPathSemantics semantics,
            IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null)
        {
            var allFiles = files
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(semantics.Comparer)
                .ToList();

            if (allFiles.Count <= 1)
            {
                return new List<List<string>> { allFiles };
            }

            if (embeddedTagsByFile != null)
            {
                var metadataAwareGroups = BuildMetadataAwareGroups(
                    allFiles,
                    folderPath,
                    semantics,
                    embeddedTagsByFile);
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
            FileSystemPathSemantics semantics,
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
                semantics.Comparer);

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
                .Select(group => group.Select(candidate => candidate.FilePath).Distinct(semantics.Comparer).ToList())
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
            FileSystemPathSemantics semantics,
            IReadOnlyDictionary<string, string> fileObjectIdentities,
            CancellationToken ct)
        {
            var result = new Dictionary<string, PathParsedMetadata>(semantics.Comparer);
            foreach (var file in files)
            {
                var canonicalFile = FileSystemPathIdentity.Canonicalize(
                    file,
                    semantics.Syntax);
                if (!fileObjectIdentities.TryGetValue(
                        canonicalFile,
                        out var expectedPhysicalObjectIdentity))
                {
                    throw new InvalidOperationException(
                        "The unmatched metadata candidate lacks its enumerated physical generation.");
                }

                using var lease = PinnedAudiobookFileRegistrationLease.Open(
                    file,
                    expectedPhysicalObjectIdentity);
                result[file] = await PathMetadataParser.ReadEmbeddedTagsAsync(
                    lease.MetadataPath,
                    ffprobePath,
                    ct);
                if (!lease.MatchesCurrentPublication())
                {
                    throw new InvalidOperationException(
                        "The unmatched metadata candidate changed during embedded-tag extraction.");
                }
            }

            return result;
        }

        internal static async Task ApplyPinnedFolderMetadataAsync(
            PathParsedMetadata target,
            string bookFolder,
            ScanFileDiscovery.EnumerationResult enumeration,
            FileSystemPathSemantics semantics,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (string.IsNullOrWhiteSpace(bookFolder))
            {
                return;
            }

            var canonicalFolder = FileSystemPathIdentity.Canonicalize(
                bookFolder,
                semantics.Syntax);
            if (!enumeration.DirectoryObjectIdentities.TryGetValue(
                    canonicalFolder,
                    out var expectedDirectoryIdentity))
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder lacks its authoritative enumeration proof.");
            }

            using var folder = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                bookFolder,
                createMissing: false);
            if (!folder.MatchesDirectoryObjectIdentity(expectedDirectoryIdentity))
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder changed after filesystem enumeration.");
            }
            var expectedNamespaceChangeToken = folder.GetNamespaceChangeToken();
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);

            var description = await TryReadPinnedTextFileAsync(
                folder,
                "desc.txt",
                maxCharacters: 2000,
                ct);
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);
            if (!string.IsNullOrWhiteSpace(description))
            {
                target.Description = description.Trim();
            }

            var narrator = await TryReadPinnedTextFileAsync(
                folder,
                "reader.txt",
                maxCharacters: 512,
                ct);
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);
            if (!string.IsNullOrWhiteSpace(narrator))
            {
                target.Narrator = narrator.Trim();
            }

            string[] visibleFiles;
            try
            {
                visibleFiles = Directory.EnumerateFiles(folder.FullPath)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder became unavailable while locating its cover image.",
                    exception);
            }
            EnsurePinnedFolderMatches(
                folder,
                expectedDirectoryIdentity,
                expectedNamespaceChangeToken);

            foreach (var fileName in visibleFiles)
            {
                if (!Path.GetFileNameWithoutExtension(fileName)
                        .Contains("cover", StringComparison.OrdinalIgnoreCase)
                    || !new[] { ".jpg", ".jpeg", ".png", ".webp" }
                        .Contains(
                            Path.GetExtension(fileName),
                            StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var cover = folder.TryOpenExistingFile(
                    fileName,
                    requireDeleteAccess: false);
                if (cover == null || !cover.IsRegularFile() || !cover.VisiblePathMatches())
                {
                    continue;
                }

                EnsurePinnedFolderMatches(
                    folder,
                    expectedDirectoryIdentity,
                    expectedNamespaceChangeToken);
                target.CoverPath = cover.FullPath;
                break;
            }
        }

        private static async Task<string?> TryReadPinnedTextFileAsync(
            PinnedDirectoryCreation.PinnedDirectoryAnchor folder,
            string fileName,
            int maxCharacters,
            CancellationToken ct)
        {
            using var file = folder.TryOpenExistingFile(
                fileName,
                requireDeleteAccess: false);
            if (file == null || !file.IsRegularFile() || !file.VisiblePathMatches())
            {
                return null;
            }

            await using var stream = file.OpenReadStream(
                bufferSize: 4096,
                asynchronous: false);
            using var reader = new StreamReader(
                stream,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            var buffer = new char[maxCharacters + 1];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await reader.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    ct);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }
            if (!file.VisiblePathMatches() || !folder.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The unmatched metadata sidecar changed while it was being read.");
            }

            return new string(buffer, 0, Math.Min(totalRead, maxCharacters));
        }

        private static void EnsurePinnedFolderMatches(
            PinnedDirectoryCreation.PinnedDirectoryAnchor folder,
            string expectedDirectoryIdentity,
            string expectedNamespaceChangeToken)
        {
            if (!folder.VisiblePathMatches()
                || !folder.MatchesDirectoryObjectIdentity(expectedDirectoryIdentity)
                || !string.Equals(
                    folder.GetNamespaceChangeToken(),
                    expectedNamespaceChangeToken,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder changed after filesystem enumeration.");
            }
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

        /// <summary>
        /// Extracts a normalized title stem from a filename for grouping purposes.
        /// Strips a trailing "N of M" index, leading track numbers, trailing
        /// Part/CD/Disc numbers, year and series decorations in brackets. Files that
        /// resolve to the same stem are treated as parts of the same audiobook. Returns
        /// the folder name as fallback when the stem would otherwise be empty (for
        /// example purely numeric filenames).
        /// </summary>
        private static string ExtractTitleStem(string filePath, string folderPath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);

            // Strip a trailing "N of M" index: "Title 001 of 498", "Title - 3 of 12".
            //
            // This runs BEFORE the leading-track strip on purpose. A file named "001 of 498"
            // with no title in it would otherwise lose its leading "001 " first, leaving
            // "of 498" — a different stem in every file, and no longer numeric, so the
            // folder-name fallback at the end never gets the chance to group them.
            //
            // Unlike a bare "(N)", this form is not ambiguous with a series marker: it
            // carries its own total, so it says the file is one part of a set rather than
            // one entry among separate works. Nothing is titled "Book 1 of 12".
            name = Regex.Replace(name, @"[\s\-_]*\d+\s*of\s*\d+$", "", RegexOptions.IgnoreCase);
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

        private static string NormalizePath(string path, FileSystemPathSyntax syntax) =>
            FileSystemPathIdentity.Canonicalize(Path.GetFullPath(path), syntax);
    }
}
