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
using Listenarr.Application.Search;
using Listenarr.Infrastructure.Ffmpeg;
using Listenarr.Infrastructure.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Extensions
{
    /// <summary>
    /// Registers hosted/background services and their supporting singletons/queues.
    /// Extracted from Program.cs so startup focuses on wiring modules, and hosted-worker
    /// surface is discoverable/testable and easy to disable in tests.
    /// </summary>
    public static class HostedServiceRegistrationExtensions
    {
        public static IServiceCollection AddListenarrHostedServices(this IServiceCollection services, IConfiguration config)
        {
            // Scan queue: enqueue folder scans to be processed in the background
            services.AddSingleton<IScanQueueService, ScanQueueService>();
            // Background worker to consume scan jobs and persist audiobook files
            services.AddHostedService<ScanBackgroundService>();

            // Move queue: enqueue safe move operations when an audiobook BasePath changes
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            // Background worker to consume move jobs and perform safe filesystem move
            services.AddHostedService<MoveBackgroundService>();

            // Register background service for daily cache cleanup
            services.AddHostedService<ImageCacheCleanupService>();

            // Register background service for download monitoring and real-time updates
            services.AddHostedService<DownloadMonitorService>();

            // Register background service for completed download handling (import pipeline)
            // Implements CompletedDownloadService pattern for stability window validation
            services.AddHostedService<MovedDownloadProcessor>();

            // Register background service for queue monitoring (external clients) and real-time updates
            services.AddHostedService<QueueMonitorService>();

            // Register background service for automatic audiobook searching
            services.AddHostedService<AutomaticSearchService>();

            // Register background service for syncing monitored author catalogs
            services.AddHostedService<AuthorMonitoringBackgroundService>();

            // Register background service for syncing monitored series catalogs
            services.AddHostedService<SeriesMonitoringBackgroundService>();

            // Background installer for ffprobe - run in background so startup isn't blocked
            services.AddHostedService<FfmpegInstallBackgroundService>();

            // Background service to rescan files missing metadata
            services.AddHostedService<MetadataRescanService>();

            // Register background service for download processing queue
            services.AddHostedService<DownloadProcessingJobProcessor>();

            // Background worker that processes unmatched-file scan jobs
            services.AddHostedService<UnmatchedScanBackgroundService>();

            return services;
        }
    }
}
