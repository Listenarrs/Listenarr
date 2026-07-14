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
        /// Executes a move whose target sits INSIDE its own source — the
        /// shape naming templates produce when they append subfolders
        /// (".../Book" → ".../Book/Narrator"; a live library hit this on 97
        /// consecutive organize jobs, every one failing "Source and target
        /// paths overlap"). The copy walk can't do it (it would recurse into
        /// its own output, then the source cleanup would delete the result),
        /// but two same-volume renames can: rename the source aside (same
        /// parent — atomic), recreate the vacated path, rename into place.
        /// Throws on failure after best-effort rollback, leaving the library
        /// as it was.
        /// </summary>
        private void ExecuteDescendMove(MoveJob job, string source, string target, string targetParent)
        {
            var sourceParent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar));
            var asideName = source.TrimEnd(Path.DirectorySeparatorChar) + ".moving-" + job.Id.ToString("N");
            string? asideReason = null;
            if (string.IsNullOrEmpty(sourceParent)
                || !FileSystemSafety.TryValidateMutationTarget(asideName, [sourceParent], out asideName, out asideReason))
            {
                throw new IOException($"Refused descend-move temp path: {asideReason ?? "no source parent"}");
            }

            Directory.Move(source, asideName);
            try
            {
                Directory.CreateDirectory(targetParent);
                Directory.Move(asideName, target);
            }
            catch
            {
                // Roll the rename back so a failed job leaves the library
                // exactly as it was. The recreated parent skeleton is empty
                // by construction — safe to drop.
                try
                {
                    if (Directory.Exists(source)
                        && !Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any())
                    {
                        Directory.Delete(source, true);
                    }
                    if (Directory.Exists(asideName) && !Directory.Exists(source))
                    {
                        Directory.Move(asideName, source);
                    }
                }
                catch (Exception rollbackEx) when (rollbackEx is not OutOfMemoryException && rollbackEx is not StackOverflowException)
                {
                    logger.LogError(rollbackEx,
                        "Descend-move rollback failed for job {JobId}; files remain at {Aside}",
                        job.Id, LogRedaction.SanitizeFilePath(asideName));
                }
                throw;
            }
        }

        /// <summary>
        /// The inverse overlap (source inside its own target — flattening a
        /// folder up into its populated ancestor) stays blocked. Returns true
        /// when the job was failed and the caller should stop.
        /// </summary>
        private async Task<bool> FailIfUpwardOverlapAsync(MoveJob job, string source, string target, CancellationToken stoppingToken)
        {
            if (!FileUtils.IsPathInsideOf(source, target)) return false;

            await moveQueueService.UpdateJobStatusAsync(job.Id, "Failed", "Source and target paths overlap", stoppingToken);
            metrics.Increment("worker.move.job.failed");
            logger.LogWarning(
                "Blocked overlapping move job {JobId}: {Source} -> {Target}",
                job.Id,
                LogRedaction.SanitizeFilePath(source),
                LogRedaction.SanitizeFilePath(target));
            return true;
        }
    }
}
