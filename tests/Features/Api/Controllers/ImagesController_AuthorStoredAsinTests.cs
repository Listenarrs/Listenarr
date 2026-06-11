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
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class ImagesController_AuthorStoredAsinTests
    {
        [Fact]
        public async Task GetImage_UsesStoredAuthorAsin_FromRepository_ToCallAudnexus_GetAuthor()
        {
            // Arrange
            var identifier = "Jane Doe"; // author name
            var authorAsin = "B00AUTHOR2";
            var relativePath = $"config/cache/images/temp/{identifier.Replace(' ', '_')}.jpg";
            var imageUrl = "https://audnexus.covers/author_stored_asin.jpg";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.SetupSequence(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync((string?)null).ReturnsAsync(relativePath);
            mockImageCache.Setup(m => m.DownloadAndCacheImageAsync(imageUrl, identifier)).ReturnsAsync(relativePath);

            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());
            audibleMock.Setup(a => a.LookupAuthorAsync(identifier, It.IsAny<string>())).ReturnsAsync((AuthorLookupItem?)null);

            var audnexusMock = new Mock<IAudnexusService>();
            audnexusMock.Setup(a => a.GetAuthorAsync(authorAsin, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(new AudnexusAuthorResponse { Asin = authorAsin, Name = identifier, Image = imageUrl });

            var book = new Audiobook { Title = "Sample", Authors = new System.Collections.Generic.List<string> { identifier }, AuthorAsins = new System.Collections.Generic.List<string> { authorAsin } };
            var mockRepo = new Mock<IAudiobookRepository>();
            // Legacy callers used GetAllAsync; new implementation prefers GetAuthorAsinByNameAsync — mock both for compatibility in tests
            mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new System.Collections.Generic.List<Audiobook> { book });
            mockRepo.Setup(r => r.GetAuthorAsinByNameAsync(identifier)).ReturnsAsync(authorAsin);

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_author_stored_asin");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake author image");

            var mockPathService = new Mock<IApplicationPathService>();
            mockPathService.SetupGet(p => p.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(mockImageCache.Object, Mock.Of<IAudiobookMetadataService>(), audibleMock.Object, audnexusMock.Object, mockRepo.Object, Mock.Of<ILogger<ImagesController>>(), mockPathService.Object);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            audnexusMock.Verify(a => a.GetAuthorAsync(authorAsin, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
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
