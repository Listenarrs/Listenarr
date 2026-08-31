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

namespace Listenarr.Tests.Features.Api.Features.Images
{
    public class ImagesController_AudnexusAuthorByAsinTests
    {
        [Fact]
        public async Task GetImage_UsesAudnexusGetAuthor_WhenIdentifierIsAsin()
        {
            // Arrange
            var identifier = "B00AUTHOR1"; // sample ASIN-like identifier
            var relativePath = $"config/cache/images/temp/{identifier}.jpg";
            var imageUrl = "https://audnexus.covers/author_by_asin.jpg";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.SetupSequence(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync((string?)null).ReturnsAsync(relativePath);
            mockImageCache.Setup(m => m.DownloadAndCacheImageAsync(imageUrl, identifier)).ReturnsAsync(relativePath);

            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());

            var audnexusMock = new Mock<IAudnexusService>();
            audnexusMock.Setup(a => a.GetAuthorAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(new AudnexusAuthorResponse { Asin = identifier, Name = "Author", Image = imageUrl });

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_audnexus");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake author image");

            var mockPathService = new Mock<IApplicationPathService>();
            mockPathService.SetupGet(p => p.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(mockImageCache.Object, Mock.Of<IAudiobookMetadataService>(), audibleMock.Object, audnexusMock.Object, Mock.Of<IAudiobookRepository>(), Mock.Of<ILogger<ImagesController>>(), mockPathService.Object, new LocalFileSystem());
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            audnexusMock.Verify(a => a.GetAuthorAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
            mockImageCache.Verify(m => m.DownloadAndCacheImageAsync(imageUrl, identifier), Times.Once);

            // The file was created above, so serving it is the only correct outcome.
            // Accepting a 404 here as well would let this test pass with image serving
            // removed entirely, which is what it used to do.
            var fileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(fullPath, fileResult.FileName);

            // Cleanup
            try
            {
                File.Delete(fullPath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }

            try
            {
                Directory.Delete(Path.Join(tempRoot, "config", "cache", "images", "temp"), true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }
        }
    }
}
