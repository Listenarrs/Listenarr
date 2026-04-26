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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using System.Reflection;
using Listenarr.Application.Repositories;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class ImagesController_ContentRootResolutionTests
    {
        [Fact]
        public async Task GetImage_UsesRepoRoot_WhenEnvironmentContentRootPointsToBinOutput()
        {
            var originalEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-images-controller-tests", Guid.NewGuid().ToString("N"));
            const string identifier = "ZZTEST1234";

            try
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

                var repoApiRoot = Path.Join(tempRoot, "listenarr.api");
                var binRoot = Path.Join(repoApiRoot, "bin", "Debug", "net8.0");
                Directory.CreateDirectory(Path.Join(repoApiRoot, "config", "cache", "images", "authors"));
                Directory.CreateDirectory(Path.Join(repoApiRoot, "wwwroot"));
                Directory.CreateDirectory(binRoot);
                File.WriteAllText(Path.Join(repoApiRoot, "listenarr.api.csproj"), "<Project />");

                var relativePath = $"config/cache/images/authors/{identifier}.jpg";
                var expectedPath = Path.Join(repoApiRoot, "config", "cache", "images", "authors", $"{identifier}.jpg");
                await File.WriteAllBytesAsync(expectedPath, new byte[] { 1, 2, 3, 4 });

                var imageCache = new Mock<IImageCacheService>();
                imageCache
                    .Setup(service => service.GetCachedImagePathAsync(identifier))
                    .ReturnsAsync(relativePath);

                var env = new Mock<IWebHostEnvironment>();
                env.SetupGet(environment => environment.ContentRootPath).Returns(binRoot);

                using var httpClientForAudible = new System.Net.Http.HttpClient();
                var controller = new ImagesController(
                    imageCache.Object,
                    Mock.Of<IAudiobookMetadataService>(),
                    new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false }.Object,
                    Mock.Of<IAudnexusService>(),
                    Mock.Of<IAudiobookRepository>(),
                    Mock.Of<ILogger<ImagesController>>(),
                    env.Object);
                controller.ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                };

                var effectiveRootField = typeof(ImagesController).GetField("_effectiveContentRootPath", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(effectiveRootField);
                Assert.Equal(repoApiRoot, effectiveRootField!.GetValue(controller));

                var result = await controller.GetImage(identifier);

                var fileResult = Assert.IsType<PhysicalFileResult>(result);
                var normalizedActualPath = fileResult.FileName.Replace('/', Path.DirectorySeparatorChar);
                Assert.Equal(expectedPath, normalizedActualPath);
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
