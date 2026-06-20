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

namespace Listenarr.Tests.Features.Application.Metadata.Audible
{
    [Trait("Name", "AudibleServiceTests")]
    [Trait("Category", "AudibleService")]
    [Trait("Third-Party", "Audible")]
    public class AudibleServiceTests
    {
        private static bool InvokeSearchResultIndicatesPodcast(AudibleSearchResult r)
        {
            var method = typeof(AudibleService).GetMethod("SearchResultIndicatesPodcast", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("Could not find SearchResultIndicatesPodcast method");
            return (bool)method.Invoke(null, new object[] { r });
        }

        [Fact]
        [Trait("Method", "SearchResultIndicatesPodcast")]
        public void ContentDeliveryBook_PreventsPodcastDetection()
        {
            var r = new AudibleSearchResult
            {
                ContentType = "podcast",
                ContentDeliveryType = "SinglePartBook"
            };

            var isPodcast = InvokeSearchResultIndicatesPodcast(r);
            Assert.False(isPodcast);
        }

        [Fact]
        [Trait("Method", "SearchResultIndicatesPodcast")]
        public void ContentTypePodcast_DetectedWhenNoBookDelivery()
        {
            var r = new AudibleSearchResult
            {
                ContentType = "podcast",
                ContentDeliveryType = null
            };

            var isPodcast = InvokeSearchResultIndicatesPodcast(r);
            Assert.True(isPodcast);
        }

        [Theory]
        [Trait("Method", "RemoveDiacritics")]
        [InlineData("Åsa Larsson", "Asa Larsson")]
        [InlineData("Ärzte Öberg", "Arzte Oberg")]
        [InlineData("café naïve", "cafe naive")]
        [InlineData("Björk Guðmundsdóttir", "Bjork Guðmundsdottir")]
        [InlineData("Harry Potter", "Harry Potter")]  // ASCII unchanged
        [InlineData("", "")]                           // empty unchanged
        public void RemoveDiacritics_StripsAccents(string input, string expected)
        {
            var result = AudibleService.RemoveDiacritics(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        [Trait("Method", "RemoveDiacritics")]
        public void RemoveDiacritics_Null_ReturnsNull()
        {
            var result = AudibleService.RemoveDiacritics(null!);
            Assert.Null(result);
        }

        [Fact]
        public void AudibleLookupJsonParser_ParsesAuthorArray()
        {
            var items = AudibleLookupJsonParser.ParseAuthorLookupItems("""
                [
                  { "asin": "A1", "name": "Author One" },
                  { "asin": "A2", "name": "Author Two" }
                ]
                """);

            Assert.Equal(2, items.Count);
            Assert.Equal("A1", items[0].Asin);
            Assert.Equal("Author Two", items[1].Name);
        }

        [Fact]
        public void AudibleLookupJsonParser_ParsesSingleAuthorEnvelope()
        {
            var item = AudibleLookupJsonParser.ParseSingleAuthorLookupItem("""
                { "asin": "A1", "name": "Author One", "image": "https://example.test/a.jpg", "region": "us" }
                """);

            Assert.NotNull(item);
            Assert.Equal("A1", item.Asin);
            Assert.Equal("Author One", item.Name);
            Assert.Equal("us", item.Region);
        }

        [Fact]
        public void AudibleLookupJsonParser_ParsesSeriesResultsEnvelope()
        {
            var items = AudibleLookupJsonParser.ParseSeriesLookupItems("""
                {
                  "results": [
                    { "asin": "S1", "name": "Series One", "position": "1" }
                  ]
                }
                """);

            Assert.Single(items);
            Assert.Equal("S1", items[0].Asin);
            Assert.Equal("Series One", items[0].Name);
            Assert.Equal("1", items[0].Position);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void AudibleLookupJsonParser_EmptyInput_ReturnsNoResults(string lookupJson)
        {
            Assert.Empty(AudibleLookupJsonParser.ParseAuthorLookupItems(lookupJson));
            Assert.Empty(AudibleLookupJsonParser.ParseSeriesLookupItems(lookupJson));
        }

        [Fact]
        public void AudibleAuthorCatalogMatcher_MatchesByAuthorAsin()
        {
            var result = new AudibleSearchResult
            {
                Authors = new List<AudibleAuthor>
                {
                    new() { Asin = "B001", Name = "Different Name" }
                }
            };

            Assert.True(AudibleAuthorCatalogMatcher.MatchesTarget(result, "Target Author", "B001"));
        }

        [Fact]
        public void AudibleAuthorCatalogMatcher_MatchesByNormalizedName()
        {
            var result = new AudibleSearchResult
            {
                Authors = new List<AudibleAuthor>
                {
                    new() { Name = "Asa Larsson" }
                }
            };

            Assert.True(AudibleAuthorCatalogMatcher.MatchesTarget(result, "Åsa Larsson", null));
        }

        [Fact]
        public void AudibleAuthorCatalogMatcher_BuildsStableFallbackKeyWhenAsinMissing()
        {
            var result = new AudibleSearchResult
            {
                Title = "Book",
                Link = "https://example.test/book"
            };

            Assert.Equal("Book|https://example.test/book", AudibleAuthorCatalogMatcher.BuildSearchResultKey(result));
        }
    }
}
