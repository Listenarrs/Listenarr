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
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Listenarr.Application.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            var services = new ServiceCollection();
            services.AddSingleton<IDownloadRepository>(_downloadRepository);
            services.AddSingleton<IConfigurationService>(configMock.Object);
            var serviceProvider = services.BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

            var clientProxyMock = new Mock<IClientProxy>();
            clientProxyMock
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var hubClientsMock = new Mock<IHubClients>();
            hubClientsMock.SetupGet(h => h.All).Returns(clientProxyMock.Object);

            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

            var loggerMock = new Mock<ILogger<DownloadMonitorService>>();
            var httpFactoryMock = new Mock<IHttpClientFactory>();
            using var httpClient = new HttpClient(new HttpClientHandler());
            httpFactoryMock
                .Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(httpClient);

            var monitor = new DownloadMonitorService(
                scopeFactory,
                hubContextMock.Object,
                loggerMock.Object,
                httpFactoryMock.Object);

            var method = typeof(DownloadMonitorService).GetMethod("MonitorDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task?)method!.Invoke(monitor, new object[] { CancellationToken.None });
            Assert.NotNull(task);
            await task!;

            configMock.Verify(c => c.GetDownloadClientConfigurationsAsync(), Times.Once);
            configMock.Verify(c => c.GetDownloadClientConfigurationAsync(It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData(DownloadStatus.Completed)]
        [InlineData(DownloadStatus.ImportPending)]
        [Trait("Scenario", "UnfinalizedCompletedStatesRemainImportCandidates")]
        public async Task MonitorDownloadsAsync_UnfinalizedCompletedStates_RemainActiveImportCandidates(DownloadStatus status)
        {
            await _downloadRepository.AddAsync(new Download
            {
                Id = $"dl-{status.ToString().ToLowerInvariant()}",
                Title = "Candidate Item",
                Status = status,
                DownloadClientId = "client-1",
                FinalPath = string.Empty,
                StartedAt = DateTime.UtcNow
            });

            var clientConfig = new DownloadClientConfiguration
            {
                Id = "client-1",
                Name = "Enabled Client",
                Type = "unknown",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { clientConfig });
            configMock.Setup(c => c.GetDownloadClientConfigurationAsync("client-1"))
                .ReturnsAsync(clientConfig);

            var services = new ServiceCollection();
            services.AddSingleton<IDownloadRepository>(_downloadRepository);
            services.AddSingleton<IConfigurationService>(configMock.Object);
            var serviceProvider = services.BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

            var clientProxyMock = new Mock<IClientProxy>();
            clientProxyMock
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var hubClientsMock = new Mock<IHubClients>();
            hubClientsMock.SetupGet(h => h.All).Returns(clientProxyMock.Object);

            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

            var loggerMock = new Mock<ILogger<DownloadMonitorService>>();
            var httpFactoryMock = new Mock<IHttpClientFactory>();
            using var httpClient = new HttpClient(new HttpClientHandler());
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var monitor = new DownloadMonitorService(
                scopeFactory,
                hubContextMock.Object,
                loggerMock.Object,
                httpFactoryMock.Object);

            var method = typeof(DownloadMonitorService).GetMethod("MonitorDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task?)method!.Invoke(monitor, new object[] { CancellationToken.None });
            Assert.NotNull(task);
            await task!;

            configMock.Verify(c => c.GetDownloadClientConfigurationAsync("client-1"), Times.Once);
        }
    }
}
