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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Infrastructure.Cache;
using Listenarr.Infrastructure.Platform;
using Listenarr.Infrastructure.Services;

namespace Listenarr.Infrastructure.Extensions
{
    /// <summary>
    /// Registers infrastructure implementations (repositories, persistence adapters, etc.).
    /// Keep this in the Infrastructure project so Program.cs can call a single registration surface.
    /// </summary>
    public static class InfrastructureServiceRegistrationExtensions
    {
        /// <summary>
        /// Registers all infrastructure services. When <paramref name="configureDb"/> is provided
        /// the DbContext, DbContextOptions, and IDbContextFactory are also registered using the
        /// supplied options (e.g. SQLite for production, InMemory for tests). Omit the parameter
        /// only when the caller has already registered a DbContext independently.
        /// </summary>
        public static IServiceCollection AddListenarrInfrastructure(
            this IServiceCollection services,
            Action<DbContextOptionsBuilder>? configureDb = null,
            string? contentRootPath = null)
        {
            if (configureDb != null)
            {
                // AddDbContextFactory registers IDbContextFactory<T> (singleton) and
                // DbContextOptions<T> (singleton). It also registers a scoped ListenArrDbContext
                // derived from the factory, which satisfies direct-injection repos like
                // QualityProfileRepository and AudiobookRepository.
                services.AddDbContextFactory<ListenArrDbContext>(configureDb, ServiceLifetime.Singleton);
            }

            services.AddScoped<IAudiobookRepository, AudiobookRepository>();
            services.AddScoped<IQualityProfileRepository, QualityProfileRepository>();

            services.AddScoped<IIndexerRepository, EfIndexerRepository>();
            services.AddScoped<IUserRepository, EfUserRepository>();
            services.AddScoped<IUserSessionRepository, EfUserSessionRepository>();
            services.AddScoped<IHistoryRepository, EfHistoryRepository>();
            services.AddScoped<IApplicationSettingsRepository, EfApplicationSettingsRepository>();
            services.AddScoped<IApiConfigurationRepository, EfApiConfigurationRepository>();
            services.AddScoped<IDownloadClientConfigurationRepository, EfDownloadClientConfigurationRepository>();
            services.AddScoped<IRemotePathMappingRepository, EfRemotePathMappingRepository>();
            services.AddScoped<IAudiobookFileRepository, EfAudiobookFileRepository>();
            services.AddScoped<IMoveJobRepository, EfMoveJobRepository>();
            services.AddScoped<IMonitoredAuthorRepository, EfMonitoredAuthorRepository>();
            services.AddScoped<IMonitoredSeriesRepository, EfMonitoredSeriesRepository>();
            services.AddScoped<IProcessExecutionLogRepository, EfProcessExecutionLogRepository>();
            services.AddScoped<IDownloadRepository, EfDownloadRepository>();
            services.AddScoped<IDownloadProcessingJobRepository, EfDownloadProcessingJobRepository>();
            services.AddScoped<IRootFolderRepository, EfRootFolderRepository>();
            services.AddScoped<IDownloadHistoryRepository, DownloadHistoryRepository>();
            services.AddSingleton<IApplicationPathService>(_ => new ApplicationPathService(contentRootPath));
            services.AddScoped<IAudioTagWriter, TagLibAudioTagWriter>();
            services.AddSingleton<IHtmlTextExtractor, HtmlAgilityPackTextExtractor>();
            services.AddSingleton<IAudibleAuthorPageParser, HtmlAgilityPackAudibleAuthorPageParser>();
            services.AddHttpClient<ICoverImageProbe, ImageSharpCoverImageProbe>();
            services.AddHttpClient<ImageCacheService>()
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.All
                });
            services.AddSingleton<IImageCacheService, ImageCacheService>();
            services.AddScoped<IApplicationVersionService, ApplicationVersionService>();

            return services;
        }
    }
}
