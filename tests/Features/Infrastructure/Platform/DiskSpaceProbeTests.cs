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

using Listenarr.Infrastructure.Platform;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Platform
{
    [Trait("Area", "Infrastructure")]
    [Trait("Name", "DiskSpaceProbeTests")]
    [Trait("Category", "DiskSpaceProbe")]
    public class DiskSpaceProbeTests : BaseTests
    {
        private static DiskSpaceProbe CreateProbe()
            => new DiskSpaceProbe(NullLogger<DiskSpaceProbe>.Instance);

        [Fact]
        [Trait("Method", "TryGetDiskSpace")]
        [Trait("Scenario", "ExistingDirectory")]
        public void TryGetDiskSpace_ExistingDirectory_ReturnsMeasuredBytes()
        {
            // Given: a real directory (the per-test temp folder) on the live filesystem.
            // This exercises the non-Windows DriveInfo branch on Linux CI.
            var probe = CreateProbe();
            var path = FileService.GetTempPath();

            // When
            var measured = probe.TryGetDiskSpace(path, out var totalBytes, out var freeBytes);

            // Then
            Assert.True(measured);
            Assert.True(totalBytes > 0);
            Assert.True(freeBytes >= 0);
            Assert.True(freeBytes <= totalBytes);
        }

        [Fact]
        [Trait("Method", "TryGetDiskSpace")]
        [Trait("Scenario", "MissingDirectory")]
        public void TryGetDiskSpace_MissingDirectory_ReturnsFalse()
        {
            // Given: a path that does not exist
            var probe = CreateProbe();
            var path = Path.Join(FileService.GetTempPath(), "does-not-exist");

            // When
            var measured = probe.TryGetDiskSpace(path, out var totalBytes, out var freeBytes);

            // Then
            Assert.False(measured);
            Assert.Equal(0, totalBytes);
            Assert.Equal(0, freeBytes);
        }
    }
}
