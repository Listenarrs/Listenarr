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
using Listenarr.Api.Features.Images;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Images
{
    /// <summary>
    /// Covers are served ~3.2k at a time on the library grid; the Cache-Control
    /// lifetime is what stands between "browse from browser cache" and "refetch
    /// ~100MB every visit". Pins the 7-day policy (regression: was 1 hour) and
    /// that range processing stays enabled for the audio-preview reuse of the
    /// same result type.
    /// </summary>
    [Trait("Area", "Images")]
    [Trait("Name", "ImageResponseBuilderCacheTests")]
    public class ImageResponseBuilderCacheTests
    {
        [Fact]
        [Trait("Scenario", "CachedImage_HasWeekLongPrivateCacheControl")]
        public void CreateCachedImageResult_SetsWeekLongPrivateCacheControl()
        {
            var fileSystem = new Mock<IFileSystem>();
            var resolver = new ImagePlaceholderResolver(
                NullLogger<ImagePlaceholderResolver>.Instance, fileSystem.Object);
            var builder = new ImageResponseBuilder(
                resolver, NullLogger.Instance, contentRootPath: "/tmp");
            var headers = new HeaderDictionary();

            var result = builder.CreateCachedImageResult(
                headers, "B000TESTID", "cache/images/library/B000TESTID.jpg", "/tmp/B000TESTID.jpg");

            Assert.Equal("private, max-age=604800", headers["Cache-Control"].ToString());
            var file = Assert.IsType<PhysicalFileResult>(result);
            Assert.True(file.EnableRangeProcessing);
            Assert.Equal("image/jpeg", file.ContentType);
        }

        [Fact]
        [Trait("Scenario", "Placeholder_KeepsShortPublicCacheControl")]
        public void CreatePlaceholderResult_KeepsShortPublicCacheControl()
        {
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
            var resolver = new ImagePlaceholderResolver(
                NullLogger<ImagePlaceholderResolver>.Instance, fileSystem.Object);
            var builder = new ImageResponseBuilder(
                resolver, NullLogger.Instance, contentRootPath: "/tmp");
            var headers = new HeaderDictionary();

            // No placeholder file on disk → redirect branch; header policy is
            // what matters: placeholders stay short-lived so a later-cached
            // real cover replaces them quickly.
            builder.CreatePlaceholderResult(
                headers, new PathString("/api/v1/images/B000TESTID"), "identifier", "B000TESTID", "not found");

            Assert.Equal("public, max-age=300", headers["Cache-Control"].ToString());
        }
    }
}
