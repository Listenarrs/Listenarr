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
    public class ImagesController_LocalTitleAuthorOpenLibraryFallbackTests
    {
        [Fact]
        public async Task GetImage_UsesOpenLibraryTitleAuthorSearch_WhenProvidersAndLocalIdsAreMissing()
        {
            // Arrange
            var identifier = "B0DQR5KHHF";
            var title = "Alchemised";
            var author = "SenLinYu";
            var isbn = "9780593972700";
            var expectedUrl = $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg";
            var relativePath = $"config/cache/images/temp/{identifier}.jpg";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.SetupSequence(m => m.GetCachedImagePathAsync(identifier))
                .ReturnsAsync((string?)null)
                .ReturnsAsync(relativePath);
            mockImageCache.Setup(m => m.DownloadAndCacheImageAsync(expectedUrl, identifier))
                .ReturnsAsync(relativePath);

            var mockMetadata = new Mock<IAudiobookMetadataService>();
            mockMetadata.Setup(m => m.GetAudibleMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new AudibleBookResponse { ImageUrl = null, Isbn = null });

            using var audibleHttpClient = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(audibleHttpClient, Mock.Of<ILogger<AudibleService>>());
            var audnexusMock = new Mock<IAudnexusService>();
            audnexusMock.Setup(a => a.GetBookMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .ReturnsAsync(new AudnexusBookResponse { Image = null, Isbn = null });

            var repoMock = new Mock<IAudiobookRepository>();
            repoMock.Setup(r => r.GetByAsinAsync(identifier))
                .ReturnsAsync(new Audiobook
                {
                    Asin = identifier,
                    Title = title,
                    Authors = new List<string> { author },
                    Isbn = null,
                    OpenLibraryId = null
                });

            var openLibraryMock = new Mock<IOpenLibraryService>();
            openLibraryMock.Setup(o => o.GetIsbnsForTitleAsync(title, author))
                .ReturnsAsync(new List<string> { isbn });

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_local_title_author_fallback");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake image data");

            var mockPathService = new Mock<IApplicationPathService>();
            mockPathService.SetupGet(p => p.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(
                mockImageCache.Object,
                mockMetadata.Object,
                audibleMock.Object,
                audnexusMock.Object,
                repoMock.Object,
                openLibraryMock.Object,
                Mock.Of<ILogger<ImagesController>>(),
                mockPathService.Object, new LocalFileSystem());
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            openLibraryMock.Verify(o => o.GetIsbnsForTitleAsync(title, author), Times.Once);
            mockImageCache.Verify(m => m.DownloadAndCacheImageAsync(expectedUrl, identifier), Times.Once);

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
