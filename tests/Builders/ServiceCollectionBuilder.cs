using Listenarr.Application.Search.Filters;
using Listenarr.Application.Search.Strategies;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Infrastructure.DependencyInjection.Downloads;
using Listenarr.Infrastructure.Downloads.Blocklist;
using Listenarr.Infrastructure.Downloads.DirectDownload;
using Listenarr.Infrastructure.HostedServices;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Listenarr.Tests.Mocks.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Listenarr.Tests.Builders
{
    /// <summary>
    /// Mock the following by default:
    /// - IDownloadItemService
    /// </summary>
    public class ServiceCollectionBuilder
    {
        private string? _contentRootPath;
        private readonly List<ServiceDescriptor> _serviceDescriptors = new();
        private readonly List<Type> _serviceTypesToRemove = new();

        public ServiceCollectionBuilder()
        {
        }

        public ServiceCollectionBuilder WithContentRootPath(string? contentRootPath)
        {
            _contentRootPath = contentRootPath;

            return this;
        }

        public ServiceCollectionBuilder WithMocks(params ServiceDescriptor[] serviceDescriptors)
        {
            return WithMocks((IEnumerable<ServiceDescriptor>)serviceDescriptors);
        }

        public ServiceCollectionBuilder WithMocks(IEnumerable<ServiceDescriptor> serviceDescriptors)
        {
            _serviceDescriptors.AddRange(serviceDescriptors);

            return this;
        }

        public ServiceCollectionBuilder WithSingleton<TService>(TService implementationInstance)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Singleton(implementationInstance));

            return this;
        }

        public ServiceCollectionBuilder WithSingleton<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            _serviceDescriptors.Add(ServiceDescriptor.Singleton<TService, TImplementation>());

            return this;
        }

        public ServiceCollectionBuilder WithSingleton<TService>(Func<IServiceProvider, TService> implementationFactory)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Singleton(implementationFactory));

            return this;
        }

        public ServiceCollectionBuilder WithScoped<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            _serviceDescriptors.Add(ServiceDescriptor.Scoped<TService, TImplementation>());

            return this;
        }

        public ServiceCollectionBuilder WithScoped<TService>(Func<IServiceProvider, TService> implementationFactory)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Scoped(implementationFactory));

            return this;
        }

        public ServiceCollectionBuilder WithTransient<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            _serviceDescriptors.Add(ServiceDescriptor.Transient<TService, TImplementation>());

            return this;
        }

        public ServiceCollectionBuilder WithTransient<TService>(Func<IServiceProvider, TService> implementationFactory)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Transient(implementationFactory));

            return this;
        }

        public ServiceCollectionBuilder Without<TService>()
        {
            return Without(typeof(TService));
        }

        public ServiceCollectionBuilder Without(params Type[] serviceTypes)
        {
            _serviceTypesToRemove.AddRange(serviceTypes);

            return this;
        }

        public ServiceCollection Build(ServiceCollection? services = null)
        {
            services ??= BuildServices();
            ApplyOverrides(services);

            return services;
        }

        private ServiceCollection BuildServices()
        {
            var configuration = new ConfigurationManager();

            var startupConfigServiceMock = new Mock<IStartupConfigService>();
            startupConfigServiceMock
                .Setup(s => s.GetConfig())
                .Returns(new StartupConfig { AuthenticationRequired = "false" });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IWorkerCycleRunner, WorkerCycleRunner>();
            services.AddListenarrAppServices(configuration);
            services.AddListenarrAdapters(configuration);
            services.AddListenarrHttpClients(configuration);
            services.AddListenarrInfrastructure(
                options => options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()),
                contentRootPath: _contentRootPath);
            var filesystemReadiness = new LibraryFilesystemReadiness();
            filesystemReadiness.MarkReady();
            services.Replace(ServiceDescriptor.Singleton(filesystemReadiness));
            services.Replace(
                ServiceDescriptor.Scoped<ILibraryAddCommitStore, InMemoryLibraryAddCommitStore>());

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
            // Real implementation, not a mock: the blocklist is only meaningful when the
            // search path can actually read back what a failure wrote.
            services.AddScoped<IBlocklistService, BlocklistService>();
            services.AddSingleton(new Mock<IDiscordBotService>().Object);
            services.AddSingleton<IFfmpegService, FfmpegServiceMock>();
            services.AddSingleton<IConfigurationService, ConfigurationService>();
            services.AddSingleton<IAudiobookFilesystemDeleteService, AudiobookFilesystemDeleteService>();
            services.AddSingleton<IFileSystemSemanticsResolver, FileSystemSemanticsResolver>();
            services.AddSingleton(new Mock<IRootFolderRelocationService>().Object);
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            services.AddSingleton<IScanQueueService, ScanQueueService>();
            services.AddSingleton<IRootFolderService, RootFolderService>();
            services.AddSingleton<IAudiobookDestinationRewriteService, AudiobookDestinationRewriteService>();
            services.AddSingleton<MetadataConverters>();
            services.AddSingleton<MetadataMerger>();
            services.AddSingleton<SearchProgressReporter>();
            services.AddSingleton<IndexerAdditionalSettingsParser>();
            services.AddSingleton<IndexerSearchWorkflow>();
            services.AddSingleton<MetadataSourceCatalog>();
            services.AddSingleton<SearchResultFilterPipeline>();
            services.AddSingleton<MetadataStrategyCoordinator>();
            services.AddSingleton<AsinCandidateCollector>();
            services.AddSingleton<AsinEnricher>();
            services.AddSingleton<SearchResultScorerService>();
            services.AddSingleton<SearchResultSortingService>();
            services.AddSingleton<AsinSearchHandler>();
            services.AddSingleton<DownloadTypeResolver>();
            services.AddSingleton<DownloadClientSelector>();
            services.AddSingleton<DownloadCachedTorrentStore>();
            services.AddSingleton<LibraryMetadataRescanWorkflow>();
            services.AddSingleton<LibraryScanPathResolver>();
            services.AddSingleton<LibraryScanQueueWorkflow>();
            services.AddSingleton<LibraryAddWorkflow>();
            services.AddSingleton<LibraryManualScanWorkflow>();
            services.AddSingleton<LibraryBulkEditWorkflow>();
            services.AddSingleton<LibraryMoveWorkflow>();
            services.AddSingleton<LibraryDeleteWorkflow>();
            services.AddSingleton<LibraryUpdateWorkflow>();
            services.AddSingleton<LibraryIdentifierWorkflow>();
            services.AddSingleton<LibraryPreviewPathWorkflow>();
            services.AddSingleton<LibraryQueryWorkflow>();
            services.AddSingleton<LibraryRenameWorkflow>();
            services.AddSingleton<SearchResponseMapper>();
            services.AddSingleton<ImagePlaceholderResolver>();
            services.AddSingleton<IndexerTestWorkflow>();
            services.AddSingleton<ProwlarrIndexerUpsertWorkflow>();
            services.AddSingleton<ManualImportPathPlanner>();
            services.AddSingleton<ManualImportCompanionImporter>();
            services.AddSingleton<AudibleAuthorPageCollector>();
            services.AddSingleton<AudibleSimpleLookupWorkflow>();
            services.AddSingleton<AudibleAuthorSearchWorkflow>();
            services.AddSingleton<DownloadService>();
            services.AddSingleton<ScanJobProcessor>();
            services.AddSingleton<IScanJobProcessor>(sp => sp.GetRequiredService<ScanJobProcessor>());
            services.AddSingleton<AudiobookContentMoveService>();
            services.AddSingleton<MoveJobProcessor>();
            services.AddSingleton<IMoveJobProcessor>(sp => sp.GetRequiredService<MoveJobProcessor>());
            services.AddSingleton<MoveBackgroundService>();
            services.AddSingleton<MoveQueueService>();
            services.AddSingleton<LibraryController>();
            services.AddSingleton<ImagesController>();
            services.AddSingleton(new EphemeralDataProtectionProvider().CreateProtector("Listenarr.ConfigurationService.ProwlarrImport"));

            services.AddSingleton<AudibleApiMock>();
            services.AddSingleton<AudnexusServiceApiMock>();
            services.AddSingleton<TransmissionApiMock>();
            services.AddSingleton<SabnzbdApiMock>();
            services.AddSingleton<NzbgetApiMock>();
            services.AddSingleton<QbittorrentApiMock>();
            services.AddSingleton(_ => new MyAnonamouseApiMock
            {
                FailOnUnexpectedCalls = true
            });

            services.AddHttpClient<AudibleService>()
                .ConfigurePrimaryHttpMessageHandler<AudibleApiMock>();

            // FIXME: All classes should rely on typed HttpClient instead of named ones
            // TODO: Find a way to test real http client configurations (cookies, retry, security, ...)
            services.AddHttpClient("transmission")
                .ConfigurePrimaryHttpMessageHandler<TransmissionApiMock>();

            services.AddHttpClient("sabnzbd")
                .ConfigurePrimaryHttpMessageHandler<SabnzbdApiMock>();

            services.AddHttpClient("nzbget")
                .ConfigurePrimaryHttpMessageHandler<NzbgetApiMock>();

            services.AddHttpClient("qbittorrent")
                .ConfigurePrimaryHttpMessageHandler<QbittorrentApiMock>();

            services.AddHttpClient("DirectDownload");

            services.AddHttpClient(DownloadRegistrationExtensions.MyAnonamouseTorrentClientName)
                .ConfigurePrimaryHttpMessageHandler<MyAnonamouseApiMock>();

            services.AddHttpClient<IAudnexusService, AudnexusService>()
                .ConfigurePrimaryHttpMessageHandler<AudnexusServiceApiMock>();

            services.AddSingleton<IDownloadClientAdapter, DownloadCLientAdapterMock>();
            services.AddSingleton<IDirectDownloadImportSourceResolver, DirectDownloadImportSourceResolver>();

            // Background services
            services.AddSingleton<DownloadMonitorProcessor>();
            services.AddSingleton<IDownloadMonitorProcessor>(sp => sp.GetRequiredService<DownloadMonitorProcessor>());
            services.AddSingleton<DownloadMonitorService>();
            services.AddSingleton<DirectDownloadProcessor>();
            services.AddSingleton<IDirectDownloadProcessor>(sp => sp.GetRequiredService<DirectDownloadProcessor>());
            services.AddSingleton<DirectDownloadService>();
            services.AddSingleton<DownloadProcessingJobProcessor>();
            services.AddSingleton<IDownloadImportProcessor>(sp => sp.GetRequiredService<DownloadProcessingJobProcessor>());
            services.AddSingleton<DownloadProcessingJobCleanupProcessor>();
            services.AddSingleton<IDownloadProcessingJobCleanupProcessor>(sp => sp.GetRequiredService<DownloadProcessingJobCleanupProcessor>());
            services.AddSingleton<DownloadProcessingJobCleanupService>();

            return services;
        }

        private void ApplyOverrides(ServiceCollection services)
        {
            foreach (var serviceDescriptor in _serviceDescriptors)
            {
                services.RemoveAll(serviceDescriptor.ServiceType);
            }

            foreach (var serviceType in _serviceTypesToRemove)
            {
                services.RemoveAll(serviceType);
            }

            foreach (var serviceDescriptor in _serviceDescriptors)
            {
                services.Add(serviceDescriptor);
            }
        }
    }
}
