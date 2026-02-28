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
            mockMetadata.Setup(m => m.GetAudimetaMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new AudimetaBookResponse
                {
                    ImageUrl = null,
                    Description = "<p>Book description only, not an image URL</p>",
                    Isbn = null
                });

            using var audimetaHttpClient = new System.Net.Http.HttpClient();
            var audimetaMock = new Mock<AudimetaService>(audimetaHttpClient, Mock.Of<ILogger<AudimetaService>>());
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
                audimetaMock.Object,
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
