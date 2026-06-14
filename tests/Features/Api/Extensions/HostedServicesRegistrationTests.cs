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
        private static readonly string[] ExpectedHostedServiceNames =
        [
            nameof(ScanBackgroundService),
            nameof(MoveBackgroundService),
            nameof(ImageCacheCleanupService),
            nameof(DownloadMonitorService),
            nameof(MovedDownloadProcessor),
            nameof(QueueMonitorService),
            nameof(AutomaticSearchService),
            nameof(AuthorMonitoringBackgroundService),
            nameof(SeriesMonitoringBackgroundService),
            nameof(FfmpegInstallBackgroundService),
            nameof(MetadataRescanService),
            nameof(DownloadProcessingJobProcessor),
            nameof(UnmatchedScanBackgroundService)
        ];

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
            AssertHostedServiceRegistered<ImageCacheCleanupService>(services);
            AssertHostedServiceRegistered<DownloadMonitorService>(services);
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(QueueMonitorService));
            AssertHostedServiceRegistered<AutomaticSearchService>(services);
            AssertHostedServiceRegistered<AuthorMonitoringBackgroundService>(services);
            AssertHostedServiceRegistered<SeriesMonitoringBackgroundService>(services);
            AssertHostedServiceRegistered<FfmpegInstallBackgroundService>(services);
            AssertHostedServiceRegistered<MetadataRescanService>(services);
            AssertHostedServiceRegistered<DownloadProcessingJobProcessor>(services);
            AssertHostedServiceRegistered<UnmatchedScanBackgroundService>(services);

            // Assert - singletons / supporting services registered
            Assert.Contains(services, d => d.ServiceType == typeof(IScanQueueService) && d.Lifetime == ServiceLifetime.Singleton);
            Assert.Contains(services, d => d.ServiceType == typeof(IMoveQueueService) && d.Lifetime == ServiceLifetime.Singleton);
            Assert.Contains(services, d => d.ServiceType == typeof(IWorkerCycleRunner) && d.Lifetime == ServiceLifetime.Singleton);

            AssertProcessorRegistered<IDownloadMonitorProcessor>(services);
            AssertProcessorRegistered<IDownloadImportProcessor>(services);
            AssertProcessorRegistered<IMovedDownloadCleanupProcessor>(services);
            AssertProcessorRegistered<IAutomaticSearchProcessor>(services);
            AssertProcessorRegistered<IAuthorMonitoringProcessor>(services);
            AssertProcessorRegistered<ISeriesMonitoringProcessor>(services);
            AssertProcessorRegistered<IMetadataRescanProcessor>(services);
            AssertProcessorRegistered<IImageCacheCleanupProcessor>(services);
            AssertProcessorRegistered<IFfmpegInstallProcessor>(services);
            AssertProcessorRegistered<IUnmatchedScanProcessor>(services);
        }

        [Fact]
        public void BackgroundWorkerOwnership_DocumentsEveryHostedService()
        {
            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            services.AddListenarrHostedServices(config);

            var architectureDoc = File.ReadAllText(FindRepositoryFile("BACKEND_ARCHITECTURE.md"));
            foreach (var hostedServiceName in ExpectedHostedServiceNames)
            {
                Assert.Contains($"`{hostedServiceName}`", architectureDoc);
            }
        }

        private static void AssertHostedServiceRegistered<TImplementation>(IEnumerable<ServiceDescriptor> services)
            where TImplementation : IHostedService
        {
            Assert.Contains(services, d =>
                d.ServiceType == typeof(IHostedService) &&
                (d.ImplementationType == typeof(TImplementation) || d.ImplementationFactory != null));
            Assert.Contains(services, d =>
                d.ServiceType == typeof(TImplementation) && d.Lifetime == ServiceLifetime.Singleton);
        }

        private static void AssertProcessorRegistered<TProcessor>(IEnumerable<ServiceDescriptor> services)
        {
            Assert.Contains(services, d => d.ServiceType == typeof(TProcessor) && d.Lifetime == ServiceLifetime.Singleton);
        }

        private static string FindRepositoryFile(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Join(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not find {fileName} from {AppContext.BaseDirectory}");
        }
    }
}
