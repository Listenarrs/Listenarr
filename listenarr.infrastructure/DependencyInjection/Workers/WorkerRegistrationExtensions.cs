/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.HostedServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection.Workers;

internal static class WorkerRegistrationExtensions
{
    public static IServiceCollection AddFeatureWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IWorkerCycleRunner, WorkerCycleRunner>();

        services.AddSingleton<IScanQueueService, ScanQueueService>();
        AddProcessor<ScanJobProcessor, IScanJobProcessor>(services);
        services.AddHostedService<ScanBackgroundService>();

        services.AddSingleton<IMoveQueueService, MoveQueueService>();
        AddProcessor<MoveJobProcessor, IMoveJobProcessor>(services);
        services.AddHostedService<MoveBackgroundService>();

        AddHostedProcessor<ImageCacheCleanupProcessor, IImageCacheCleanupProcessor, ImageCacheCleanupService>(services);
        AddHostedProcessor<DownloadMonitorProcessor, IDownloadMonitorProcessor, DownloadMonitorService>(services);
        AddHostedProcessor<MovedDownloadCleanupProcessor, IMovedDownloadCleanupProcessor, MovedDownloadCleanupService>(services);

        AddProcessor<QueueMonitorProcessor, IQueueMonitorProcessor>(services);
        services.AddHostedService<QueueMonitorService>();

        AddHostedProcessor<AutomaticSearchProcessor, IAutomaticSearchProcessor, AutomaticSearchService>(services);
        AddHostedProcessor<AuthorMonitoringProcessor, IAuthorMonitoringProcessor, AuthorMonitoringBackgroundService>(services);
        AddHostedProcessor<SeriesMonitoringProcessor, ISeriesMonitoringProcessor, SeriesMonitoringBackgroundService>(services);
        AddHostedProcessor<FfmpegInstallProcessor, IFfmpegInstallProcessor, FfmpegInstallBackgroundService>(services);
        AddHostedProcessor<MetadataRescanProcessor, IMetadataRescanProcessor, MetadataRescanService>(services);

        services.AddSingleton<DownloadProcessingJobProcessor>();
        services.AddSingleton<IDownloadImportProcessor>(provider =>
            provider.GetRequiredService<DownloadProcessingJobProcessor>());
        services.AddHostedService(provider =>
            provider.GetRequiredService<DownloadProcessingJobProcessor>());

        AddHostedProcessor<UnmatchedScanProcessor, IUnmatchedScanProcessor, UnmatchedScanBackgroundService>(services);
        return services;
    }

    private static void AddProcessor<TProcessor, TContract>(IServiceCollection services)
        where TProcessor : class, TContract
        where TContract : class
    {
        services.AddSingleton<TProcessor>();
        services.AddSingleton<TContract>(provider =>
            provider.GetRequiredService<TProcessor>());
    }

    private static void AddHostedProcessor<TProcessor, TContract, THostedService>(
        IServiceCollection services)
        where TProcessor : class, TContract
        where TContract : class
        where THostedService : class, IHostedService
    {
        AddProcessor<TProcessor, TContract>(services);
        services.AddSingleton<THostedService>();
        services.AddHostedService(provider =>
            provider.GetRequiredService<THostedService>());
    }
}
