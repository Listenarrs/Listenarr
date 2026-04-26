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
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class AudiobookMetadataServiceTests
    {
        [Fact]
        public async Task GetMetadataAsync_UsesAudnexus_WhenAudibleMetadataReturnsNull()
        {
            // Arrange
            var mockSearch = new Mock<ISearchService>();
            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());
            var audnexusMock = new Mock<IAudnexusService>();
            var logger = Mock.Of<ILogger<AudiobookMetadataService>>();

            // Simulate two metadata sources: Audible (priority 1) then Audnexus (priority 2)
            var sources = new List<ApiConfiguration>
            {
                new ApiConfiguration { Name = "Audible", BaseUrl = "https://api.audible.com", Priority = 1, IsEnabled = true },
                new ApiConfiguration { Name = "Audnexus", BaseUrl = "https://api.audnex.us", Priority = 2, IsEnabled = true }
            };

            mockSearch.Setup(s => s.GetEnabledMetadataSourcesAsync()).ReturnsAsync(sources);

            // Audible-backed metadata returns null
            audibleMock.Setup(a => a.GetBookMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>())).ReturnsAsync((AudibleBookResponse?)null);

            // Audnexus returns a book with Image, Authors, Description and IsAdult set
            var audnexusResp = new AudnexusBookResponse
            {
                Asin = "BTESTASIN",
                Title = "Test Title",
                Image = "https://audnexus.covers/cover.jpg",
                Authors = new List<AudnexusAuthor> { new AudnexusAuthor { Asin = "BAUTH", Name = "Author One" } },
                Description = "Test description",
                IsAdult = true
            };
            audnexusMock.Setup(a => a.GetBookMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>())).ReturnsAsync(audnexusResp);

            var svc = new AudiobookMetadataService(mockSearch.Object, audibleMock.Object, audnexusMock.Object, logger);

            // Act
            var res = await svc.GetMetadataAsync("BTESTASIN", "us", true);

            // Assert
            Assert.NotNull(res);
            // Expect the returned object to contain the converted metadata (as AudibleBookResponse wrapper)
            var asObj = res as dynamic;
            Assert.NotNull(asObj);
            Assert.NotNull(asObj!.metadata);
            var metadata = asObj.metadata as AudibleBookResponse;
            Assert.NotNull(metadata);
            Assert.Equal("BTESTASIN", metadata!.Asin);
            Assert.Equal("https://audnexus.covers/cover.jpg", metadata!.ImageUrl);
            Assert.Equal("Audnexus", (string)asObj.source);

            // New assertions for mapped fields
            Assert.NotNull(metadata.Authors);
            Assert.Single(metadata.Authors);
            Assert.Equal("BAUTH", metadata.Authors[0].Asin);
            Assert.Equal("Test description", metadata.Description);
            Assert.True(metadata.Explicit);
        }
    }
}
