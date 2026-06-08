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

using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Audiobooks
{
    public class LibraryListService : ILibraryListService
    {
        private static readonly DownloadStatus[] ActiveLibraryDownloadStatuses =
        {
            DownloadStatus.Queued,
            DownloadStatus.Downloading,
            DownloadStatus.Paused,
            DownloadStatus.Processing,
            DownloadStatus.ImportPending
        };

        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookFileRepository _audiobookFileRepository;
        private readonly IQualityProfileRepository _qualityProfileRepository;
        private readonly IDownloadRepository _downloadRepository;

        public LibraryListService(
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audiobookFileRepository,
            IQualityProfileRepository qualityProfileRepository,
            IDownloadRepository downloadRepository)
        {
            _audiobookRepository = audiobookRepository;
            _audiobookFileRepository = audiobookFileRepository;
            _qualityProfileRepository = qualityProfileRepository;
            _downloadRepository = downloadRepository;
        }

        public async Task<IReadOnlyList<LibraryAudiobookListItem>> GetAllAsync()
        {
            var allAudiobooks = await _audiobookRepository.GetAllAsync();
            var audiobooks = allAudiobooks
                .OrderBy(a => a.Title)
                .ToList();

            if (audiobooks.Count == 0)
            {
                return Array.Empty<LibraryAudiobookListItem>();
            }

            // The download repository resolves its own DbContext from IDbContextFactory, so this
            // read can safely run concurrently with the shared-context reads below.
            var activeDownloadTask = _downloadRepository.GetActiveAudiobookIdsAsync(ActiveLibraryDownloadStatuses);

            // File summaries, file counts, and series memberships all execute against the shared
            // scoped ListenArrDbContext, so they must be awaited sequentially: starting a second
            // query on a single DbContext before the first completes throws "a second operation
            // was started on this context instance" under real database latency.
            var fileSummaryRows = await _audiobookFileRepository.GetFormatSummariesAsync();
            var fileCountById = await _audiobookFileRepository.GetCountsByAudiobookIdAsync();
            var membershipsByAudiobookId = await _audiobookRepository.GetAllSeriesMembershipsGroupedByAudiobookIdAsync();
            var filesByAudiobookId = fileSummaryRows
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<AudiobookFormatSummary>)g.ToList());

            var qualityProfileIds = audiobooks
                .Where(a => a.QualityProfileId.HasValue)
                .Select(a => a.QualityProfileId!.Value)
                .Distinct()
                .ToArray();

            var allQualityProfiles = await _qualityProfileRepository.GetAllAsync();
            var qualityProfiles = qualityProfileIds.Length == 0
                ? new List<QualityProfile>()
                : allQualityProfiles.Where(q => qualityProfileIds.Contains(q.Id)).ToList();

            var qualityProfilesById = qualityProfiles.ToDictionary(q => q.Id);

            var activeDownloadAudiobookIds = await activeDownloadTask;
            var activeDownloadAudiobookIdSet = activeDownloadAudiobookIds.ToHashSet();

            return audiobooks.Select(a =>
            {
                filesByAudiobookId.TryGetValue(a.Id, out var files);
                var hasTrackedFiles = files != null && files.Count > 0;
                var hasLegacyFileSummary = !string.IsNullOrWhiteSpace(a.FilePath);
                var hasAnyFile = hasTrackedFiles || hasLegacyFileSummary;
                var wanted = AudiobookWantedEvaluator.Compute(a.Monitored, hasTrackedFiles, a.FilePath);
                QualityProfile? qualityProfile = null;
                if (a.QualityProfileId.HasValue)
                {
                    qualityProfilesById.TryGetValue(a.QualityProfileId.Value, out qualityProfile);
                }

                return new LibraryAudiobookListItem
                {
                    Id = a.Id,
                    Title = a.Title,
                    Authors = a.Authors?.ToArray(),
                    Narrators = a.Narrators?.ToArray(),
                    PublishYear = a.PublishYear,
                    PublishedDate = a.PublishedDate,
                    Series = a.Series,
                    SeriesNumber = a.SeriesNumber,
                    SeriesMemberships = membershipsByAudiobookId.TryGetValue(a.Id, out var memberships) && memberships.Count > 0
                        ? memberships.Select(m => new AudiobookSeriesMembershipDto
                        {
                            Id = m.Id,
                            SeriesName = m.SeriesName,
                            SeriesNumber = m.SeriesNumber,
                            SeriesAsin = m.SeriesAsin,
                            IsPrimary = m.IsPrimary,
                            SortOrder = m.SortOrder,
                        }).ToArray()
                        : null,
                    Genres = a.Genres?.ToArray(),
                    Asin = a.Asin,
                    OpenLibraryId = a.OpenLibraryId,
                    Publisher = a.Publisher,
                    Language = a.Language,
                    Runtime = a.Runtime,
                    Edition = a.Edition,
                    ImageUrl = a.ImageUrl,
                    Monitored = a.Monitored,
                    BasePath = a.BasePath,
                    FilePath = a.FilePath,
                    FileSize = a.FileSize,
                    FileCount = fileCountById.TryGetValue(a.Id, out var trueCount) ? trueCount : 0,
                    Quality = a.Quality,
                    QualityProfileId = a.QualityProfileId,
                    AuthorAsins = a.AuthorAsins?.ToArray(),
                    Wanted = wanted,
                    Status = AudiobookStatusEvaluator.ComputeStatus(
                         activeDownloadAudiobookIdSet.Contains(a.Id),
                         hasAnyFile,
                         a.Quality,
                         qualityProfile,
                         files)
                };
            }).ToList();
        }
    }
}
