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
    public class ImagesController_TempToLibraryForAudiobookTests
    {
        [Fact]
        public async Task GetImage_WhenTempExists_AndAudiobookExists_MovesToLibraryAndServesLibraryFile()
        {
            // Arrange
            var identifier = "B002V1OF70";
            var tempRelative = $"config/cache/images/temp/{identifier}.jpg";
            var libRelative = $"config/cache/images/library/{identifier}.jpg";
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_temp_to_lib");

            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "library"));

            var tempFull = Path.Join(tempRoot, tempRelative);
            var libFull = Path.Join(tempRoot, libRelative);

            File.WriteAllText(tempFull, "temp image");
            File.WriteAllText(libFull, "library image");

            var mockImageCache = new Mock<IImageCacheService>();
            // Initially GetCachedImagePathAsync returns the temp path
            mockImageCache.Setup(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync(tempRelative);
            // When MoveToLibraryStorageAsync is called, pretend it moved and return the library relative path
            mockImageCache.Setup(m => m.MoveToLibraryStorageAsync(identifier, null)).ReturnsAsync(libRelative);

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByAsinAsync(identifier)).ReturnsAsync(new Audiobook { Asin = identifier });

            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());

            // Set ContentRootPath on the mocked environment to our tempRoot
            var mockPathService = new Mock<IApplicationPathService>();
            mockPathService.SetupGet(p => p.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(mockImageCache.Object, Mock.Of<IAudiobookMetadataService>(), audibleMock.Object, Mock.Of<IAudnexusService>(), mockRepo.Object, Mock.Of<ILogger<ImagesController>>(), mockPathService.Object, new LocalFileSystem());
            controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            mockImageCache.Verify(m => m.MoveToLibraryStorageAsync(identifier, null), Times.Once);

            // The file was created above, so serving it is the only correct outcome.
            // Accepting a 404 here as well would let this test pass with image serving
            // removed entirely, which is what it used to do.
            var fileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(libFull, fileResult.FileName);

            // Cleanup
            try
            {
                File.Delete(tempFull);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }

            try
            {
                File.Delete(libFull);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }

            try
            {
                Directory.Delete(Path.Join(tempRoot, "config"), true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }
        }
    }
}
