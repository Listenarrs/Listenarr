/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;
using Listenarr.Infrastructure.Configuration;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace Listenarr.Infrastructure.DependencyInjection.Downloads;

internal static class DownloadRegistrationExtensions
{
    internal const string MyAnonamouseTorrentClientName = "MyAnonamouseTorrent";

    public static IServiceCollection AddDownloadHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient("DirectDownload")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromHours(2))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .Or<TaskCanceledException>()
                .CircuitBreakerAsync(3, TimeSpan.FromMinutes(1)))
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .Or<TaskCanceledException>()
                .WaitAndRetryAsync(2, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt))));

        services.AddHttpClient(MyAnonamouseTorrentClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });

        return services;
    }

    public static IServiceCollection AddDownloadServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IDownloadPushService, DownloadPushService>();
        services.AddScoped<IDownloadService, DownloadService>();
        services.AddScoped<DownloadTypeResolver>();
        services.AddScoped<DownloadClientSelector>();
        services.AddScoped<DownloadCachedTorrentStore>();
        services.AddSingleton<IDownloadReferenceService, DownloadReferenceService>();
        services.AddScoped<DirectDownloadWorkflow>();
        services.AddScoped<DownloadRemovalWorkflow>();
        services.AddScoped<DownloadQueueCandidateLoader>();
        services.AddScoped<DownloadClientQueuePoller>();
        services.AddScoped<DownloadOrphanCleanupService>();
        services.AddScoped<IDownloadQueueService, DownloadQueueService>();
        services.AddScoped<ImportDestinationPlanner>();
        services.AddScoped<ArchiveImportExtractor>();
        services.AddScoped<IDownloadImportService, DownloadImportService>();
        services.AddScoped<FileMover>();
        services.AddScoped<IFileMover>(provider =>
            provider.GetRequiredService<FileMover>());
        services.AddScoped<IFilePublicationSourceCapability>(provider =>
            provider.GetRequiredService<FileMover>());
        services.AddScoped<IFilePublicationCapabilityResolver,
            FilePublicationCapabilityResolver>();
        services.AddScoped<IArchiveExtractor, ArchiveExtractor>();
        services.AddOptions<FileMoverOptions>()
            .Bind(configuration.GetSection("FileMover"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FileMoverOptions>, FileMoverOptionsValidator>();
        services.AddScoped<IDownloadClientGateway, DownloadClientGateway>();
        services.AddScoped<IRemotePathMappingService, RemotePathMappingService>();
        services.AddScoped<IDownloadProcessingJobService, DownloadProcessingJobService>();
        services.AddScoped<IDirectDownloadImportSourceResolver, DirectDownloadImportSourceResolver>();
        return services;
    }

    public static IServiceCollection AddDownloadInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDownloadClientConfigurationRepository, EfDownloadClientConfigurationRepository>();
        services.AddScoped<IRemotePathMappingRepository, EfRemotePathMappingRepository>();
        services.AddScoped<IDownloadRepository, EfDownloadRepository>();
        services.AddScoped<IDownloadProcessingJobRepository, EfDownloadProcessingJobRepository>();
        services.AddScoped<IDownloadHistoryRepository, DownloadHistoryRepository>();
        services.AddScoped<IImportFinalizationService, ImportFinalizationService>();
        services.AddSingleton<IDownloadReferenceProtector, DataProtectionDownloadReferenceProtector>();
        services.AddScoped<IDownloadSubmissionPreparer, DownloadSubmissionPreparer>();
        services.AddScoped<ITorrentMetadataService, TorrentMetadataService>();
        services.AddScoped<INzbFileDownloader, NzbFileDownloader>();
        services.AddScoped<MyAnonamouseTorrentPreparationService>();
        services.AddSingleton<IDirectDownloadSourcePolicy, InternetArchiveDirectDownloadSourcePolicy>();
        services.AddScoped<IDownloadSourceResolver, MyAnonamouseSourceResolver>();
        services.AddScoped<IDownloadSourceResolver, GenericTorrentSourceResolver>();
        services.AddScoped<IDownloadSourceResolver, GenericUsenetSourceResolver>();
        services.AddScoped<IDownloadSourceResolver, DirectDownloadSubmissionResolver>();
        return services;
    }
}
