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

using Listenarr.Application.Audiobooks.Organizing;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryOrganizeSweepWorkflow
    {
        /// <summary>
        /// Collapse a single "nested one level too deep" row (organize-preview
        /// reason code <c>target_ancestor</c>) into its canonical parent folder.
        /// These rows — a historical import artifact where the files sit in a
        /// redundant subfolder beneath their canonical folder — can't go through
        /// the normal move queue because the target is an ancestor of the source,
        /// which the queue refuses. The flatten is strongly guarded (see
        /// <see cref="OrganizeFilesystem.ExecuteFlatten"/>): it only collapses
        /// empty wrapper directories and never touches a target that holds
        /// foreign files. On success the audiobook's BasePath and file paths are
        /// rebased in place so the DB matches the new on-disk layout.
        /// </summary>
        public async Task<IActionResult> FlattenAsync(OrganizeFlattenRequest? request, CancellationToken ct)
        {
            if (request == null || request.AudiobookId <= 0)
            {
                return new BadRequestObjectResult(new OrganizeFlattenResultDto { Success = false, Error = "No audiobook id provided" });
            }

            var audiobooks = await _repo.GetByIdsWithFilesAsync(new List<int> { request.AudiobookId }, ct);
            var audiobook = audiobooks.FirstOrDefault();
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new OrganizeFlattenResultDto { Success = false, Error = $"Audiobook {request.AudiobookId} not found" });
            }

            var settings = await _configurationService.GetApplicationSettingsAsync();
            var rootFolders = await _rootFolderService.GetAllAsync();

            var currentPath = NormalizeOrganizePath(audiobook.BasePath);
            if (string.IsNullOrEmpty(currentPath))
            {
                return new BadRequestObjectResult(new OrganizeFlattenResultDto { Success = false, Error = "Audiobook has no current folder path." });
            }

            var (target, invalidReason, _) = ComputeOrganizeTarget(audiobook, settings, rootFolders);
            if (!string.IsNullOrEmpty(invalidReason))
            {
                return new BadRequestObjectResult(new OrganizeFlattenResultDto { Success = false, Error = invalidReason });
            }

            // Only the ancestor/nested case is a flatten. Re-validate against the
            // live state rather than trusting the caller's snapshot.
            if (!IsTargetAncestorOfSource(currentPath, target))
            {
                return new BadRequestObjectResult(new OrganizeFlattenResultDto
                {
                    Success = false,
                    Error = "This row is not a 'nested one level too deep' case; flatten only applies to those.",
                });
            }

            var outcome = _organizeFilesystem.ExecuteFlatten(currentPath, target, Guid.NewGuid());
            if (!outcome.Success)
            {
                _logger.LogWarning("Organize flatten for audiobook {Id} ({Source} -> {Target}) refused: {Reason}",
                    audiobook.Id, LogRedaction.SanitizeFilePath(currentPath), LogRedaction.SanitizeFilePath(target), outcome.ErrorMessage);
                return new BadRequestObjectResult(new OrganizeFlattenResultDto { Success = false, Error = outcome.ErrorMessage });
            }

            // Files are on disk at the canonical path now — rebase the DB record
            // so a re-scan isn't required.
            var newBase = NormalizeOrganizePath(target);
            audiobook.BasePath = newBase;
            if (audiobook.Files != null)
            {
                foreach (var file in audiobook.Files.Where(f => !string.IsNullOrWhiteSpace(f.Path)))
                {
                    var fp = NormalizeOrganizePath(file.Path);
                    if (string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase) || FileUtils.IsPathInsideOf(fp, currentPath))
                    {
                        var rel = Path.GetRelativePath(currentPath, fp);
                        file.Path = NormalizeOrganizePath(Path.Combine(newBase, rel));
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var fp = NormalizeOrganizePath(audiobook.FilePath);
                if (string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase) || FileUtils.IsPathInsideOf(fp, currentPath))
                {
                    var rel = Path.GetRelativePath(currentPath, fp);
                    audiobook.FilePath = NormalizeOrganizePath(Path.Combine(newBase, rel));
                }
            }

            try
            {
                await _repo.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex,
                    "Flatten moved audiobook {Id} files to {Target} on disk but persisting the new paths failed; a re-scan will recover.",
                    audiobook.Id, LogRedaction.SanitizeFilePath(newBase));
                return new OkObjectResult(new OrganizeFlattenResultDto
                {
                    Success = true,
                    FilesMoved = outcome.FilesMoved,
                    NewPath = newBase,
                    Error = "Files were flattened on disk but the database update failed — re-scan the library to reconcile.",
                });
            }

            try
            {
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title,
                    EventType = "Organized",
                    Message = $"Flattened nested folder into canonical path ({outcome.FilesMoved} file(s))",
                    Source = "Organize",
                    Timestamp = DateTime.UtcNow,
                    NotificationSent = false,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Non-fatal: failed to add history entry after flatten for audiobook {Id}", audiobook.Id);
            }

            _logger.LogInformation("Flattened audiobook {Id} from {Source} into {Target} ({Files} files)",
                audiobook.Id, LogRedaction.SanitizeFilePath(currentPath), LogRedaction.SanitizeFilePath(newBase), outcome.FilesMoved);
            return new OkObjectResult(new OrganizeFlattenResultDto { Success = true, FilesMoved = outcome.FilesMoved, NewPath = newBase });
        }

        /// <summary>
        /// Build the canonical target folder path for <paramref name="audiobook"/>.
        /// Delegates the pattern rendering to <see cref="LibraryPathPlanner"/> —
        /// the same planner the add and move flows use — so the organize sweep
        /// can never disagree with the paths the rest of the app computes.
        /// Returns the <c>(target, invalidReason, invalidReasonCode)</c> triple;
        /// a non-empty reason buckets the row as
        /// <see cref="OrganizePreviewStatus.InvalidTarget"/>.
        /// </summary>
        private (string Target, string? InvalidReason, string? InvalidReasonCode) ComputeOrganizeTarget(
            Audiobook audiobook,
            ApplicationSettings settings,
            List<RootFolder> rootFolders)
        {
            var firstAuthor = audiobook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            if (string.IsNullOrWhiteSpace(audiobook.Title))
            {
                return (string.Empty, "Missing title", OrganizeInvalidReasonCode.MissingTitle);
            }
            if (string.IsNullOrWhiteSpace(firstAuthor))
            {
                return (string.Empty, "Missing author", OrganizeInvalidReasonCode.MissingAuthor);
            }
            if (string.IsNullOrWhiteSpace(settings?.FolderNamingPattern))
            {
                return (string.Empty, "FolderNamingPattern is not configured", OrganizeInvalidReasonCode.PatternNotConfigured);
            }

            var root = ResolveOrganizeRoot(audiobook.BasePath, settings, rootFolders);
            if (string.IsNullOrEmpty(root))
            {
                return (string.Empty, "Audiobook is outside any configured library root", OrganizeInvalidReasonCode.OutsideRoot);
            }

            var combined = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(
                audiobook, root, settings.FolderNamingPattern, _fileNamingService);
            if (string.IsNullOrWhiteSpace(combined))
            {
                return (string.Empty, "Naming pattern produced an empty path", OrganizeInvalidReasonCode.EmptyPattern);
            }

            return (NormalizeOrganizePath(combined), null, null);
        }

        /// <summary>
        /// Pick the canonical root for <paramref name="basePath"/>: the
        /// longest configured root folder (or <c>OutputPath</c>) that contains
        /// the current base path. Falls back to the default root, then to
        /// <c>settings.OutputPath</c>, when the audiobook has no base path
        /// yet. Returns empty when the audiobook lives outside every root.
        /// </summary>
        private static string ResolveOrganizeRoot(
            string? basePath,
            ApplicationSettings settings,
            List<RootFolder> rootFolders)
        {
            var configuredRoots = rootFolders
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => NormalizeOrganizePath(r.Path!))
                .ToList();
            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                var outputNormalized = NormalizeOrganizePath(settings.OutputPath);
                if (!configuredRoots.Any(r => string.Equals(r, outputNormalized, StringComparison.OrdinalIgnoreCase)))
                {
                    configuredRoots.Add(outputNormalized);
                }
            }

            if (string.IsNullOrWhiteSpace(basePath))
            {
                var defaultRoot = rootFolders.FirstOrDefault(r => r.IsDefault)?.Path;
                if (!string.IsNullOrWhiteSpace(defaultRoot)) return NormalizeOrganizePath(defaultRoot);
                if (!string.IsNullOrWhiteSpace(settings.OutputPath)) return NormalizeOrganizePath(settings.OutputPath);
                return configuredRoots.FirstOrDefault() ?? string.Empty;
            }

            var current = NormalizeOrganizePath(basePath);
            return configuredRoots
                .Where(r => string.Equals(r, current, StringComparison.OrdinalIgnoreCase) || FileUtils.IsPathInsideOf(current, r))
                .OrderByDescending(r => r.Length)
                .FirstOrDefault() ?? string.Empty;
        }

        private static string NormalizeOrganizePath(string? path)
            => string.IsNullOrWhiteSpace(path) ? string.Empty : FileUtils.NormalizeStoredPath(path);

        private static string NormalizeOrganizeKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var trimmed = path.TrimEnd('/', '\\');
            return trimmed.ToUpperInvariant();
        }

        /// <summary>
        /// Returns true if the audiobook's current path matches a configured
        /// root folder exactly. Rows in this state can't be moved by the
        /// organize-library flow: the "source" would be the library root
        /// itself, and the post-copy delete of the source would wipe every
        /// other audiobook on the same root.
        /// </summary>
        private static bool IsSourceAtRootFolder(string? currentPath, IEnumerable<RootFolder> rootFolders)
        {
            if (string.IsNullOrWhiteSpace(currentPath)) return false;
            var currentKey = NormalizeOrganizeKey(NormalizeOrganizePath(currentPath));
            if (string.IsNullOrEmpty(currentKey)) return false;
            foreach (var rf in rootFolders)
            {
                if (string.IsNullOrWhiteSpace(rf?.Path)) continue;
                var rootKey = NormalizeOrganizeKey(NormalizeOrganizePath(rf.Path));
                if (currentKey == rootKey) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when <paramref name="currentPath"/> and <paramref name="target"/>
        /// normalize to the same key — the canonical-equality case used by the
        /// already_canonical bucket. Bucketing the "target on disk exists with
        /// content" case would otherwise misclassify these rows: the
        /// audiobook's own files trivially make the target non-empty.
        /// </summary>
        private static bool IsCurrentPathEqualToTarget(string? currentPath, string target)
        {
            var currentKey = NormalizeOrganizeKey(NormalizeOrganizePath(currentPath));
            var targetKey = NormalizeOrganizeKey(NormalizeOrganizePath(target));
            return !string.IsNullOrEmpty(currentKey) && currentKey == targetKey;
        }

        /// <summary>
        /// Returns true when the move would flatten the source into one of its
        /// own ancestors — e.g. source <c>/audiobooks/Author/Title/Narrator</c>
        /// targeting <c>/audiobooks/Author/Title</c> after a pattern change that
        /// removed a directory level. The move queue refuses this ("Source and
        /// target paths overlap"); mirroring the check here keeps these rows out
        /// of will_move so the preview reflects what apply would actually do.
        /// </summary>
        private static bool IsTargetAncestorOfSource(string? source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return false;
            string sourceFull;
            string targetFull;
            try
            {
                sourceFull = Path.GetFullPath(source);
                targetFull = Path.GetFullPath(target);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Unparseable path — let the apply-time guard surface it instead.
                return false;
            }
            if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase)) return false;
            var sep = Path.DirectorySeparatorChar;
            var sourceWithSep = sourceFull.TrimEnd(sep) + sep;
            var targetWithSep = targetFull.TrimEnd(sep) + sep;
            return sourceWithSep.StartsWith(targetWithSep, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true when the target directory exists on disk and is not
        /// empty. The move queue refuses moves into populated targets;
        /// surfacing the same condition at preview time keeps these rows out
        /// of will_move. Callers must rule out the canonical-equality case
        /// (target == current) before invoking this.
        /// </summary>
        private bool TargetExistsWithContent(string target) => _organizeFilesystem.TargetExistsWithContent(target);

        /// <summary>
        /// True when an existing, populated organize target can be safely
        /// replaced by the move: it holds no audio on disk (see
        /// <see cref="OrganizeFilesystem.IsMetadataStubDirectory"/>), no tracked
        /// file row lives at or under it, and no other audiobook's BasePath
        /// points at or under it. The move processor re-checks the on-disk half
        /// at execute time; the DB half is checked here because the processor is
        /// pure file-system code.
        /// </summary>
        private bool IsReplaceableStubTarget(
            string target,
            int audiobookId,
            IReadOnlyCollection<Audiobook> allAudiobooks,
            IReadOnlyCollection<AudiobookFile> allFiles)
        {
            if (!_organizeFilesystem.IsMetadataStubDirectory(target)) return false;

            var key = NormalizeOrganizeKey(NormalizeOrganizePath(target));
            if (string.IsNullOrEmpty(key)) return false;
            var prefix = key + "/";

            bool AtOrUnderTarget(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                var k = NormalizeOrganizeKey(NormalizeOrganizePath(path));
                return k == key || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            if (allFiles.Any(f => AtOrUnderTarget(f.Path))) return false;
            if (allAudiobooks.Any(a => a.Id != audiobookId && AtOrUnderTarget(a.BasePath))) return false;
            return true;
        }
    }
}
