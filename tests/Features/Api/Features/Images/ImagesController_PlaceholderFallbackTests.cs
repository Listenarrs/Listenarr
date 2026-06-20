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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Images
{
    public class ImagesController_PlaceholderFallbackTests
    {
        [Fact]
        public async Task GetImage_ReturnsPlaceholder_WhenImageLookupFails()
        {
            // Arrange
            const string identifier = "B000APXZHK";

            var imageCache = new Mock<IImageCacheService>();
            imageCache.Setup(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync((string?)null);

            var metadataService = new Mock<IAudiobookMetadataService>();
            metadataService
                .Setup(m => m.GetAudibleMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync((AudibleBookResponse?)null);
            metadataService
                .Setup(m => m.GetMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync((object?)null);

            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());
            audible
                .Setup(m => m.LookupAuthorAsync(identifier, It.IsAny<string>()))
                .ReturnsAsync((AuthorLookupItem?)null);

            var audnexus = new Mock<IAudnexusService>();
            audnexus
                .Setup(m => m.GetBookMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .ReturnsAsync((AudnexusBookResponse?)null);
            audnexus
                .Setup(m => m.GetAuthorAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync((AudnexusAuthorResponse?)null);
            audnexus
                .Setup(m => m.SearchAuthorsAsync(identifier, It.IsAny<string>()))
                .ReturnsAsync(new List<AudnexusAuthorSearchResult>());

            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByAsinAsync(identifier)).ReturnsAsync((Audiobook?)null);
            repo.Setup(r => r.GetAuthorAsinByNameAsync(identifier)).ReturnsAsync((string?)null);

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_missing_placeholder");
            Directory.CreateDirectory(tempRoot);

            var mockPathService = new Mock<IApplicationPathService>();
            mockPathService.SetupGet(p => p.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(
                imageCache.Object,
                metadataService.Object,
                audible.Object,
                audnexus.Object,
                repo.Object,
                Mock.Of<ILogger<ImagesController>>(),
                mockPathService.Object, new LocalFileSystem());

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            Assert.False(result is NotFoundObjectResult);
            Assert.True(
                result is PhysicalFileResult physical && physical.FileName.EndsWith("placeholder.svg", System.StringComparison.OrdinalIgnoreCase)
                || result is RedirectResult redirect && redirect.Url == "/placeholder.svg",
                $"Expected placeholder response, got {result.GetType().Name}");
        }
    }
}
