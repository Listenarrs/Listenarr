using Listenarr.Api.Controllers;
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Common;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Application.Notification;
using Listenarr.Application.Search;
using Listenarr.Application.Search.Filters;
using Listenarr.Application.Search.Strategies;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Extensions;
using Listenarr.Infrastructure.FileSystem;
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
    /// <summary>
    /// Mock the following by default:
    /// - IDownloadItemService
    /// </summary>
    public class ServiceCollectionBuilder
    {
        public ServiceCollectionBuilder()
        {
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

            var hubClientsMock = new Mock<IHubClients>();
            var clientProxyMock = new Mock<IClientProxy>();

            hubClientsMock.Setup(clients => clients.All).Returns(clientProxyMock.Object);

            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.Setup(x => x.Clients).Returns(hubClientsMock.Object);
            services.AddSingleton(hubContextMock.Object);

            services.AddSingleton(startupConfigServiceMock.Object);
            services.AddSingleton(new Mock<IDownloadHistoryService>().Object);
            services.AddSingleton(new Mock<IDiscordBotService>().Object);
            services.AddSingleton<IFfmpegService, FfmpegServiceMock>();
            services.AddSingleton<IConfigurationService, ConfigurationService>();
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            services.AddSingleton<IScanQueueService, ScanQueueService>();
            services.AddSingleton<IRootFolderService, RootFolderService>();
            services.AddSingleton<MetadataConverters>();
            services.AddSingleton<MetadataMerger>();
            services.AddSingleton<SearchProgressReporter>();
            services.AddSingleton<SearchResultFilterPipeline>();
            services.AddSingleton<MetadataStrategyCoordinator>();
            services.AddSingleton<AsinCandidateCollector>();
            services.AddSingleton<AsinEnricher>();
            services.AddSingleton<SearchResultScorerService>();
            services.AddSingleton<AsinSearchHandler>();
            services.AddSingleton<DownloadService>();
            services.AddSingleton<MoveBackgroundService>();
            services.AddSingleton<MoveQueueService>();
            services.AddSingleton<LibraryController>();
            services.AddSingleton(new EphemeralDataProtectionProvider().CreateProtector("Listenarr.ConfigurationService.ProwlarrImport"));

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

            services.AddSingleton<IDownloadClientAdapter, DownloadCLientAdapterMock>();

            // Background services
            services.AddSingleton<DownloadMonitorService>(); // FIXME: This should be a processor
            services.AddSingleton<DownloadProcessingJobProcessor>();

            return services;
        }
    }
}
