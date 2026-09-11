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
using System.Text.Json;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Search
{
    /// <summary>
    /// MapMetadataResultToAudibleAsync reshapes a MetadataSearchResult into the Audible response
    /// shape. When the Audible product lookup cannot be made, it falls back to the fields the
    /// metadata result already carries. A MetadataSearchResult holds the series as a name only,
    /// so the fallback has no series identifier to report and must say so with a null rather
    /// than repeating the name into the asin field. Clients that store what they are handed
    /// otherwise write a series name into a series identifier column.
    /// </summary>
    [Trait("Name", "SearchResponseMapperSeriesFallbackTests")]
    [Trait("Category", "Api")]
    public class SearchResponseMapperSeriesFallbackTests : BaseTests
    {
        private static SearchResponseMapper MapperWithNoAudibleLookup()
        {
            var metadataService = new Mock<IAudiobookMetadataService>();
            metadataService
                .Setup(s => s.GetAudibleMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync((AudibleBookResponse?)null);

            return new SearchResponseMapper(
                metadataService.Object,
                NullLogger<SearchResponseMapper>.Instance);
        }

        private static async Task<JsonElement> MapAsync(MetadataSearchResult md)
        {
            var mapped = await MapperWithNoAudibleLookup()
                .MapMetadataResultToAudibleAsync(md, "us", new DefaultHttpContext());

            return JsonSerializer.SerializeToElement(mapped);
        }

        private static MetadataSearchResult SheAndAllan() => new()
        {
            Asin = "B00CQ5WAXW",
            Title = "She and Allan",
            Author = "H. Rider Haggard",
            Series = "Ayesha",
            SeriesNumber = "0",
        };

        [Fact]
        public async Task SeriesFallback_ReportsNoSeriesIdentifier()
        {
            var element = await MapAsync(SheAndAllan());

            var series = Assert.Single(element.GetProperty("series").EnumerateArray().ToList());

            Assert.Equal(JsonValueKind.Null, series.GetProperty("asin").ValueKind);
        }

        [Fact]
        public async Task SeriesFallback_StillCarriesTheNameAndPosition()
        {
            var element = await MapAsync(SheAndAllan());

            var series = Assert.Single(element.GetProperty("series").EnumerateArray().ToList());

            Assert.Equal("Ayesha", series.GetProperty("name").GetString());
            Assert.Equal("0", series.GetProperty("position").GetString());
        }

        /// <summary>
        /// The author fallback immediately above already reports a null asin for the same reason.
        /// Pinned here so the two stay consistent: if somebody ever fills one of them in from a
        /// name, this test says which convention the file follows.
        /// </summary>
        [Fact]
        public async Task AuthorFallback_AlsoReportsNoIdentifier()
        {
            var element = await MapAsync(SheAndAllan());

            var author = Assert.Single(element.GetProperty("authors").EnumerateArray().ToList());

            Assert.Equal(JsonValueKind.Null, author.GetProperty("asin").ValueKind);
            Assert.Equal("H. Rider Haggard", author.GetProperty("name").GetString());
        }

        [Fact]
        public async Task NoSeries_ReportsAnEmptySeriesList()
        {
            var md = SheAndAllan();
            md.Series = null;
            md.SeriesNumber = null;

            var element = await MapAsync(md);

            Assert.Empty(element.GetProperty("series").EnumerateArray().ToList());
        }
    }
}
