using System.Collections.Generic;
using System.IO;
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
            mockMetadata.Setup(m => m.GetAudimetaMetadataAsync(identifier, It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new AudimetaBookResponse { ImageUrl = null, Isbn = null });

            using var audimetaHttpClient = new System.Net.Http.HttpClient();
            var audimetaMock = new Mock<AudimetaService>(audimetaHttpClient, Mock.Of<ILogger<AudimetaService>>());
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

            var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(
                mockImageCache.Object,
                mockMetadata.Object,
                audimetaMock.Object,
                audnexusMock.Object,
                repoMock.Object,
                openLibraryMock.Object,
                Mock.Of<ILogger<ImagesController>>(),
                mockEnv.Object);
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
