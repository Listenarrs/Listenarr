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
    // GetMetadataAsync returns Task<object?> and, on success, an *internal anonymous type*
    // `new { metadata = result, ... }`. The controller's fallback previously read it via
    // `dynamic env.metadata`, which throws RuntimeBinderException across the assembly boundary
    // (the anonymous type is internal to another assembly, so the binder sees it as `object`),
    // producing an uncacheable 500. These tests exercise that exact envelope shape — which the
    // other image tests deliberately avoid by returning AudibleBookResponse directly.
    public class ImagesController_MetadataEnvelopeReflectionTests
    {
        [Fact]
        public async Task GetImage_ResolvesCover_FromAnonymousMetadataEnvelope()
        {
            // Arrange
            var identifier = "BENVELOPE1";
            var relativePath = $"config/cache/images/temp/{identifier}.jpg";
            var imageUrl = "https://audnexus.covers/envelope.jpg";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.Setup(m => m.DownloadAndCacheImageAsync(imageUrl, identifier)).ReturnsAsync(relativePath);
            mockImageCache.SetupSequence(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync((string?)null).ReturnsAsync(relativePath);

            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());
            audibleMock.Setup(a => a.GetBookMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>())).ReturnsAsync((AudibleBookResponse?)null);

            var mockMetadata = new Mock<IAudiobookMetadataService>();
            mockMetadata.Setup(m => m.GetAudibleMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((AudibleBookResponse?)null);
            // The real GetMetadataAsync returns an internal anonymous envelope on success. Reproduce it
            // exactly so the controller must reach the cover URL through the reflection path.
            var envelope = new { metadata = new AudibleBookResponse { ImageUrl = imageUrl } };
            mockMetadata.Setup(m => m.GetMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((object)envelope);

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_envelope");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake image data");

            var mockPathService = new Mock<IApplicationPathService>();
            mockPathService.SetupGet(p => p.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(
                mockImageCache.Object, mockMetadata.Object, audibleMock.Object, Mock.Of<IAudnexusService>(),
                Mock.Of<IAudiobookRepository>(), Mock.Of<ILogger<ImagesController>>(), mockPathService.Object, new LocalFileSystem());
            controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert: reflection reached metadata.ImageUrl (so the URL was queued and downloaded).
            // This is the proof that resolution is revived, not merely that the 500 is avoided.
            mockImageCache.Verify(m => m.DownloadAndCacheImageAsync(imageUrl, identifier), Times.Once);
            Assert.False(result is ObjectResult obj && obj.StatusCode == 500, "fallback path must not 500");

            try { File.Delete(fullPath); } catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { }
            try { Directory.Delete(Path.Join(tempRoot, "config", "cache", "images", "temp"), true); } catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { }
        }

        [Fact]
        public async Task GetImage_DoesNotThrow_WhenEnvelopeHasNoMetadataMember()
        {
            // Arrange: an envelope shape without a `metadata` member. The old dynamic access threw
            // RuntimeBinderException -> 500; the reflection guard must instead return gracefully.
            var identifier = "BENVELOPE2";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.Setup(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync((string?)null);

            using var httpClientForAudible = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>());
            audibleMock.Setup(a => a.GetBookMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>())).ReturnsAsync((AudibleBookResponse?)null);

            var mockMetadata = new Mock<IAudiobookMetadataService>();
            mockMetadata.Setup(m => m.GetAudibleMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((AudibleBookResponse?)null);
            mockMetadata.Setup(m => m.GetMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((object)new { other = "no-metadata-member" });

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_envelope2");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));

            var mockPathService = new Mock<IApplicationPathService>();
            mockPathService.SetupGet(p => p.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(
                mockImageCache.Object, mockMetadata.Object, audibleMock.Object, Mock.Of<IAudnexusService>(),
                Mock.Of<IAudiobookRepository>(), Mock.Of<ILogger<ImagesController>>(), mockPathService.Object, new LocalFileSystem());
            controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert: no RuntimeBinderException surfacing as a 500.
            Assert.False(result is ObjectResult obj && obj.StatusCode == 500, "missing metadata member must not 500");

            try { Directory.Delete(Path.Join(tempRoot, "config", "cache", "images", "temp"), true); } catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { }
        }
    }
}
