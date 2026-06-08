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
// csharp
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Common;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Application.Notification;
using Listenarr.Application.Search;
using Listenarr.Application.Security;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Infrastructure.Ffmpeg;
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Infrastructure.OpenLibrary;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Platform;
using Listenarr.Infrastructure.Search.Providers;
using Listenarr.Infrastructure.Security;
using Listenarr.Infrastructure.Services;
using Listenarr.Infrastructure.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Extensions
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
            services.AddScoped<IIndexerSearchProvider, InternetArchiveSearchProvider>();
            services.AddScoped<IIndexerSearchProvider, TorznabNewznabSearchProvider>();
            services.AddScoped<IIndexerSearchProvider, MyAnonamouseSearchProvider>();

            services.AddScoped<ISearchService, SearchService>();
            services.AddScoped<IMetadataService, MetadataService>();
            services.AddScoped<IAudiobookFileService, AudiobookFileService>();
            services.AddScoped<IAuthorCatalogService, AuthorCatalogService>();
            services.AddScoped<ISeriesCatalogService, SeriesCatalogService>();
            services.AddScoped<ILibraryAddService, LibraryAddService>();
            services.AddScoped<ILibraryListService, LibraryListService>();
            services.AddScoped<IAuthorMonitoringService, AuthorMonitoringService>();
            services.AddScoped<ISeriesMonitoringService, SeriesMonitoringService>();
            // Metadata extraction limiter to bound concurrent ffprobe calls
            services.AddSingleton<MetadataExtractionLimiter>();
            // Ffmpeg installer: provides a bundled ffprobe binary when not present on the system
            services.AddSingleton<IFfmpegService, FfmpegService>();
            // Service to accept client-pushed download updates and maintain recent-push cache
            services.AddSingleton<IDownloadPushService, DownloadPushService>();
            services.AddScoped<IAsinLookupService, AsinLookupService>();
            services.AddScoped<IAudiobookMetadataService, AudiobookMetadataService>();
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
            // Queue service extracted from DownloadService to encapsulate queue-building and filtering
            services.AddScoped<IDownloadQueueService, DownloadQueueService>();
            services.AddScoped<IOpenLibraryService, OpenLibraryService>();
            services.AddScoped<IFileNamingService, FileNamingService>();
            services.AddScoped<IRenameService, RenameService>();
            // Centralized import service: handles moving/copying, naming and audiobook registration
            services.AddScoped<IDownloadImportService, DownloadImportService>();
            // Centralized file mover for robust move/copy with retries and diagnostics
            services.AddScoped<IFileMover, FileMover>();
            // Archive extractor for extracting common archive types (zip/rar/7z)
            services.AddScoped<IArchiveExtractor, ArchiveExtractor>();
            // Bind FileMover options from configuration (optional)
            services.Configure<FileMoverOptions>(config.GetSection("FileMover"));
            // Gateway that wraps adapters for higher-level orchestration
            services.AddScoped<IDownloadClientGateway, DownloadClientGateway>();
            // Process runner for external process execution (robocopy, ffprobe, playwright installer)
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            // Store for persisting external process execution outputs (stdout/stderr) - best-effort
            services.AddScoped<IProcessExecutionStore, ProcessExecutionStore>();
            services.AddScoped<IRemotePathMappingService, RemotePathMappingService>();
            // Cross-platform disk-space measurement (Windows native call vs. DriveInfo)
            services.AddSingleton<IDiskSpaceProbe, DiskSpaceProbe>();
            services.AddScoped<ISystemService, SystemService>();
            services.AddScoped<IQualityProfileService, QualityProfileService>();
            services.AddScoped<IUserService, UserService>();
            services.AddSingleton<ILoginRateLimiter, LoginRateLimiter>();
            services.AddScoped<IDownloadProcessingJobService, DownloadProcessingJobService>();

            // Discord bot service for managing bot process
            services.AddSingleton<IDiscordBotService, DiscordBotService>();

            // Toast service for broadcasting UI toasts via SignalR
            services.AddSingleton<IToastService, ToastService>();

            // Notification service for webhook notifications (typed HttpClient so a HttpClient is injected)
            // (already registered above)

            // Allow services to access the current HttpContext so NotificationService can
            // build absolute image URLs when the startup config doesn't supply a base URL.
            services.AddHttpContextAccessor();


            // Always register session service, but it will check config internally
            services.AddScoped<SessionService>();
            services.AddScoped<ISessionService, ConditionalSessionService>();

            // Scan queue & background workers registrations are left in Program.cs (hosted services)
            // but any other application service registrations belong here.

            return services;
        }
    }
}
