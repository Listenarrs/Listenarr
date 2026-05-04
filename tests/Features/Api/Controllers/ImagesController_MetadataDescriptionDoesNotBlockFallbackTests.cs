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
    public class ImagesController_MetadataDescriptionDoesNotBlockFallbackTests
    {
        [Fact]
        public async Task GetImage_IgnoresNonImageDescription_AndFallsBackToAudnexus()
        {
            // Arrange
            var identifier = "B0DTEST123"; // ASIN-like identifier so Audnexus ASIN fallback runs
            var relativePath = $"config/cache/images/temp/{identifier}.jpg";
            var audnexusImageUrl = "https://audnexus.covers/fallback.jpg";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.SetupSequence(m => m.GetCachedImagePathAsync(identifier))
                .ReturnsAsync((string?)null)
                .ReturnsAsync(relativePath);
            mockImageCache.Setup(m => m.DownloadAndCacheImageAsync(audnexusImageUrl, identifier))
                .ReturnsAsync(relativePath);

            var mockMetadata = new Mock<IAudiobookMetadataService>();
            mockMetadata.Setup(m => m.GetAudibleMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new AudibleBookResponse
                {
                    ImageUrl = null,
                    Description = "<p>Book description only, not an image URL</p>",
                    Isbn = null
                });

            using var audibleHttpClient = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(audibleHttpClient, Mock.Of<ILogger<AudibleService>>());
            var audnexusMock = new Mock<IAudnexusService>();
            audnexusMock.Setup(a => a.GetBookMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .ReturnsAsync(new AudnexusBookResponse { Image = audnexusImageUrl });

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_desc_fallback");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake image data");

            var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(
                mockImageCache.Object,
                mockMetadata.Object,
                audibleMock.Object,
                audnexusMock.Object,
                Mock.Of<IAudiobookRepository>(),
                Mock.Of<ILogger<ImagesController>>(),
                mockEnv.Object);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            mockImageCache.Verify(m => m.DownloadAndCacheImageAsync(audnexusImageUrl, identifier), Times.Once);
            mockImageCache.Verify(m => m.DownloadAndCacheImageAsync(It.Is<string>(s => s.Contains("description only")), identifier), Times.Never);

            if (result is PhysicalFileResult fileResult)
            {
                Assert.Equal(fullPath, fileResult.FileName);
            }
            else
            {
                Assert.IsType<NotFoundObjectResult>(result);
            }

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
