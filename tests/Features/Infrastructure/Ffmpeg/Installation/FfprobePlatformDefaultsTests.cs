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
using System.Runtime.InteropServices;
using Listenarr.Tests.Common;

// Aliased because `Listenarr.Tests.Features.Architecture` is an enclosing namespace here, and a
// namespace member wins over the type of the same name.
using Arch = System.Runtime.InteropServices.Architecture;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    [Trait("Name", "FfprobePlatformDefaultsTests")]
    [Trait("Category", "FfmpegService")]
    public class FfprobePlatformDefaultsTests : BaseTests
    {
        // Suffixes FfprobeArchiveExtractor dispatches on. A URL outside this set downloads fine and
        // then extracts nothing, which is the failure mode that hid the macOS bug: the install log
        // shows a successful download and no binary ever appears.
        private static readonly string[] ExtractableSuffixes = [".zip", ".tar.xz", ".tar.gz", ".tgz"];

        public static TheoryData<OSPlatform, Arch> AllPlatforms() => new()
        {
            { OSPlatform.Linux, Arch.X64 },
            { OSPlatform.Linux, Arch.Arm64 },
            { OSPlatform.OSX, Arch.X64 },
            { OSPlatform.Windows, Arch.X64 },
        };

        [Theory]
        [MemberData(nameof(AllPlatforms))]
        [Trait("Method", "GetDownloadUrl")]
        public void GetDownloadUrl_ReturnsAnArchiveTheExtractorCanOpen(
            OSPlatform platform,
            Arch architecture)
        {
            var url = FfprobePlatformDefaults.GetDownloadUrl(platform, architecture);

            Assert.NotNull(url);
            Assert.Contains(
                ExtractableSuffixes,
                suffix => url!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        [Trait("Method", "GetDownloadUrl")]
        public void GetDownloadUrl_ForMacOs_PointsAtTheFfprobeArchive()
        {
            // evermeet ships a separate archive per binary, so the ffmpeg archive contains no
            // ffprobe at all and the install can never succeed on macOS.
            var url = FfprobePlatformDefaults.GetDownloadUrl(OSPlatform.OSX, Arch.X64);

            Assert.NotNull(url);
            var fileName = url!.Split('/')[^1];
            Assert.Contains("ffprobe", fileName, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [MemberData(nameof(AllPlatforms))]
        [Trait("Method", "GetDownloadUrl")]
        public void GetDownloadUrl_IsServedOverHttps(OSPlatform platform, Arch architecture)
        {
            var url = FfprobePlatformDefaults.GetDownloadUrl(platform, architecture);

            Assert.NotNull(url);
            Assert.StartsWith("https://", url, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "GetDownloadUrl")]
        public void GetDownloadUrl_ForLinux_DistinguishesArm64FromX64()
        {
            var x64 = FfprobePlatformDefaults.GetDownloadUrl(OSPlatform.Linux, Arch.X64);
            var arm64 = FfprobePlatformDefaults.GetDownloadUrl(OSPlatform.Linux, Arch.Arm64);

            Assert.NotEqual(x64, arm64);
            Assert.Contains("arm64", arm64!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "GetDownloadUrl")]
        public void GetDownloadUrl_ForAnUnsupportedPlatform_ReturnsNull()
        {
            Assert.Null(FfprobePlatformDefaults.GetDownloadUrl(OSPlatform.FreeBSD, Arch.X64));
        }

        [Fact]
        [Trait("Method", "GetDownloadUrl")]
        public void GetDownloadUrl_WithoutArguments_MatchesTheHostPlatform()
        {
            var expected = FfprobePlatformDefaults.GetDownloadUrl(
                OperatingSystem.IsWindows() ? OSPlatform.Windows
                    : OperatingSystem.IsMacOS() ? OSPlatform.OSX
                    : OSPlatform.Linux,
                RuntimeInformation.OSArchitecture);

            Assert.Equal(expected, FfprobePlatformDefaults.GetDownloadUrl());
        }
    }
}
