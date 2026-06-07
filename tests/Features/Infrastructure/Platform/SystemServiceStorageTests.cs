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

using Listenarr.Application.Interfaces;
using Listenarr.Infrastructure.Platform;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Platform
{
    [Trait("Area", "Infrastructure")]
    [Trait("Name", "SystemServiceStorageTests")]
    [Trait("Category", "SystemService")]
    public class SystemServiceStorageTests : BaseTests
    {
        [Fact]
        [Trait("Method", "GetStorageInfoAsync")]
        [Trait("Scenario", "WithoutRootFolders")]
        public async Task GetStorageInfoAsync_WithoutRootFolders_ReturnsAppDataAndSystemDisks()
        {
            // Given: no root folders configured
            var systemService = CreateSystemService();

            // When
            var storageInfo = await systemService.GetStorageInfoAsync();

            // Then: the System entry first (path root — the container root filesystem in
            // Docker, e.g. docker.img on Unraid), then the App Data entry (content root)
            Assert.Equal(2, storageInfo.Disks.Count);
            var systemDisk = storageInfo.Disks[0];
            Assert.Equal("System", systemDisk.Label);
            Assert.Equal(Path.GetPathRoot(_applicationPathService.ContentRootPath), systemDisk.Path);
            Assert.Equal("available", systemDisk.Status);
            Assert.True(systemDisk.TotalBytes > 0);
            var disk = storageInfo.Disks[1];
            Assert.Equal("App Data", disk.Label);
            Assert.Equal(_applicationPathService.ContentRootPath, disk.Path);
            Assert.Equal("available", disk.Status);
            Assert.True(disk.TotalBytes > 0);
            // Legacy top-level fields keep describing the app disk
            Assert.Equal(disk.TotalBytes, storageInfo.TotalBytes);
            Assert.Equal(disk.FreeBytes, storageInfo.FreeBytes);
            Assert.Equal(disk.UsedFormatted, storageInfo.UsedFormatted);
        }

        [Fact]
        [Trait("Method", "GetStorageInfoAsync")]
        [Trait("Scenario", "WithConfigDirectory")]
        public async Task GetStorageInfoAsync_WithConfigDirectory_MeasuresConfigRoot()
        {
            // Given: the config directory exists (as it does at runtime — DB, logs and
            // cache live there; in Docker it is the mounted /app/config volume)
            Directory.CreateDirectory(_applicationPathService.ConfigRootPath);
            var systemService = CreateSystemService();

            // When
            var storageInfo = await systemService.GetStorageInfoAsync();

            // Then: the App Data entry points at the config root, not the install dir
            Assert.Equal(2, storageInfo.Disks.Count);
            Assert.Equal("System", storageInfo.Disks[0].Label);
            var disk = storageInfo.Disks[1];
            Assert.Equal("App Data", disk.Label);
            Assert.Equal(_applicationPathService.ConfigRootPath, disk.Path);
            Assert.Equal("available", disk.Status);
        }

        [Fact]
        [Trait("Method", "GetStorageInfoAsync")]
        [Trait("Scenario", "WithRootFolders")]
        public async Task GetStorageInfoAsync_WithRootFolders_AddsOneEntryPerFolder()
        {
            // Given: two root folders (deliberately on the same filesystem — no dedupe)
            var folderA = FileService.GetTempDirectory("audiobooks");
            var folderB = FileService.GetTempDirectory("podcasts");
            await _rootFolderRepository.AddAsync(
                new RootFolderBuilder().WithName("Audiobooks").WithPath(folderA).Build());
            await _rootFolderRepository.AddAsync(
                new RootFolderBuilder().WithName("Podcasts").WithPath(folderB).Build());
            var systemService = CreateSystemService();

            // When
            var storageInfo = await systemService.GetStorageInfoAsync();

            // Then: system disk and app disk first, then one entry per configured folder
            Assert.Equal(4, storageInfo.Disks.Count);
            Assert.Equal("System", storageInfo.Disks[0].Label);
            Assert.Equal("App Data", storageInfo.Disks[1].Label);
            Assert.Equal(
                new[] { "Audiobooks", "Podcasts" },
                storageInfo.Disks.Skip(2).Select(d => d.Label).OrderBy(l => l).ToArray());
            Assert.All(storageInfo.Disks, d => Assert.Equal("available", d.Status));
            Assert.All(storageInfo.Disks, d => Assert.True(d.TotalBytes > 0));
            Assert.All(storageInfo.Disks, d => Assert.False(string.IsNullOrEmpty(d.FreeFormatted)));
        }

        [Fact]
        [Trait("Method", "GetStorageInfoAsync")]
        [Trait("Scenario", "MissingRootFolderPath")]
        public async Task GetStorageInfoAsync_MissingRootFolderPath_MarksEntryUnavailable()
        {
            // Given: a root folder pointing at a path that does not exist
            var missing = Path.Join(FileService.GetTempPath(), "does-not-exist");
            await _rootFolderRepository.AddAsync(
                new RootFolderBuilder().WithName("Missing").WithPath(missing).Build());
            var systemService = CreateSystemService();

            // When: the call must not throw
            var storageInfo = await systemService.GetStorageInfoAsync();

            // Then: the bad folder is reported unavailable, the app disk is unaffected
            var entry = Assert.Single(storageInfo.Disks, d => d.Label == "Missing");
            Assert.Equal("unavailable", entry.Status);
            Assert.Equal(0, entry.TotalBytes);
            Assert.Equal("available", storageInfo.Disks[0].Status);
        }

        private SystemService CreateSystemService()
        {
            // IConfigurationService and IApplicationVersionService are not used by
            // the storage path — bare mocks, mirroring SystemServiceVersionTests.
            var configurationService = new Mock<IConfigurationService>();
            var applicationVersionService = new Mock<IApplicationVersionService>();

            return new SystemService(
                configurationService.Object,
                NullLogger<SystemService>.Instance,
                _applicationPathService,
                applicationVersionService.Object,
                _provider.GetRequiredService<IRootFolderService>());
        }
    }
}
