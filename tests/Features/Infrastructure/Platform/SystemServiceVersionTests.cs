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

namespace Listenarr.Tests.Features.Infrastructure.Platform
{
    [Trait("Area", "Infrastructure")]
    [Trait("Name", "SystemServiceVersionTests")]
    [Trait("Category", "SystemService")]
    public class SystemServiceVersionTests
    {
        [Fact]
        [Trait("Method", "GetSystemInfo")]
        [Trait("Scenario", "UsesHostApplicationVersion")]
        public void GetSystemInfo_UsesHostApplicationVersion()
        {
            var systemService = CreateSystemService();

            var systemInfo = systemService.GetSystemInfo();

            Assert.Equal(ApplicationVersionTestUtils.GetExpectedApiVersion(), systemInfo.Version);
            Assert.NotEqual("1.0.0.0", systemInfo.Version);
        }

        [Fact]
        [Trait("Method", "GetServiceHealthAsync")]
        [Trait("Scenario", "UsesHostApplicationVersion")]
        public async Task GetServiceHealthAsync_UsesHostApplicationVersion()
        {
            var systemService = CreateSystemService();

            var serviceHealth = await systemService.GetServiceHealthAsync();

            Assert.Equal(ApplicationVersionTestUtils.GetExpectedApiVersion(), serviceHealth.Version);
            Assert.NotEqual("1.0.0.0", serviceHealth.Version);
        }

        private static SystemService CreateSystemService()
        {
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(service => service.GetApiConfigurationsAsync())
                .ReturnsAsync(new List<ApiConfiguration>());
            configurationService
                .Setup(service => service.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>());

            var applicationPathService = new Mock<IApplicationPathService>();

            var applicationVersionService = new Mock<IApplicationVersionService>();
            applicationVersionService
                .Setup(service => service.Resolve())
                .Returns(ApplicationVersionTestUtils.GetExpectedApiVersion());

            var rootFolderService = new Mock<IRootFolderService>();
            rootFolderService
                .Setup(service => service.GetAllAsync())
                .ReturnsAsync(new List<RootFolder>());

            return new SystemService(
                configurationService.Object,
                NullLogger<SystemService>.Instance,
                applicationPathService.Object,
                applicationVersionService.Object,
                rootFolderService.Object,
                new DiskSpaceProbe(NullLogger<DiskSpaceProbe>.Instance));
        }
    }
}
