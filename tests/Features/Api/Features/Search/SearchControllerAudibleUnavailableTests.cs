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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Search
{
    /// <summary>
    /// The flag only earns its keep if the endpoint acts on it. These cover the two answers
    /// GET /search/audible has to keep apart: a failed lookup and a catalogue that really
    /// holds nothing. Without the second one, a 503 on every empty result would pass.
    /// </summary>
    public class SearchControllerAudibleUnavailableTests
    {
        [Fact]
        public async Task SearchAudible_WhenTheProviderDidNotAnswer_Returns503()
        {
            var controller = BuildController(new AudibleSearchResponse
            {
                Results = new List<AudibleSearchResult>(),
                TotalResults = 0,
                ProviderUnavailable = true
            });

            var result = await controller.SearchAudible("dune");

            var status = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(503, status.StatusCode);
        }

        [Fact]
        public async Task SearchAudible_WhenTheCatalogueAnsweredWithNothing_Returns200()
        {
            var controller = BuildController(new AudibleSearchResponse
            {
                Results = new List<AudibleSearchResult>(),
                TotalResults = 0
            });

            var result = await controller.SearchAudible("dune");

            Assert.IsType<OkObjectResult>(result.Result);
        }

        private static SearchController BuildController(AudibleSearchResponse response)
        {
            var controller = new SearchController(
                Mock.Of<ISearchService>(),
                Mock.Of<ILogger<SearchController>>(),
                new StubAudibleService(response),
                Mock.Of<IAudiobookMetadataService>());
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            };
            return controller;
        }

        private sealed class StubAudibleService(AudibleSearchResponse response) : AudibleService(new HttpClient(), Mock.Of<ILogger<AudibleService>>())
        {
            public override Task<AudibleSearchResponse?> SearchBooksAsync(
                string query, int page = 1, int limit = 50, string region = "us", string? language = null)
                => Task.FromResult<AudibleSearchResponse?>(response);
        }
    }
}
