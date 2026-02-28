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
    public class ImagesController_AuthorFallbackTests
    {
        [Fact]
        public async Task GetImage_UsesAuthorLookup_WhenIdentifierIsAuthorName()
        {
            // Arrange
            var identifier = "J. R. R. Tolkien";
            var relativePath = $"config/cache/images/temp/{identifier.Replace(' ', '_')}.jpg";
            var imageUrl = "https://audimeta.covers/author.jpg";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.SetupSequence(m => m.GetCachedImagePathAsync(identifier)).ReturnsAsync((string?)null).ReturnsAsync(relativePath);
            mockImageCache.Setup(m => m.DownloadAndCacheImageAsync(imageUrl, identifier)).ReturnsAsync(relativePath);

            var audimetaMock = new Mock<AudimetaService>(new System.Net.Http.HttpClient(), Mock.Of<ILogger<AudimetaService>>());
            audimetaMock.Setup(a => a.LookupAuthorAsync(identifier, It.IsAny<string>())).ReturnsAsync(new AuthorLookupItem { Asin = "A1", Name = "J. R. R. Tolkien", Image = imageUrl });

            var audnexusMock = new Mock<IAudnexusService>();

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_author");
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
