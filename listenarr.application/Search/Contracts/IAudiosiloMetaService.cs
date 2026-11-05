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
 */
namespace Listenarr.Application.Search.Contracts
{
    /// <summary>
    /// Looks up audiobook editions (recordings) in audiosilo-meta (https://meta.audiosilo.app), an open
    /// community database (CC0) that keeps every recording of a work - including editions Audible's
    /// search no longer lists once a newer recording supersedes them. Each recording carries its
    /// narrators, runtime and region-scoped ASINs, which is what tells two editions of a book apart.
    /// </summary>
    public interface IAudiosiloMetaService
    {
        /// <summary>
        /// Recordings of works whose title (and, when given, author) match. Never throws for a
        /// lookup failure: an unreachable or erroring service yields an empty list.
        /// </summary>
        Task<IReadOnlyList<AudiobookEditionCandidate>> SearchEditionsAsync(
            string title,
            string? author,
            CancellationToken cancellationToken = default);
    }

    public sealed record AudiobookEditionCandidate(
        string Source,
        string WorkId,
        string Title,
        IReadOnlyList<string> Authors,
        IReadOnlyList<string> Narrators,
        int? RuntimeMinutes,
        string? Publisher,
        string? ReleaseDate,
        IReadOnlyList<RegionalAsin> Asins,
        string? CoverUrl,
        string? Series,
        string? SeriesPosition,
        string? Language,
        bool? Abridged);

    public sealed record RegionalAsin(string Region, string Asin);
}
