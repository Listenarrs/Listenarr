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
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Images.Cache
{
    [Trait("Name", "ImageCacheServiceImageBytesTests")]
    [Trait("Category", "Cache")]
    public class ImageCacheServiceImageBytesTests : BaseTests
    {
        private const string Asin = "B008DFUGCQ";
        private static readonly byte[] ImageBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

        [Fact]
        public async Task GetImageBytesAsync_ForAStoredLibraryRelativePath_ReturnsTheFile()
        {
            using var fixture = CacheFixture.Create();
            var path = Path.Join(fixture.LibraryPath, Asin + ".jpg");
            await File.WriteAllBytesAsync(path, ImageBytes);

            var bytes = await fixture.Service.GetImageBytesAsync($"config/cache/images/library/{Asin}.jpg");

            Assert.Equal(ImageBytes, bytes);
        }

        [Fact]
        public async Task GetImageBytesAsync_ForAStoredLibraryPathWithALeadingSlash_ReturnsTheFile()
        {
            // Every writer stores the library cover as "/config/cache/images/library/<asin>.jpg".
            using var fixture = CacheFixture.Create();
            await File.WriteAllBytesAsync(Path.Join(fixture.LibraryPath, Asin + ".jpg"), ImageBytes);

            var bytes = await fixture.Service.GetImageBytesAsync($"/config/cache/images/library/{Asin}.jpg");

            Assert.Equal(ImageBytes, bytes);
        }

        [Theory]
        [InlineData("config/cache/images/library/missing.jpg")]
        [InlineData("/config/appsettings.json")]
        [InlineData("config/cache/images/library/../../../outside.jpg")]
        [InlineData("config/appsettings.json")]
        [InlineData("/etc/hostname")]
        public async Task GetImageBytesAsync_ForAPathOutsideTheImageCacheOrMissing_ReturnsNull(string reference)
        {
            using var fixture = CacheFixture.Create();
            await File.WriteAllBytesAsync(Path.Join(fixture.ContentRoot, "outside.jpg"), ImageBytes);
            await File.WriteAllBytesAsync(Path.Join(fixture.ConfigRoot, "appsettings.json"), ImageBytes);

            var bytes = await fixture.Service.GetImageBytesAsync(reference);

            Assert.Null(bytes);
        }

        [Fact]
        public async Task GetImageBytesAsync_ForAFileOverTheSizeLimit_ReturnsNull()
        {
            using var fixture = CacheFixture.Create();
            var path = Path.Join(fixture.LibraryPath, Asin + ".jpg");
            await using (var stream = File.Create(path))
            {
                stream.SetLength(10L * 1024 * 1024 + 1);
            }

            var bytes = await fixture.Service.GetImageBytesAsync($"config/cache/images/library/{Asin}.jpg");

            Assert.Null(bytes);
        }

        [Theory]
        [InlineData("http://127.0.0.1/cover.jpg")]
        [InlineData("http://localhost/cover.jpg")]
        public async Task GetImageBytesAsync_ForAnInternalUrl_RefusesBeforeAnyRequest(string url)
        {
            using var fixture = CacheFixture.Create();

            var bytes = await fixture.Service.GetImageBytesAsync(url);

            Assert.Null(bytes);
            Assert.Equal(0, fixture.Handler.Requests);
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }
        }

        private sealed class CacheFixture : IDisposable
        {
            private readonly string _root;

            private CacheFixture(string root)
            {
                _root = root;
                ContentRoot = Path.Join(root, "listenarr.api");
                ConfigRoot = Path.Join(ContentRoot, "config");
                LibraryPath = Path.Join(ConfigRoot, "cache", "images", "library");

                var paths = new Mock<IApplicationPathService>();
                paths.SetupGet(service => service.ContentRootPath).Returns(ContentRoot);
                foreach (var folder in new[] { "temp", "library", "authors", "series" })
                {
                    var resolved = Path.Join(ConfigRoot, "cache", "images", folder);
                    paths.Setup(service => service.ResolveFromConfig("cache", "images", folder)).Returns(resolved);
                }

                Handler = new CountingHandler();
                Service = new ImageCacheService(
                    Mock.Of<ILogger<ImageCacheService>>(),
                    new HttpClient(Handler),
                    paths.Object);
            }

            public string ContentRoot { get; }

            public string ConfigRoot { get; }

            public string LibraryPath { get; }

            public CountingHandler Handler { get; }

            public ImageCacheService Service { get; }

            public static CacheFixture Create() =>
                new(Path.Join(Path.GetTempPath(), "image-bytes-" + Guid.NewGuid().ToString("N")));

            public void Dispose()
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
        }
    }
}
