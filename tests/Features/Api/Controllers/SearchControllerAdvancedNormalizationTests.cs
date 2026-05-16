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
using Listenarr.Api.Controllers;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Application.Search;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class SearchControllerAdvancedNormalizationTests
    {
        [Fact]
        public async Task AdvancedSearch_StripsStructuredTitlePrefix_BeforeBuildingUnifiedQuery()
        {
            var mockService = new Mock<ISearchService>();
            string? capturedQuery = null;
            mockService
                .Setup(s => s.IntelligentSearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<double>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string query, int _, int _, string _, bool _, double _, string _, string? _, CancellationToken _) =>
                {
                    capturedQuery = query;
                    return Task.FromResult(new List<MetadataSearchResult>());
                });

            var logger = Mock.Of<ILogger<SearchController>>();
            using var httpClient = new System.Net.Http.HttpClient();
            var mockAudibleService = new Mock<AudibleService>(httpClient, Mock.Of<ILogger<AudibleService>>());
            var mockMetadataService = new Mock<IAudiobookMetadataService>();
            var controller = new SearchController(mockService.Object, logger, mockAudibleService.Object, mockMetadataService.Object);
            controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            var req = new SearchRequest
            {
                Mode = SearchMode.Advanced,
                Title = "TITLE:Project Hail Mary",
                Language = "english"
            };

            var reqJson = JsonSerializer.SerializeToElement(req);
            var actionResult = await controller.Search(reqJson);

            Assert.NotNull(actionResult);
            Assert.Equal("TITLE:Project Hail Mary", capturedQuery);
        }
    }
}
