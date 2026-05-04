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
using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class ImageCacheServiceTests
    {
        [Fact]
        public async Task MoveToAuthorLibraryStorageAsync_UsesRepoRoot_WhenDevContentRootPointsToBinOutput()
        {
            var originalEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-image-cache-tests", Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

                var repoApiRoot = Path.Join(tempRoot, "listenarr.api");
                var binRoot = Path.Join(repoApiRoot, "bin", "Debug", "net8.0");
                Directory.CreateDirectory(Path.Join(repoApiRoot, "config"));
                Directory.CreateDirectory(Path.Join(binRoot, "config"));
                File.WriteAllText(Path.Join(repoApiRoot, "listenarr.api.csproj"), "<Project />");

                using var httpClientForFactory = new HttpClient();
                var httpClientFactory = new Mock<IHttpClientFactory>();
                httpClientFactory
                    .Setup(factory => factory.CreateClient(It.IsAny<string>()))
                    .Returns(httpClientForFactory);

                var service = new ImageCacheService(
                    Mock.Of<ILogger<ImageCacheService>>(),
                    httpClientFactory.Object,
                    binRoot);

                var repoTempImage = Path.Join(repoApiRoot, "config", "cache", "images", "temp", "AUTHOR123.jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(repoTempImage)!);
                await File.WriteAllBytesAsync(repoTempImage, new byte[] { 1, 2, 3, 4 });

                var relativePath = await service.MoveToAuthorLibraryStorageAsync("AUTHOR123");

                var expectedAuthorImage = Path.Join(repoApiRoot, "config", "cache", "images", "authors", "AUTHOR123.jpg");
                var wrongAuthorImage = Path.Join(binRoot, "config", "cache", "images", "authors", "AUTHOR123.jpg");

                Assert.Equal("config/cache/images/authors/AUTHOR123.jpg", relativePath);
                Assert.True(File.Exists(expectedAuthorImage));
                Assert.False(File.Exists(wrongAuthorImage));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalEnvironment);
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }
    }
}
