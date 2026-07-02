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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Aggregate view of the background move queue for the maintenance UI:
    /// per-status counts, the jobs processing right now, and short tails of
    /// recent completions/failures — each annotated with the audiobook's title
    /// and file count/bytes so the banner can say "moving X (12 files, 3.4 GB)"
    /// and "queue: N files, M GB remaining".
    /// </summary>
    public sealed class LibraryMoveSummaryWorkflow
    {
        private readonly IMoveJobRepository _moveJobRepository;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookFileRepository _audioFileRepository;

        public LibraryMoveSummaryWorkflow(
            IMoveJobRepository moveJobRepository,
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audioFileRepository)
        {
            _moveJobRepository = moveJobRepository;
            _audiobookRepository = audiobookRepository;
            _audioFileRepository = audioFileRepository;
        }

        public async Task<IActionResult> SummaryAsync(int recentLimit, CancellationToken ct)
        {
            if (recentLimit < 0) recentLimit = 0;
            if (recentLimit > 200) recentLimit = 200;

            var all = await _moveJobRepository.GetAllAsync(ct);

            var byStatus = all
                .GroupBy(j => j.Status ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            int CountOf(string s) => byStatus.TryGetValue(s, out var n) ? n : 0;

            var recentCompleted = all
                .Where(j => string.Equals(j.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.UpdatedAt ?? j.EnqueuedAt)
                .Take(recentLimit)
                .ToList();
            var recentFailed = all
                .Where(j => string.Equals(j.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.UpdatedAt ?? j.EnqueuedAt)
                .Take(recentLimit)
                .ToList();
            var processingNow = all
                .Where(j => string.Equals(j.Status, "Processing", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.UpdatedAt ?? j.EnqueuedAt)
                .ToList();
            var queuedNow = all
                .Where(j => string.Equals(j.Status, "Queued", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // File counts + byte totals + titles for the in-scope audiobooks so
            // the banner can render meaningful per-job and per-queue size info.
            var inScopeAudiobookIds = new HashSet<int>();
            foreach (var j in recentCompleted) inScopeAudiobookIds.Add(j.AudiobookId);
            foreach (var j in recentFailed) inScopeAudiobookIds.Add(j.AudiobookId);
            foreach (var j in processingNow) inScopeAudiobookIds.Add(j.AudiobookId);
            foreach (var j in queuedNow) inScopeAudiobookIds.Add(j.AudiobookId);

            var fileStatsByAudiobookId = new Dictionary<int, (int Count, long Bytes)>();
            var titlesByAudiobookId = new Dictionary<int, string?>();
            if (inScopeAudiobookIds.Count > 0)
            {
                var allFiles = await _audioFileRepository.GetAllAsync(ct);
                fileStatsByAudiobookId = allFiles
                    .Where(f => inScopeAudiobookIds.Contains(f.AudiobookId))
                    .GroupBy(f => f.AudiobookId)
                    .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(f => f.Size ?? 0L)));

                var allAudiobooks = await _audiobookRepository.GetAllAsync();
                titlesByAudiobookId = allAudiobooks
                    .Where(a => inScopeAudiobookIds.Contains(a.Id))
                    .ToDictionary(a => a.Id, a => a.Title);
            }

            (int FileCount, long TotalBytes) StatsFor(int audiobookId) =>
                fileStatsByAudiobookId.TryGetValue(audiobookId, out var s) ? s : (0, 0L);

            object Project(MoveJob j)
            {
                var (fc, tb) = StatsFor(j.AudiobookId);
                titlesByAudiobookId.TryGetValue(j.AudiobookId, out var title);
                return new
                {
                    id = j.Id,
                    audiobookId = j.AudiobookId,
                    audiobookTitle = title,
                    status = j.Status,
                    error = j.Error,
                    requestedPath = j.RequestedPath,
                    sourcePath = j.SourcePath,
                    enqueuedAt = j.EnqueuedAt,
                    updatedAt = j.UpdatedAt,
                    attemptCount = j.AttemptCount,
                    fileCount = fc,
                    totalBytes = tb,
                };
            }

            var queuedFiles = 0;
            var queuedBytes = 0L;
            foreach (var j in queuedNow)
            {
                var (fc, tb) = StatsFor(j.AudiobookId);
                queuedFiles += fc;
                queuedBytes += tb;
            }

            return new OkObjectResult(new
            {
                total = all.Count,
                queued = CountOf("Queued"),
                processing = CountOf("Processing"),
                completed = CountOf("Completed"),
                failed = CountOf("Failed"),
                // Any status the server hasn't seen the producer use yet falls
                // into a residual bucket so the FE never silently drops a count.
                other = all.Count - CountOf("Queued") - CountOf("Processing") - CountOf("Completed") - CountOf("Failed"),
                queuedFiles,
                queuedBytes,
                currentlyProcessing = processingNow.Select(Project).ToList(),
                recentCompleted = recentCompleted.Select(Project).ToList(),
                recentFailed = recentFailed.Select(Project).ToList(),
            });
        }
    }
}
