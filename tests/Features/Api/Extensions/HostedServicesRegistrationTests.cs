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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Listenarr.Infrastructure.Extensions;
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Infrastructure.Ffmpeg;

namespace Listenarr.Tests.Features.Api.Extensions
{
    public class HostedServicesRegistrationTests
    {
        [Fact]
        public void AddListenarrHostedServices_RegistersHostedServicesAndSingletons()
        {
            // Arrange
            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            // Act
            services.AddListenarrHostedServices(config);

            // Assert - hosted services registered
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ScanBackgroundService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MoveBackgroundService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ImageCacheCleanupService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DownloadMonitorService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(QueueMonitorService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AutomaticSearchService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AuthorMonitoringBackgroundService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SeriesMonitoringBackgroundService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(FfmpegInstallBackgroundService));
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MetadataRescanService));

            // Assert - singletons / supporting services registered
            Assert.Contains(services, d => d.ServiceType == typeof(IScanQueueService) && d.Lifetime == ServiceLifetime.Singleton);
            Assert.Contains(services, d => d.ServiceType == typeof(IMoveQueueService) && d.Lifetime == ServiceLifetime.Singleton);
        }
    }
}
