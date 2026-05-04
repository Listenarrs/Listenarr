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
using Listenarr.Api.Controllers;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class ImagesController_MetadataDownloadFallbackTests
    {
        [Fact]
        public async Task GetImage_FallsBackToGetMetadataAsync_WhenAudibleNull_AndDownloadsImage()
        {
            // Arrange
            var identifier = "BTESTASIN";
            var relativePath = $"config/cache/images/temp/{identifier}.jpg";
            var imageUrl = "https://audnexus.covers/fallback.jpg";

            var mockImageCache = new Mock<IImageCacheService>();

            // Mock DownloadAndCache to return a relative path
            mockImageCache.Setup(m => m.DownloadAndCacheImageAsync(imageUrl, identifier)).ReturnsAsync(relativePath);
            // After download, GetCachedImagePathAsync returns the relativePath (first call null, second call returned path)
            mockImageCache.SetupSequence(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync((string?)null).ReturnsAsync(relativePath);

            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());
            audibleMock.Setup(a => a.GetBookMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>())).ReturnsAsync((AudibleBookResponse?)null);

            var mockMetadata = new Mock<IAudiobookMetadataService>();
            // Fallback GetMetadataAsync returns envelope with metadata.ImageUrl
            var meta = new AudibleBookResponse { ImageUrl = imageUrl };
            // Return the AudibleBookResponse directly so the controller can handle it without anonymous envelope issues
            mockMetadata.Setup(m => m.GetAudibleMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((AudibleBookResponse?)null);
            mockMetadata.Setup(m => m.GetMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((object)meta);

            // Create temporary content root and the cached image file
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_fallback");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake image data");

            var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var audnexusMock = Mock.Of<IAudnexusService>();
            var controller = new ImagesController(mockImageCache.Object, mockMetadata.Object, audibleMock.Object, audnexusMock, Mock.Of<IAudiobookRepository>(), Mock.Of<ILogger<ImagesController>>(), mockEnv.Object);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            mockMetadata.Verify(m => m.GetAudibleMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
            mockMetadata.Verify(m => m.GetMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
            mockImageCache.Verify(m => m.DownloadAndCacheImageAsync(imageUrl, identifier), Times.Once);

            if (result is PhysicalFileResult fileResult)
            {
                Assert.Equal(fullPath, fileResult.FileName);
            }
            else
            {
                Assert.IsType<NotFoundObjectResult>(result);
            }

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
