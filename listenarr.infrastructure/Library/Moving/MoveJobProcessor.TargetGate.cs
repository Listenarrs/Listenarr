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
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public partial class MoveJobProcessor
    {
        /// <summary>
        /// Gate on an existing target directory. Returns true when the move may
        /// proceed. An empty target is reclaimed as-is. A populated target fails
        /// the job — unless the organize flow explicitly flagged it as a
        /// replaceable metadata stub (no audio at any depth, nothing in the DB
        /// referencing it), in which case the on-disk no-audio invariant is
        /// re-verified here and the stub is deleted so the move can replace it.
        /// The DB half of the stub check happened at enqueue time; if audio
        /// landed in the target since, refuse rather than destroy it.
        /// </summary>
        private async Task<bool> TryReclaimTargetAsync(MoveJob job, string target, CancellationToken stoppingToken)
        {
            if (!Directory.Exists(target))
            {
                return true;
            }

            var targetHasContent = Directory.EnumerateFileSystemEntries(target).Any();
            if (!targetHasContent)
            {
                // Target exists but is empty - safe to proceed (will use it instead of creating new)
                logger.LogInformation("Target directory {Target} exists but is empty; proceeding with move", LogRedaction.SanitizeFilePath(target));
                return true;
            }

            if (job.ReplaceStubTarget && organizeFilesystem.IsMetadataStubDirectory(target))
            {
                logger.LogInformation(
                    "Move job {JobId}: target {Target} is a verified metadata-only stub; deleting it so the move can replace it",
                    job.Id,
                    LogRedaction.SanitizeFilePath(target));
                Directory.Delete(target, true);
                return true;
            }

            await moveQueueService.UpdateJobStatusAsync(job.Id, "Failed", "Target directory already exists and contains files", stoppingToken);
            metrics.Increment("worker.move.job.failed");
            return false;
        }

        private static bool IsFilesystemRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.IsNullOrWhiteSpace(root)
                && string.Equals(fullPath, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }
}
