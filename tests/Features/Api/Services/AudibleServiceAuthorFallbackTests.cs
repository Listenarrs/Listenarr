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
using Listenarr.Api.Services;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "AudibleServiceAuthorFallbackTests")]
    [Trait("Category", "AudibleService")]
    [Trait("Third-Party", "Audible")]
    public class AudibleServiceAuthorFallbackTests : BaseTests
    {
        [Fact]
        [Trait("Method", "GetBooksByAuthorAsync")]
        public async Task GetBooksByAuthorAsync_ParsesFullBleedAudibleTiles_WhenAuthorBooksEndpointReturnsEmpty()
        {
            var service = _provider.GetRequiredService<AudibleService>();
            var result = await service.GetBooksByAuthorAsync("Andy Weir", "B00G0WYW92");

            var response = Assert.IsType<AudibleSearchResponse>(result);
            var book = Assert.Single(response.Results ?? []);
            Assert.Equal("B08G9PRS1K", book.Asin);
            Assert.Equal("Project Hail Mary", book.Title);
            Assert.Equal("https://m.media-amazon.com/images/I/B1jkwD8awiL.png", book.ImageUrl);
            Assert.Equal("https://www.audible.com/pd/Project-Hail-Mary-Audiobook/B08G9PRS1K", book.Link);
            Assert.Contains(book.Authors ?? [], author => author.Name == "Andy Weir");
        }

        [Fact]
        [Trait("Method", "GetBooksByAuthorAsync")]
        public async Task GetBooksByAuthorAsync_ParsesLegacyProductListItems_AndHydratesLanguage()
        {
            var service = _provider.GetRequiredService<AudibleService>();
            var result = await service.GetBooksByAuthorAsync("Ernest Cline", "B004XRR8Z6");

            var response = Assert.IsType<AudibleSearchResponse>(result);
            Assert.Equal(2, response.TotalResults);
            Assert.Collection(
                response.Results ?? new List<AudibleSearchResult>(),
                first =>
                {
                    Assert.Equal("B005FRGT44", first.Asin);
                    Assert.Equal("Ready Player One", first.Title);
                    Assert.Equal("english", first.Language);
                    Assert.Equal("https://www.audible.com/pd/Ready-Player-One-Audiobook/B005FRGT44", first.Link);
                },
                second =>
                {
                    Assert.Equal("0593396960", second.Asin);
                    Assert.Equal("Ready Player Two", second.Title);
                    Assert.Equal("english", second.Language);
                    Assert.Equal("https://www.audible.com/pd/Ready-Player-Two-Audiobook/0593396960", second.Link);
                });
        }

        [Fact]
        [Trait("Method", "GetBooksByAuthorAsync")]
        public async Task GetBooksByAuthorAsync_ReturnsComprehensiveDirectAuthorSearchResults_WithoutUsingFallbacks()
        {
            var service = _provider.GetRequiredService<AudibleService>();
            var result = await service.GetBooksByAuthorAsync("Stephen King", "B000AQ0842", 1, 50, "us");

            var response = Assert.IsType<AudibleSearchResponse>(result);
            Assert.Equal(3, response.TotalResults);
            Assert.Collection(
                response.Results ?? new List<AudibleSearchResult>(),
                first =>
                {
                    Assert.Equal("B0077DEH7A", first.Asin);
                    Assert.Equal("The Stand", first.Title);
                },
                second =>
                {
                    Assert.Equal("B005UR3VFO", second.Asin);
                    Assert.Equal("11-22-63", second.Title);
                },
                third =>
                {
                    Assert.Equal("B019WPM4ZM", third.Asin);
                    Assert.Equal("It", third.Title);
                });
        }
        [Fact]
        [Trait("Method", "SearchByAuthorAsync")]
        public async Task SearchByAuthorAsync_FallsBackToAudibleAuthorPage_WhenAuthorBooksEndpointReturnsNotFound()
        {
            var service = _provider.GetRequiredService<AudibleService>();
            var result = await service.SearchByAuthorAsync("SenLinYu", page: 1, limit: 50, region: "us");

            Assert.NotNull(result);
            Assert.NotNull(result!.Results);
            var book = Assert.Single(result.Results!);
            Assert.Equal("B0DQR9D4YG", book.Asin);
            Assert.Equal("Alchemised", book.Title);
            Assert.Equal("https://m.media-amazon.com/images/I/51IrMtF6fzL._SL500_.jpg", book.ImageUrl);
            Assert.Equal("https://www.audible.com/pd/Alchemised-Audiobook/B0DQR9D4YG", book.Link);
            Assert.NotNull(book.Authors);
            Assert.Equal("SenLinYu", Assert.Single(book.Authors!).Name);
            Assert.Equal(1, result.TotalResults);
        }

        [Fact]
        [Trait("Method", "SearchByTitleAndAuthorPagedAsync")]
        public async Task SearchByTitleAndAuthorPagedAsync_FiltersFallbackAuthorPageResults_ByTitle()
        {
            var service = _provider.GetRequiredService<AudibleService>();
            var result = await service.SearchByTitleAndAuthorPagedAsync("Alchemised", "SenLinYu", page: 1, limit: 50, region: "us");

            Assert.NotNull(result);
            Assert.NotNull(result!.Results);
            var book = Assert.Single(result.Results!);
            Assert.Equal("B0DQR9D4YG", book.Asin);
            Assert.Equal("Alchemised", book.Title);
            Assert.NotNull(book.Authors);
            Assert.Equal("SenLinYu", Assert.Single(book.Authors!).Name);
        }
    }
}
