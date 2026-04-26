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
using Listenarr.Api.Services;
using Listenarr.Application.Repositories;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
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
            using var audibleHttpClient = new System.Net.Http.HttpClient();
            var audibleMock = new Mock<AudibleService>(audibleHttpClient, Mock.Of<ILogger<AudibleService>>());
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
                audibleMock.Object,
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
