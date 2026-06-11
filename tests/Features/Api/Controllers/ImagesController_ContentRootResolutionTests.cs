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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class ImagesController_ContentRootResolutionTests : BaseTests
    {
        private string _tempRoot = string.Empty;
        private readonly Mock<IImageCacheService> _imageCache = new();
        private readonly Mock<IApplicationPathService> _mockPathService = new();

        public override async Task InitializeAsync()
        {
            _tempRoot = FileService.GetTempDirectory("images-controller-content-root");
            _mockPathService.SetupGet(p => p.ContentRootPath).Returns(_tempRoot);

            _services.AddSingleton(_imageCache.Object);
            _services.AddSingleton(_mockPathService.Object);
            Init();

            await base.InitializeAsync();
        }

        [Fact]
        public async Task GetImage_UsesApplicationPathServiceContentRoot()
        {
            const string identifier = "ZZTEST1234";

            Directory.CreateDirectory(Path.Join(_tempRoot, "config", "cache", "images", "authors"));

            var relativePath = $"config/cache/images/authors/{identifier}.jpg";
            var expectedPath = Path.Join(_tempRoot, "config", "cache", "images", "authors", $"{identifier}.jpg");
            await File.WriteAllBytesAsync(expectedPath, new byte[] { 1, 2, 3, 4 });

            _imageCache
                .Setup(service => service.GetCachedImagePathAsync(identifier))
                .ReturnsAsync(relativePath);

            var controller = _provider.GetRequiredService<ImagesController>();
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            var effectiveRootField = typeof(ImagesController).GetField("_effectiveContentRootPath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(effectiveRootField);
            Assert.Equal(_tempRoot, effectiveRootField!.GetValue(controller));

            var result = await controller.GetImage(identifier);

            var fileResult = Assert.IsType<PhysicalFileResult>(result);
            var normalizedActualPath = fileResult.FileName.Replace('/', Path.DirectorySeparatorChar);
            Assert.Equal(expectedPath, normalizedActualPath);
        }
    }
}
