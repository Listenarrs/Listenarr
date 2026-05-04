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
using System.Reflection;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Area", "DownloadMonitoring")]
    public class DownloadMonitorServiceTests : BaseTests
    {
        private static ListenArrDbContext CreateInMemoryDb()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new ListenArrDbContext(options);
        }

        [Fact]
        public async Task MonitorDownloadsAsync_SkipsClientPolling_WhenNoEnabledDownloadClientsConfigured()
        {
            await _downloadRepository.AddAsync(new Download
            {
                Id = "dl-no-clients",
                Title = "No Clients Configured",
                Status = DownloadStatus.Downloading,
                DownloadClientId = "missing-client-id",
                StartedAt = DateTime.UtcNow
            });

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync()).ReturnsAsync(new List<DownloadClientConfiguration>());
            configMock.Setup(c => c.GetDownloadClientConfigurationAsync(It.IsAny<string>())).ReturnsAsync((DownloadClientConfiguration?)null);
            _services.AddSingleton(configMock.Object);

            var clientProxyMock = new Mock<IClientProxy>();
            clientProxyMock
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _services.AddSingleton(clientProxyMock.Object);
            Init();

            var monitor = _provider.GetRequiredService<DownloadMonitorService>();

            var method = typeof(DownloadMonitorService).GetMethod("MonitorDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task?)method!.Invoke(monitor, new object[] { CancellationToken.None });
            Assert.NotNull(task);
            await task!;

            configMock.Verify(c => c.GetDownloadClientConfigurationsAsync(), Times.Once);
            configMock.Verify(c => c.GetDownloadClientConfigurationAsync(It.IsAny<string>()), Times.Never);
        }
    }
}
