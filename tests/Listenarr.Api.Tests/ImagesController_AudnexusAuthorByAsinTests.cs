using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Listenarr.Api.Controllers;
using Listenarr.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
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

            var audimetaMock = new Mock<AudimetaService>(new System.Net.Http.HttpClient(), Mock.Of<ILogger<AudimetaService>>());

            var audnexusMock = new Mock<IAudnexusService>();
            audnexusMock.Setup(a => a.GetAuthorAsync(identifier, It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(new AudnexusAuthorResponse { Asin = identifier, Name = "Author", Image = imageUrl });

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_audnexus");
            Directory.CreateDirectory(Path.Join(tempRoot, "config", "cache", "images", "temp"));
            var fullPath = Path.Join(tempRoot, relativePath);
            File.WriteAllText(fullPath, "fake author image");

            var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(mockImageCache.Object, Mock.Of<IAudiobookMetadataService>(), audimetaMock.Object, audnexusMock.Object, Mock.Of<IAudiobookRepository>(), Mock.Of<ILogger<ImagesController>>(), mockEnv.Object);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            // Act
            var result = await controller.GetImage(identifier);

            // Assert
            audnexusMock.Verify(a => a.GetAuthorAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
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
