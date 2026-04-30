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
namespace Listenarr.Api.Services.Search;

/// <summary>
/// Handles merging and population of metadata from various sources (search results, scraped data, API responses).
/// </summary>
public class MetadataMerger
{
    private readonly ILogger<MetadataMerger> _logger;

    public MetadataMerger(ILogger<MetadataMerger> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Merges data from source metadata into target metadata, filling in missing fields.
    /// </summary>
    public void MergeMetadata(AudibleBookMetadata source, AudibleBookMetadata target)
    {
        _logger.LogInformation("Merging metadata: source.PublishYear={SourceYear}, target.PublishYear={TargetYear}, target.Asin={Asin}",
            source.PublishYear, target.PublishYear, target.Asin);

        // Only merge fields that are missing in target
        if (string.IsNullOrEmpty(target.Title)) target.Title = source.Title;
        if (target.Authors == null || !target.Authors.Any()) target.Authors = source.Authors;
        if (target.Narrators == null || !target.Narrators.Any()) target.Narrators = source.Narrators;
        if (string.IsNullOrEmpty(target.Publisher)) target.Publisher = source.Publisher;
        if (string.IsNullOrEmpty(target.Description)) target.Description = source.Description;
        if (target.Genres == null || !target.Genres.Any()) target.Genres = source.Genres;
        if (string.IsNullOrEmpty(target.Language)) target.Language = source.Language;
        if (string.IsNullOrEmpty(target.ImageUrl)) target.ImageUrl = source.ImageUrl;
        if (!target.Runtime.HasValue && source.Runtime.HasValue) target.Runtime = source.Runtime;
        if (string.IsNullOrEmpty(target.PublishYear)) target.PublishYear = source.PublishYear;
        if (string.IsNullOrEmpty(target.Subtitle)) target.Subtitle = source.Subtitle;

        _logger.LogInformation("After merge: target.PublishYear={TargetYear}, target.Asin={Asin}", target.PublishYear, target.Asin);
    }

}
