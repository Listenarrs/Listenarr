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
using Listenarr.Tests.Features.Api.Features.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryImportControllerTests")]
    [Trait("Category", "LibraryImportController")]
    public class LibraryImportControllerTests : BaseTests
    {
        private static LibraryImportController CreateController(Mock<ISearchService> search)
        {
            var metadata = new Mock<IAudiobookMetadataService>().Object;
            var mapper = new SearchResponseMapper(metadata, NullLogger<SearchResponseMapper>.Instance, null);
            var workflow = new StructuredSearchWorkflow(
                search.Object,
                NullLogger<StructuredSearchWorkflow>.Instance,
                new StubAudibleService(),
                metadata,
                null,
                new MetadataConverters(null, NullLogger<MetadataConverters>.Instance),
                mapper,
                null);
            return new LibraryImportController(workflow)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        private static Mock<ISearchService> SearchReturning(params MetadataSearchResult[] results)
        {
            var search = new Mock<ISearchService>();
            search.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(results.ToList());
            return search;
        }

        private static JsonElement Body(IActionResult result)
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            return JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        [Fact]
        [Trait("Method", "Match")]
        [Trait("Scenario", "TakesTheFittingResultPreferringTheAuthor")]
        public async Task Match_TakesTheFittingResultPreferringTheAuthor()
        {
            var search = SearchReturning(
                new MetadataSearchResult { Asin = "B0CY5SRLQQ", Title = "The Perfect Match", Author = "Lyla Mars" },
                new MetadataSearchResult { Asin = "B0OTHER004", Title = "Primal Hunter, Tome 4", Author = "Someone" },
                new MetadataSearchResult { Asin = "B0FXXVV42J", Title = "Primal Hunter", Subtitle = "Primal Hunter, Tome 4", Author = "Zogarth" });

            var body = Body(await CreateController(search).Match(new LibraryImportController.MatchRequest
            {
                Title = "Primal Hunter, Tome 4",
                Author = "Zogarth",
                Region = "fr"
            }));

            Assert.Equal(3, body.GetProperty("candidates").GetArrayLength());
            Assert.Equal("B0FXXVV42J", body.GetProperty("match").GetProperty("asin").GetString());
            Assert.Equal("fr", body.GetProperty("match").GetProperty("region").GetString());
            search.Verify(s => s.IntelligentSearchAsync(It.IsAny<string>(), 8, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), "fr", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "Match")]
        [Trait("Scenario", "ReturnsNoMatchWhenNoTitleFits")]
        public async Task Match_ReturnsNoMatchWhenNoTitleFits()
        {
            var search = SearchReturning(
                new MetadataSearchResult { Asin = "B0CY5SRLQQ", Title = "The Perfect Match", Author = "Lyla Mars" });

            var body = Body(await CreateController(search).Match(new LibraryImportController.MatchRequest
            {
                Title = "not a match",
                Region = "fr"
            }));

            Assert.Equal(1, body.GetProperty("candidates").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("match").ValueKind);
        }

        [Fact]
        [Trait("Method", "Match")]
        [Trait("Scenario", "RequiresAnAsinOrATitle")]
        public async Task Match_RequiresAnAsinOrATitle()
        {
            var result = await CreateController(new Mock<ISearchService>()).Match(
                new LibraryImportController.MatchRequest { Asin = "erg" });

            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
