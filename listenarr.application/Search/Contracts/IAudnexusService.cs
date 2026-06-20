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

namespace Listenarr.Application.Search.Contracts
{
    /// <summary>
    /// Interface for AudnexusService to allow easier testing and DI.
    /// </summary>
    public interface IAudnexusService
    {
        Task<AudnexusBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool seedAuthors = true, bool update = false);
        Task<List<AudnexusAuthorSearchResult>?> SearchAuthorsAsync(string name, string region = "us");
        Task<AudnexusAuthorResponse?> GetAuthorAsync(string asin, string region = "us", bool update = false);
        Task<AudnexusChapterResponse?> GetChaptersAsync(string asin, string region = "us", bool update = false);
    }
}
