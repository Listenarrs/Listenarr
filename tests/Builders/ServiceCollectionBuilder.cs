using Listenarr.Api.Controllers;
using Listenarr.Api.Extensions;
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Api.Services.Search;
using Listenarr.Api.Services.Search.Filters;
using Listenarr.Api.Services.Search.Strategies;
using Listenarr.Application.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Extensions;
using Listenarr.Tests.Mocks;
using Listenarr.Tests.Mocks.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Listenarr.Tests.Builders
{
    public class ServiceCollectionBuilder
    {
        private Mock<IImportItemResolutionService> _importItemResolutionService;

        public ServiceCollectionBuilder()
        {
            _importItemResolutionService = new Mock<IImportItemResolutionService>();
            _importItemResolutionService
                .Setup(r => r.ResolveImportItemAsync(
                    It.IsAny<Download>(),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    return queueItem;
                });
        }

        public ServiceCollection Build()
        {
            var configuration = new ConfigurationManager();

            var startupConfigServiceMock = new Mock<IStartupConfigService>();
            startupConfigServiceMock
                .Setup(s => s.GetConfig())
                .Returns(new StartupConfig { AuthenticationRequired = "false" });

            var services = new ServiceCollection();
            services.AddMemoryCache();
            services.AddListenarrAppServices(configuration);
            services.AddListenarrAdapters(configuration);
            services.AddListenarrInfrastructure(options =>
                options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

            var appMetricsServiceMock = new Mock<IAppMetricsService>();
            services.AddSingleton(appMetricsServiceMock);
            services.AddSingleton(appMetricsServiceMock.Object);

            var webHostEnvironmentMock = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            services.AddSingleton(webHostEnvironmentMock);
            services.AddSingleton(webHostEnvironmentMock.Object);

            services.AddSingleton(_importItemResolutionService);
            services.AddSingleton(_importItemResolutionService.Object);

            services.AddSingleton(startupConfigServiceMock.Object);
            services.AddSingleton(new Mock<IHubContext<DownloadHub>>().Object);
            services.AddSingleton(new Mock<IDownloadHistoryService>().Object);
            services.AddSingleton(new Mock<IDiscordBotService>().Object);
            services.AddSingleton<IFfmpegService, FfmpegServiceMock>();
            services.AddSingleton<IConfigurationService, ConfigurationService>();
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            services.AddSingleton<IScanQueueService, ScanQueueService>();
            services.AddSingleton<DownloadProcessingBackgroundService>();
            services.AddSingleton<MetadataConverters>();
            services.AddSingleton<MetadataMerger>();
            services.AddSingleton<SearchProgressReporter>();
            services.AddSingleton<SearchResultFilterPipeline>();
            services.AddSingleton<MetadataStrategyCoordinator>();
            services.AddSingleton<AsinCandidateCollector>();
            services.AddSingleton<AsinEnricher>();
            services.AddSingleton<SearchResultScorer>();
            services.AddSingleton<AsinSearchHandler>();
            services.AddSingleton<DownloadService>();
            services.AddSingleton<MoveBackgroundService>();
            services.AddSingleton<MoveQueueService>();
            services.AddSingleton<LibraryController>();
            services.AddSingleton(new EphemeralDataProtectionProvider().CreateProtector("Listenarr.ConfigurationService.ProwlarrImport"));

            // Allow to retrieve specific adapters directly in the tests
            services.AddScoped<QbittorrentAdapter>();
            services.AddScoped<TransmissionAdapter>();
            services.AddScoped<SabnzbdAdapter>();
            services.AddScoped<NzbgetAdapter>();

            services.AddSingleton<AudibleApiMock>();
            services.AddSingleton<AudnexusServiceApiMock>();
            services.AddSingleton<TransmissionApiMock>();
            services.AddSingleton<SabnzbdApiMock>();
            services.AddSingleton<NzbgetApiMock>();
            services.AddSingleton<MyAnonamouseApiMock>();

            services.AddHttpClient<AudibleService>()
                .ConfigurePrimaryHttpMessageHandler<AudibleApiMock>();

            // FIXME: All classes should rely on typed HttpClient instead of named ones
            services.AddHttpClient("transmission")
                .ConfigurePrimaryHttpMessageHandler<TransmissionApiMock>();

            services.AddHttpClient("sabnzbd")
                .ConfigurePrimaryHttpMessageHandler<SabnzbdApiMock>();

            services.AddHttpClient("nzbget")
                .ConfigurePrimaryHttpMessageHandler<NzbgetApiMock>();

            services.AddHttpClient<IAudnexusService, AudnexusService>()
                .ConfigurePrimaryHttpMessageHandler<AudnexusServiceApiMock>();

            return services;
        }
    }
}
