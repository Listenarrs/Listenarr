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
    public class ImagesController_AlternateAsinCachedImageAliasTests
    {
        [Fact]
        public async Task GetImage_UsesCachedImageFromAlternateStoredAsin_WhenRequestedPrimaryAsinCacheMisses()
        {
            var requestedAsin = "B0NEWASI12";
            var oldAsin = "B0OLDASI34";
            var relativePath = $"config/cache/images/library/{oldAsin}.jpg";

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache.Setup(m => m.GetCachedImagePathAsync(requestedAsin))
                .ReturnsAsync((string?)null);
            mockImageCache.Setup(m => m.GetCachedImagePathAsync(oldAsin))
                .ReturnsAsync(relativePath);

            var mockMetadata = new Mock<IAudiobookMetadataService>();
            using var audimetaHttpClient = new System.Net.Http.HttpClient();
            var audimetaMock = new Mock<AudimetaService>(audimetaHttpClient, Mock.Of<ILogger<AudimetaService>>());
            var audnexusMock = new Mock<IAudnexusService>();

            var repoMock = new Mock<IAudiobookRepository>();
            repoMock.Setup(r => r.GetByAsinAsync(requestedAsin))
                .ReturnsAsync(new Audiobook
                {
                    Asin = requestedAsin,
                    ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                    {
                        new AudiobookExternalIdentifier
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = requestedAsin,
                            ValueNormalized = requestedAsin,
                            IsPrimary = true
                        },
                        new AudiobookExternalIdentifier
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = oldAsin,
                            ValueNormalized = oldAsin,
                            IsPrimary = false
                        }
                    }
                });

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_contentroot_alt_asin_alias");
            var fullPath = Path.Join(tempRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "fake image data");

            var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var controller = new ImagesController(
                mockImageCache.Object,
                mockMetadata.Object,
                audimetaMock.Object,
                audnexusMock.Object,
                repoMock.Object,
                Mock.Of<ILogger<ImagesController>>(),
                mockEnv.Object);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            };

            var result = await controller.GetImage(requestedAsin);

            var fileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(fullPath, fileResult.FileName);
            mockImageCache.Verify(m => m.DownloadAndCacheImageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

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
                Directory.Delete(Path.Join(tempRoot, "config"), true);
            }
            catch (System.Exception)
            {
                // Best-effort test cleanup; ignore cleanup failures.
            }
        }
    }
}
