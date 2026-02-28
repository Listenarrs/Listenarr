using System.IO;
using System.Threading.Tasks;
using Listenarr.Api.Controllers;
using Listenarr.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class ImagesController_MetadataDownloadFallbackTests
    {
        [Fact]
        public async Task GetImage_FallsBackToGetMetadataAsync_WhenAudimetaNull_AndDownloadsImage()
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

            var audimetaMock = new Mock<AudimetaService>(new System.Net.Http.HttpClient(), Mock.Of<ILogger<AudimetaService>>());
            audimetaMock.Setup(a => a.GetBookMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>())).ReturnsAsync((AudimetaBookResponse?)null);

            var mockMetadata = new Mock<IAudiobookMetadataService>();
            // Fallback GetMetadataAsync returns envelope with metadata.ImageUrl
            var meta = new AudimetaBookResponse { ImageUrl = imageUrl };
            // Return the AudimetaBookResponse directly so the controller can handle it without anonymous envelope issues
            mockMetadata.Setup(m => m.GetAudimetaMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((AudimetaBookResponse?)null);
            mockMetadata.Setup(m => m.GetMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((object)meta);

            // Create temporary content root and the cached image file
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_fallback");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake image data");

            var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var audnexusMock = Mock.Of<IAudnexusService>();
            var controller = new ImagesController(mockImageCache.Object, mockMetadata.Object, audimetaMock.Object, audnexusMock, Mock.Of<IAudiobookRepository>(), Mock.Of<ILogger<ImagesController>>(), mockEnv.Object);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            mockMetadata.Verify(m => m.GetAudimetaMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
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
            catch (System.Exception)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }

            try
            {
                Directory.Delete(Path.Join(tempRoot, "config", "cache", "images", "temp"), true);
            }
            catch (System.Exception)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }
        }
    }
}
