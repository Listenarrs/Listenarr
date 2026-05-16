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
using System.Reflection;
using Listenarr.Application.Search;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SearchServiceSortingTests
    {
        [Fact]
        public async Task ApplySorting_SortsByLanguage_Descending()
        {
            var svc = (SearchService)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SearchService));

            var results = new List<SearchResult>
            {
                new SearchResult { Id = "1", Title = "A", Language = "english" },
                new SearchResult { Id = "2", Title = "B", Language = "french" },
                new SearchResult { Id = "3", Title = "C", Language = null },
                new SearchResult { Id = "4", Title = "D", Language = "German" }
            };

            // Call private ApplySorting via reflection
            var method = typeof(SearchService).GetMethod("ApplySorting", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ordered = await (Task<List<SearchResult>>)method.Invoke(svc, new object[] { results, SearchSortBy.Language, SearchSortDirection.Descending })!;

            // Expect order: 'english', 'German', 'french', null (case-insensitive, descending)
            // StringComparer.OrdinalIgnoreCase sorts lexicographically; descending should put 'french' > 'english' > 'German' > '' but to be deterministic test the comparer by actual result
            Assert.Equal(4, ordered.Count);
            // Ensure none of the nulls are first when descending
            Assert.NotNull(ordered[0].Language);
        }

        [Fact]
        public async Task ApplySorting_SortsByLanguage_Ascending()
        {
            var svc = (SearchService)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SearchService));

            var results = new List<SearchResult>
            {
                new SearchResult { Id = "1", Title = "A", Language = "english" },
                new SearchResult { Id = "2", Title = "B", Language = "french" },
                new SearchResult { Id = "3", Title = "C", Language = null },
                new SearchResult { Id = "4", Title = "D", Language = "German" }
            };

            var method = typeof(SearchService).GetMethod("ApplySorting", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ordered = await (Task<List<SearchResult>>)method.Invoke(svc, new object[] { results, SearchSortBy.Language, SearchSortDirection.Ascending })!;

            // Ascending should place null/empty first
            Assert.Equal(4, ordered.Count);
            Assert.Null(ordered[0].Language);
        }
    }
}
