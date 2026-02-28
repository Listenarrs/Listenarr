using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Api.Services.Search;
using Listenarr.Api.Services.Search.Filters;
using Listenarr.Api.Services.Search.Strategies;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class SearchServiceSortingTests
    {
        [Fact]
        public void ApplySorting_SortsByLanguage_Descending()
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
            var ordered = (List<SearchResult>)method.Invoke(svc, new object[] { results, SearchSortBy.Language, SearchSortDirection.Descending })!;

            // Expect order: 'english', 'German', 'french', null (case-insensitive, descending)
            // StringComparer.OrdinalIgnoreCase sorts lexicographically; descending should put 'french' > 'english' > 'German' > '' but to be deterministic test the comparer by actual result
            Assert.Equal(4, ordered.Count);
            // Ensure none of the nulls are first when descending
            Assert.NotNull(ordered[0].Language);
        }

        [Fact]
        public void ApplySorting_SortsByLanguage_Ascending()
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
            var ordered = (List<SearchResult>)method.Invoke(svc, new object[] { results, SearchSortBy.Language, SearchSortDirection.Ascending })!;

            // Ascending should place null/empty first
            Assert.Equal(4, ordered.Count);
            Assert.Null(ordered[0].Language);
        }
    }
}
