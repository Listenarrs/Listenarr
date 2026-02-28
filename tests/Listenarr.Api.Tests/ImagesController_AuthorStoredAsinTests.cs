using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Listenarr.Api.Controllers;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
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

            var audimetaMock = new Mock<AudimetaService>(new System.Net.Http.HttpClient(), Mock.Of<ILogger<AudimetaService>>());
            audimetaMock.Setup(a => a.LookupAuthorAsync(identifier, It.IsAny<string>())).ReturnsAsync((AuthorLookupItem?)null);

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

            var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(mockImageCache.Object, Mock.Of<IAudiobookMetadataService>(), audimetaMock.Object, audnexusMock.Object, mockRepo.Object, Mock.Of<ILogger<ImagesController>>(), mockEnv.Object);
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
