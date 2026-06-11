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
using Listenarr.Infrastructure.Cache;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Cache
{
    [Trait("Name", "ImageCacheServiceTests")]
    [Trait("Category", "Cache")]
    public class ImageCacheServiceTests : BaseTests
    {
        [Fact]
        public async Task MoveToAuthorLibraryStorageAsync_UsesApplicationPathServiceCachePaths()
        {
            var tempRoot = FileService.GetTempPath();
            var repoApiRoot = Path.Join(tempRoot, "listenarr.api");
            var tempCachePath = Path.Join(repoApiRoot, "config", "cache", "images", "temp");
            var libraryCachePath = Path.Join(repoApiRoot, "config", "cache", "images", "library");
            var authorCachePath = Path.Join(repoApiRoot, "config", "cache", "images", "authors");
            var seriesCachePath = Path.Join(repoApiRoot, "config", "cache", "images", "series");

            using var httpClient = new HttpClient();

            var applicationPathService = new Mock<IApplicationPathService>();
            applicationPathService.SetupGet(service => service.ContentRootPath).Returns(repoApiRoot);
            applicationPathService
                .Setup(service => service.ResolveFromConfig("cache", "images", "temp"))
                .Returns(tempCachePath);
            applicationPathService
                .Setup(service => service.ResolveFromConfig("cache", "images", "library"))
                .Returns(libraryCachePath);
            applicationPathService
                .Setup(service => service.ResolveFromConfig("cache", "images", "authors"))
                .Returns(authorCachePath);
            applicationPathService
                .Setup(service => service.ResolveFromConfig("cache", "images", "series"))
                .Returns(seriesCachePath);

            var service = new ImageCacheService(
                Mock.Of<ILogger<ImageCacheService>>(),
                httpClient,
                applicationPathService.Object);

            var repoTempImage = Path.Join(tempCachePath, "AUTHOR123.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(repoTempImage)!);
            await File.WriteAllBytesAsync(repoTempImage, new byte[] { 1, 2, 3, 4 });

            var relativePath = await service.MoveToAuthorLibraryStorageAsync("AUTHOR123");

            var expectedAuthorImage = Path.Join(authorCachePath, "AUTHOR123.jpg");

            Assert.Equal("config/cache/images/authors/AUTHOR123.jpg", relativePath);
            Assert.True(File.Exists(expectedAuthorImage));
        }
    }
}
