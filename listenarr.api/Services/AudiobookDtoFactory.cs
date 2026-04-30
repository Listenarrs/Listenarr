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
namespace Listenarr.Api.Services
{
    public static class AudiobookDtoFactory
    {
        public static AudiobookDto BuildFromEntity(Audiobook audiobook)
        {
            if (audiobook == null) return null!;

            var files = audiobook.Files?.Select(f => new AudiobookFileDto
            {
                Id = f.Id,
                Path = f.Path,
                Size = f.Size,
                DurationSeconds = f.DurationSeconds,
                Format = f.Format,
                Container = f.Container,
                Codec = f.Codec,
                Bitrate = f.Bitrate,
                SampleRate = f.SampleRate,
                Channels = f.Channels,
                CreatedAt = f.CreatedAt,
                Source = f.Source
            }).ToArray();

            var dto = new AudiobookDto
            {
                Id = audiobook.Id,
                Title = audiobook.Title,
                Subtitle = audiobook.Subtitle,
                Authors = audiobook.Authors?.ToArray(),
                Narrators = audiobook.Narrators?.ToArray(),
                Asin = audiobook.Asin,
                Isbn = audiobook.Isbn,
                Language = audiobook.Language,
                Genres = audiobook.Genres,
                Tags = audiobook.Tags?.ToArray(),
                Description = audiobook.Description,
                PublishYear = audiobook.PublishYear,
                PublishedDate = audiobook.PublishedDate,
                Series = audiobook.Series,
                SeriesNumber = audiobook.SeriesNumber,
                SeriesMemberships = audiobook.SeriesMemberships?
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.SortOrder)
                    .Select(m => new AudiobookSeriesMembershipDto
                    {
                        Id = m.Id,
                        SeriesName = m.SeriesName,
                        SeriesNumber = m.SeriesNumber,
                        SeriesAsin = m.SeriesAsin,
                        IsPrimary = m.IsPrimary,
                        SortOrder = m.SortOrder
                    })
                    .ToArray(),
                Monitored = audiobook.Monitored,
                FilePath = audiobook.FilePath,
                FileSize = audiobook.FileSize,
                BasePath = audiobook.BasePath,
                Files = files,
                ImageUrl = audiobook.ImageUrl,
                Quality = audiobook.Quality,
                QualityProfileId = audiobook.QualityProfileId,
                Edition = audiobook.Edition,
                Version = audiobook.Version,
                Abridged = audiobook.Abridged,
                Explicit = audiobook.Explicit
            };

            // Compute wanted flag: true if monitored but has no file records
            // (if we have file records, audiobook has content and is not "wanted")
            dto.Wanted = audiobook.Monitored && (dto.Files == null || !dto.Files.Any());


            return dto;
        }
    }
}
