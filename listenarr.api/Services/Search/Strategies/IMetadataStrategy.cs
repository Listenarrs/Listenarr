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
namespace Listenarr.Api.Services.Search.Strategies;

/// <summary>
/// Strategy for fetching metadata from different sources (Audible, Audnexus, scrapers, etc.).
/// </summary>
public interface IMetadataStrategy
{
    /// <summary>
    /// Name of the metadata source (e.g., "Audible", "Audnexus").
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Checks if this strategy can handle the given metadata source.
    /// </summary>
    bool CanHandle(ApiConfiguration source);

    /// <summary>
    /// Attempts to fetch metadata for the given ASIN.
    /// </summary>
    /// <param name="asin">The ASIN to fetch metadata for</param>
    /// <param name="source">The metadata source configuration</param>
    /// <param name="originalSource">Original source where the ASIN was found (Amazon/Audible)</param>
    /// <returns>Metadata if successful, null otherwise</returns>
    Task<AudibleBookMetadata?> FetchMetadataAsync(string asin, ApiConfiguration source, string? originalSource);
}
