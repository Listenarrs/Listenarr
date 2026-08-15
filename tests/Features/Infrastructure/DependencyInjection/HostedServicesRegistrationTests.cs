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
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Infrastructure.Downloads.DirectDownload;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.DependencyInjection
{
    public class HostedServicesRegistrationTests
    {
        private static readonly string[] ExpectedHostedServiceNames =
        [
            nameof(ScanBackgroundService),
            nameof(MoveBackgroundService),
            nameof(ImageCacheCleanupService),
            nameof(DownloadMonitorService),
            nameof(DirectDownloadService),
            nameof(MovedDownloadCleanupService),
            nameof(QueueMonitorService),
            nameof(AutomaticSearchService),
            nameof(AuthorMonitoringBackgroundService),
            nameof(SeriesMonitoringBackgroundService),
            nameof(FfmpegInstallBackgroundService),
            nameof(MetadataRescanService),
            nameof(DownloadProcessingJobProcessor),
            nameof(DownloadProcessingJobCleanupService),
            nameof(UnmatchedScanBackgroundService)
        ];

        private static readonly Type[] ExpectedProcessorTypes =
        [
            typeof(DownloadMonitorProcessor),
            typeof(DirectDownloadProcessor),
            typeof(DownloadProcessingJobProcessor),
            typeof(DownloadProcessingJobCleanupProcessor),
            typeof(MovedDownloadCleanupProcessor),
            typeof(ScanJobProcessor),
            typeof(MoveJobProcessor),
            typeof(QueueMonitorProcessor),
            typeof(AutomaticSearchProcessor),
            typeof(AuthorMonitoringProcessor),
            typeof(SeriesMonitoringProcessor),
            typeof(MetadataRescanProcessor),
            typeof(ImageCacheCleanupProcessor),
            typeof(FfmpegInstallProcessor),
            typeof(UnmatchedScanProcessor)
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
            AssertHostedServiceRegistered<DirectDownloadService>(services);
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(QueueMonitorService));
            AssertHostedServiceRegistered<AutomaticSearchService>(services);
            AssertHostedServiceRegistered<AuthorMonitoringBackgroundService>(services);
            AssertHostedServiceRegistered<SeriesMonitoringBackgroundService>(services);
            AssertHostedServiceRegistered<FfmpegInstallBackgroundService>(services);
            AssertHostedServiceRegistered<MetadataRescanService>(services);
            AssertHostedServiceRegistered<DownloadProcessingJobProcessor>(services);
            AssertHostedServiceRegistered<DownloadProcessingJobCleanupService>(services);
            AssertHostedServiceRegistered<UnmatchedScanBackgroundService>(services);

            // Assert - singletons / supporting services registered
            Assert.Contains(services, d => d.ServiceType == typeof(IScanQueueService) && d.Lifetime == ServiceLifetime.Singleton);
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(IMoveQueueService));
            Assert.Contains(services, d => d.ServiceType == typeof(IWorkerCycleRunner) && d.Lifetime == ServiceLifetime.Singleton);

            foreach (var processorType in ExpectedProcessorTypes)
            {
                Assert.Contains(services, d => d.ServiceType == processorType && d.Lifetime == ServiceLifetime.Singleton);
            }

            AssertProcessorRegistered<IDownloadMonitorProcessor>(services);
            AssertProcessorRegistered<IDirectDownloadProcessor>(services);
            AssertProcessorRegistered<IDownloadImportProcessor>(services);
            AssertProcessorRegistered<IDownloadProcessingJobCleanupProcessor>(services);
            AssertProcessorRegistered<IMovedDownloadCleanupProcessor>(services);
            AssertProcessorRegistered<IScanJobProcessor>(services);
            AssertProcessorRegistered<IMoveJobProcessor>(services);
            AssertProcessorRegistered<IAutomaticSearchProcessor>(services);
            AssertProcessorRegistered<IAuthorMonitoringProcessor>(services);
            AssertProcessorRegistered<ISeriesMonitoringProcessor>(services);
            AssertProcessorRegistered<IMetadataRescanProcessor>(services);
            AssertProcessorRegistered<IImageCacheCleanupProcessor>(services);
            AssertProcessorRegistered<IFfmpegInstallProcessor>(services);
            AssertProcessorRegistered<IUnmatchedScanProcessor>(services);
            AssertProcessorRegistered<IQueueMonitorProcessor>(services);
        }

        [Fact]
        public void AddListenarrHostedServices_DoesNotRegisterHostedServiceTwice()
        {
            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            services.AddListenarrHostedServices(config);

            var hostedServiceNames = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .Select(GetHostedServiceName)
                .ToList();

            Assert.Equal(hostedServiceNames.Count, hostedServiceNames.Distinct(StringComparer.Ordinal).Count());
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

            foreach (var processorType in ExpectedProcessorTypes)
            {
                Assert.Contains($"`{processorType.Name}`", architectureDoc);
            }
        }

        private static void AssertHostedServiceRegistered<TImplementation>(IEnumerable<ServiceDescriptor> services)
            where TImplementation : IHostedService
        {
            Assert.Contains(services, d =>
                d.ServiceType == typeof(IHostedService) &&
                GetHostedServiceName(d) == typeof(TImplementation).Name);
            Assert.Contains(services, d =>
                d.ServiceType == typeof(TImplementation) && d.Lifetime == ServiceLifetime.Singleton);
        }

        private static void AssertProcessorRegistered<TProcessor>(IEnumerable<ServiceDescriptor> services)
        {
            Assert.Contains(services, d => d.ServiceType == typeof(TProcessor) && d.Lifetime == ServiceLifetime.Singleton);
        }

        private static string GetHostedServiceName(ServiceDescriptor descriptor)
        {
            if (descriptor.ImplementationType != null)
            {
                return descriptor.ImplementationType.Name;
            }

            var factoryType = descriptor.ImplementationFactory?.Method.ReturnType;
            return factoryType?.Name ?? descriptor.ToString();
        }

        private static string FindRepositoryFile(string fileName) =>
            Path.Join(TestUtils.FindRepositoryRoot(), fileName);
    }
}
