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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Queue
{
    public sealed class DownloadQueueCandidateLoader(
        IDownloadRepository downloadRepository,
        IDownloadProcessingJobRepository downloadProcessingJobRepository,
        ILogger<DownloadQueueCandidateLoader> logger)
    {
        public async Task<DownloadQueueCandidateSet> LoadAsync()
        {
            var queueDisplayCandidates = await downloadRepository.GetQueueDisplayCandidatesAsync();
            var queueMatchingCandidates = await downloadRepository.GetQueueMatchingCandidatesAsync();
            var knownClientItemIds = await downloadRepository.GetKnownClientItemIdsAsync();

            logger.LogInformation(
                "Loaded {DisplayCount} queue display candidates, {MatchingCount} queue matching candidates, and {KnownClientIdCount} known client IDs",
                queueDisplayCandidates.Count,
                queueMatchingCandidates.Count,
                knownClientItemIds.Count);

            var ddlDownloads = queueDisplayCandidates
                .Where(d => string.Equals(d.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var ddlToShow = await BuildVisibleDdlDownloadsAsync(ddlDownloads);

            var externalDownloads = queueDisplayCandidates
                .Where(d => !string.Equals(d.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var visibleDownloads = ddlToShow.Concat(externalDownloads).ToList();
            var allKnownClientItemIds = new HashSet<string>(knownClientItemIds, StringComparer.OrdinalIgnoreCase);

            logger.LogDebug(
                "Final filtering result: {FinalCount} downloads to include in queue filtering ({DdlCount} DDL, {ExternalCount} external), {MatchingCount} in matching pool",
                visibleDownloads.Count,
                ddlToShow.Count,
                externalDownloads.Count,
                queueMatchingCandidates.Count);

            return new DownloadQueueCandidateSet(
                visibleDownloads,
                queueMatchingCandidates,
                allKnownClientItemIds);
        }

        private async Task<List<Download>> BuildVisibleDdlDownloadsAsync(List<Download> ddlDownloads)
        {
            var ddlToShow = new List<Download>();
            if (!ddlDownloads.Any())
            {
                return ddlToShow;
            }

            var ddlCompleted = ddlDownloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
            if (ddlCompleted.Any())
            {
                var completedIds = ddlCompleted.Select(d => d.Id).ToList();
                var pendingJobs = await downloadProcessingJobRepository.GetPendingDownloadIdsAsync(completedIds);
                var allJobDownloads = await downloadProcessingJobRepository.GetAllJobDownloadIdsAsync(completedIds);

                var ddlCompletedToShow = ddlCompleted
                    .Where(d => pendingJobs.Contains(d.Id) || !allJobDownloads.Contains(d.Id))
                    .ToList();

                ddlToShow.AddRange(ddlCompletedToShow);
                logger.LogInformation(
                    "DDL pending jobs count: {PendingJobs}, All job downloads count: {AllJobs}, DDL completed to show: {CompletedToShow}",
                    pendingJobs.Count,
                    allJobDownloads.Count,
                    ddlCompletedToShow.Count);
            }

            ddlToShow.AddRange(ddlDownloads.Where(d =>
                d.Status != DownloadStatus.Completed &&
                d.Status != DownloadStatus.Moved));

            return ddlToShow;
        }
    }

    public sealed record DownloadQueueCandidateSet(
        List<Download> VisibleDownloads,
        List<Download> MatchingDownloads,
        HashSet<string> KnownClientItemIds);
}
