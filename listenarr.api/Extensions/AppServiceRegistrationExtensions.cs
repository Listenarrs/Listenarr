// csharp
using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Listenarr.Api.Services;

namespace Listenarr.Api.Extensions
{
    /// <summary>
    /// Registers application-level services (business logic, helpers, and related DI).
    /// Extracted from Program.cs to keep startup focused and composable.
    /// </summary>
    public static class AppServiceRegistrationExtensions
    {
        public static IServiceCollection AddListenarrAppServices(this IServiceCollection services, IConfiguration config)
        {
            // Core services and application logic
            services.AddScoped<IConfigurationService, ConfigurationService>();
            // Startup config: read config.json (optional) and expose via IStartupConfigService
            services.AddSingleton<IStartupConfigService, StartupConfigService>();
            
            // Register indexer search providers
            services.AddScoped<Listenarr.Api.Services.Search.Providers.IIndexerSearchProvider, Listenarr.Api.Services.Search.Providers.InternetArchiveSearchProvider>();
            services.AddScoped<Listenarr.Api.Services.Search.Providers.IIndexerSearchProvider, Listenarr.Api.Services.Search.Providers.TorznabNewznabSearchProvider>();
            services.AddScoped<Listenarr.Api.Services.Search.Providers.IIndexerSearchProvider, Listenarr.Api.Services.Search.Providers.MyAnonamouseSearchProvider>();
            
            services.AddScoped<ISearchService, SearchService>();
            services.AddScoped<IMetadataService, MetadataService>();
            services.AddScoped<IAudioFileService, AudioFileService>();
            // Metadata extraction limiter to bound concurrent ffprobe calls
            services.AddSingleton<MetadataExtractionLimiter>();
            // Ffmpeg installer: provides a bundled ffprobe binary when not present on the system
            services.AddSingleton<IFfmpegService, FfmpegInstallerService>();
            // Service to accept client-pushed download updates and maintain recent-push cache
            services.AddSingleton<DownloadPushService>();
            services.AddScoped<IAsinLookupService, AsinLookupService>();
            // Notification service for webhook notifications (typed HttpClient so a HttpClient is injected)
            services.AddHttpClient<NotificationService>();
            // Also register the concrete NotificationService type in the container so constructors
            // requesting the concrete type (rather than an interface) can resolve it. Some
            // callers (tests) request the concrete type directly when activating dependent
            // services like `DownloadService`.
            services.AddScoped<NotificationService>(sp =>
                ActivatorUtilities.CreateInstance<NotificationService>(sp));
            // Also register by interface so GetService<INotificationService>() resolves
            services.AddScoped<INotificationService>(sp => sp.GetRequiredService<NotificationService>());

            // Minimal application metrics service for telemetry/metrics counters used by tests and instrumentation
            services.AddSingleton<IAppMetricsService, NoopAppMetricsService>();

            services.AddScoped<IDownloadService, DownloadService>();
            // Also expose a narrower orchestrator interface to allow incremental decomposition
            services.AddScoped<IDownloadOrchestrator, DownloadService>();
            // Queue service extracted from DownloadService to encapsulate queue-building and filtering
            services.AddScoped<IDownloadQueueService, DownloadQueueService>();
            services.AddScoped<IFileProcessingHandler, FileProcessingHandler>();
            services.AddScoped<IOpenLibraryService, OpenLibraryService>();
            services.AddScoped<IImageCacheService>(sp => new ImageCacheService(
                sp.GetRequiredService<ILogger<ImageCacheService>>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().ContentRootPath
            ));
            services.AddScoped<IFileNamingService, FileNamingService>();
            // Centralized import service: handles moving/copying, naming and audiobook registration
            services.AddScoped<IImportService, ImportService>();
            // Centralized file mover for robust move/copy with retries and diagnostics
            services.AddScoped<IFileMover, FileMover>();
            // File finalizer: handles import delegation and final-path sync
            services.AddScoped<IFileFinalizer, FileFinalizer>();
            // Archive extractor for extracting common archive types (zip/rar/7z)
            services.AddScoped<IArchiveExtractor, ArchiveExtractor>();
            // Completed download processor: handles imports and registration when a download completes
            services.AddScoped<ICompletedDownloadProcessor, CompletedDownloadProcessor>();
            // Bind FileMover options from configuration (optional)
            services.Configure<Listenarr.Api.Services.FileMoverOptions>(config.GetSection("FileMover"));
            // Gateway that wraps adapters for higher-level orchestration
            services.AddScoped<IDownloadClientGateway, DownloadClientGateway>();
            // Process runner for external process execution (robocopy, ffprobe, playwright installer)
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            // Store for persisting external process execution outputs (stdout/stderr) - best-effort
            services.AddScoped<IProcessExecutionStore, ProcessExecutionStore>();
            services.AddScoped<IRemotePathMappingService, RemotePathMappingService>();
            services.AddScoped<ISystemService, SystemService>();
            services.AddScoped<IQualityProfileService, QualityProfileService>();
            services.AddScoped<IUserService, UserService>();
            services.AddSingleton<ILoginRateLimiter, LoginRateLimiter>();
            services.AddScoped<IDownloadProcessingQueueService, DownloadProcessingQueueService>();

            // Repository for Download entity encapsulating EF operations used by application services
            services.AddScoped<Listenarr.Api.Repositories.IDownloadRepository, Listenarr.Api.Repositories.EfDownloadRepository>();
            // Repository for DownloadProcessingJob queries
            services.AddScoped<Listenarr.Api.Repositories.IDownloadProcessingJobRepository, Listenarr.Api.Repositories.EfDownloadProcessingJobRepository>();

            // Discord bot service for managing bot process
            services.AddSingleton<IDiscordBotService, DiscordBotService>();

            // Toast service for broadcasting UI toasts via SignalR
            services.AddSingleton<IToastService, ToastService>();

            // Notification service for webhook notifications (typed HttpClient so a HttpClient is injected)
            // (already registered above)

            // Allow services to access the current HttpContext so NotificationService can
            // build absolute image URLs when the startup config doesn't supply a base URL.
            services.AddHttpContextAccessor();

            // Ensure API shim interface resolves by forwarding to the application-level
            // IHubBroadcaster registration (added in AddListenarrAdapters). Some test
            // hosts reference the API shim type directly, so provide this mapping.
            services.AddSingleton<Listenarr.Api.Services.IHubBroadcaster>(sp =>
                (Listenarr.Api.Services.IHubBroadcaster)sp.GetRequiredService<Listenarr.Application.Services.IHubBroadcaster>());
            // Always register session service, but it will check config internally
            services.AddScoped<SessionService>();
            services.AddScoped<ISessionService, ConditionalSessionService>();

            // Scan queue & background workers registrations are left in Program.cs (hosted services)
            // but any other application service registrations belong here.

            return services;
        }
    }
}
