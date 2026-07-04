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

namespace Listenarr.Application.Audiobooks.Contracts
{
    public enum CoverCacheStatus
    {
        /// <summary>A cover was resolved and its cached file location is known.</summary>
        Resolved = 0,
        /// <summary>No cover could be resolved for this identifier (negative cache).</summary>
        NoCover = 1,
    }

    /// <summary>One row of the cover-resolution cache.</summary>
    public sealed record CoverCacheEntry(
        string Identifier,
        CoverCacheStatus Status,
        string? RelativePath,
        string? SourceUrl,
        long LastCheckedUtcSeconds,
        string? ThumbSizes);

    /// <summary>
    /// Fast local index of cover-image resolution results, kept in a dedicated sidecar SQLite
    /// database (ImageCache.db) separate from the app's main DB. Lets the image endpoint answer
    /// "already resolved" / "known to have no cover" with a single indexed lookup instead of a
    /// live provider round-trip on every view/refresh. Safe to delete — it rebuilds itself.
    /// </summary>
    public interface IImageCacheStore
    {
        /// <summary>Look up a cached resolution result, or null if this identifier is unknown.</summary>
        CoverCacheEntry? TryGet(string identifier);

        /// <summary>Record that a cover resolved to <paramref name="relativePath"/>.</summary>
        void UpsertResolved(string identifier, string relativePath, string? sourceUrl, string? thumbSizes = null);

        /// <summary>Record that no cover could be found for this identifier (negative cache).</summary>
        void UpsertNoCover(string identifier);

        /// <summary>Remove a cached result (e.g. on a manual re-check).</summary>
        void Remove(string identifier);
    }
}
