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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Organizing
{
    /// <summary>
    /// Pure filesystem primitives shared by the organize-library flow (preview
    /// classification in the API layer) and the move-job processor (execute-time
    /// guards): metadata-stub detection and the strongly-guarded flatten used
    /// for "nested one level too deep" rows.
    /// </summary>
    public sealed class OrganizeFilesystem(ILogger<OrganizeFilesystem> logger) : IOrganizeFilesystem
    {
        /// <summary>
        /// True when <paramref name="path"/> is an existing directory that
        /// contains no audio files at any depth — a metadata-only husk (covers,
        /// .opf, playlists) left behind by a removed release. On any read error
        /// we don't treat it as a stub.
        /// </summary>
        public bool IsMetadataStubDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!Directory.Exists(path)) return false;
                return !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Any(FileUtils.IsAudioFile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return false;
            }
        }

        /// <summary>
        /// True when the target directory exists on disk and is not empty. On a
        /// permission or IO error reading the target, returns false — the
        /// apply-time guard surfaces the real problem instead of a guess here.
        /// </summary>
        public bool TargetExistsWithContent(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            try
            {
                if (!Directory.Exists(target)) return false;
                return Directory.EnumerateFileSystemEntries(target).Any();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return false;
            }
        }

        /// <summary>
        /// Read-only feasibility check for <see cref="ExecuteFlatten"/>: decides
        /// whether collapsing <paramref name="source"/> into ancestor
        /// <paramref name="dest"/> is safe, and returns the file count, without
        /// touching the filesystem. Shared by the organize-preview (to bucket a
        /// nested row and decide whether to offer the one-click flatten) and by
        /// <see cref="ExecuteFlatten"/> (which refuses anything but
        /// <see cref="FlattenFeasibility.Ok"/>) so the preview's promise and the
        /// executor's guard can never disagree.
        /// </summary>
        public (FlattenFeasibility Feasibility, int FileCount) EvaluateFlatten(string source, string dest)
        {
            if (string.IsNullOrWhiteSpace(source)) return (FlattenFeasibility.SourcePathEmpty, 0);
            if (string.IsNullOrWhiteSpace(dest)) return (FlattenFeasibility.TargetPathEmpty, 0);
            if (!Directory.Exists(source)) return (FlattenFeasibility.SourceMissing, 0);

            var sourceFull = TrimTrailingSeparator(Path.GetFullPath(source));
            var destFull = TrimTrailingSeparator(Path.GetFullPath(dest));
            var sep = Path.DirectorySeparatorChar;

            if (string.Equals(sourceFull, destFull, StringComparison.OrdinalIgnoreCase))
                return (FlattenFeasibility.SamePath, 0);
            // dest must be a STRICT ancestor of source. If it isn't, this isn't a flatten.
            if (!(sourceFull + sep).StartsWith(destFull + sep, StringComparison.OrdinalIgnoreCase))
                return (FlattenFeasibility.NotAncestor, 0);
            if (!Directory.Exists(destFull))
                return (FlattenFeasibility.TargetMissing, 0);
            var destParent = Path.GetDirectoryName(destFull);
            if (string.IsNullOrEmpty(destParent) || IsFilesystemRoot(destParent))
                return (FlattenFeasibility.NoUsableParent, 0);

            // Guard: every FILE under dest must already live under source. If dest
            // holds any file outside source's subtree, flattening would merge into
            // populated content — refuse and leave it for manual resolution.
            var sourceWithSep = sourceFull + sep;
            try
            {
                var destFiles = Directory.EnumerateFiles(destFull, "*", SearchOption.AllDirectories)
                    .Select(f => TrimTrailingSeparator(Path.GetFullPath(f)))
                    .ToList();
                if (destFiles.Any(f => !f.StartsWith(sourceWithSep, StringComparison.OrdinalIgnoreCase)))
                    return (FlattenFeasibility.TargetHasForeignFiles, 0);
                if (destFiles.Count == 0)
                    return (FlattenFeasibility.NoFiles, 0);
                return (FlattenFeasibility.Ok, destFiles.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Can't read the target — treat as not-feasible so neither the
                // preview nor the executor offers a flatten it can't safely do.
                return (FlattenFeasibility.TargetHasForeignFiles, 0);
            }
        }

        /// <summary>Human-readable explanation of a non-Ok <see cref="FlattenFeasibility"/>.</summary>
        public string DescribeFeasibility(FlattenFeasibility f) => f switch
        {
            FlattenFeasibility.SourcePathEmpty => "Source path is empty",
            FlattenFeasibility.TargetPathEmpty => "Target path is empty",
            FlattenFeasibility.SourceMissing => "Source path does not exist",
            FlattenFeasibility.SamePath => "Source and target are the same path",
            FlattenFeasibility.NotAncestor => "Target is not an ancestor of source; not a flatten",
            FlattenFeasibility.TargetMissing => "Target directory does not exist",
            FlattenFeasibility.NoUsableParent => "Target has no usable parent directory (looks like a library root)",
            FlattenFeasibility.TargetHasForeignFiles => "Target directory contains files outside the nested source folder; resolve manually",
            FlattenFeasibility.NoFiles => "Nothing to flatten: no files under the source folder",
            _ => "Flatten is not available for this row",
        };

        /// <summary>
        /// Flatten a redundantly-nested folder: collapse everything under
        /// <paramref name="source"/> up into <paramref name="dest"/>, where
        /// <paramref name="dest"/> is a strict ancestor of <paramref name="source"/>
        /// (e.g. source <c>/a/Title/Narrator/Title</c> → dest <c>/a/Title/Narrator</c>).
        /// This is the exact case the normal move queue refuses ("Source and
        /// target paths overlap"). It backs the organize-library "nested one
        /// level too deep" rows, which a normal move can't resolve.
        ///
        /// Strongly guarded via <see cref="EvaluateFlatten"/> so no real file is
        /// ever clobbered or lost: refuses unless dest is a strict ancestor of
        /// source AND every file under dest already lives under source. That
        /// guarantees dest holds nothing but source's subtree wrapped in redundant
        /// (empty) directories, so the operation only collapses empty wrappers. The
        /// move itself is two same-volume <see cref="Directory.Move(string,string)"/>
        /// renames via a sibling staging dir, with the source extracted before the
        /// redundant dest shell is removed — so an interruption leaves the files
        /// recoverable at the staging path rather than destroyed.
        /// </summary>
        public FlattenOutcome ExecuteFlatten(string source, string dest, Guid jobId)
        {
            var (feasibility, fileCount) = EvaluateFlatten(source, dest);
            if (feasibility != FlattenFeasibility.Ok)
            {
                return new FlattenOutcome { Success = false, ErrorMessage = DescribeFeasibility(feasibility) };
            }

            var sourceFull = TrimTrailingSeparator(Path.GetFullPath(source));
            var destFull = TrimTrailingSeparator(Path.GetFullPath(dest));
            var destParent = Path.GetDirectoryName(destFull)!;
            var tempName = Path.Combine(destParent, $"flatten.tmp-{jobId:N}");
            try
            {
                if (Directory.Exists(tempName)) Directory.Delete(tempName, true);

                // 1. Extract source's subtree to a sibling staging dir (atomic
                //    same-volume rename). dest now holds only empty wrappers.
                Directory.Move(sourceFull, tempName);

                // 2. Remove the redundant dest shell (guaranteed file-free by the
                //    guard above), then promote the extracted subtree into dest.
                Directory.Delete(destFull, true);
                var finalParent = Path.GetDirectoryName(destFull);
                if (!string.IsNullOrEmpty(finalParent) && !Directory.Exists(finalParent))
                {
                    Directory.CreateDirectory(finalParent);
                }
                Directory.Move(tempName, destFull);

                return new FlattenOutcome { Success = true, FilesMoved = fileCount, TempPathUsed = tempName };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex,
                    "Flatten failed collapsing {Source} into {Dest}. Files may be staged at {Temp} — recover manually if needed.",
                    LogRedaction.SanitizeFilePath(sourceFull),
                    LogRedaction.SanitizeFilePath(destFull),
                    LogRedaction.SanitizeFilePath(tempName));
                return new FlattenOutcome
                {
                    Success = false,
                    ErrorMessage = $"Flatten failed: {ex.Message}",
                    TempPathUsed = tempName,
                };
            }
        }

        private static string TrimTrailingSeparator(string path) =>
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool IsFilesystemRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var fullPath = TrimTrailingSeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(fullPath);
            root = root == null ? null : TrimTrailingSeparator(root);
            return !string.IsNullOrWhiteSpace(root)
                && string.Equals(fullPath, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }
}
