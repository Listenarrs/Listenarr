/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection.Library;

internal static class LibraryRegistrationExtensions
{
    public static IServiceCollection AddLibraryServices(this IServiceCollection services)
    {
        services.AddScoped<IAudiobookFileService, AudiobookFileService>();
        services.AddScoped<IAuthorCatalogService, AuthorCatalogService>();
        services.AddScoped<ISeriesCatalogService, SeriesCatalogService>();
        services.AddScoped<ILibraryAddService, LibraryAddService>();
        services.AddScoped<IAudiobookFilesystemDeleteService, AudiobookFilesystemDeleteService>();
        services.AddScoped<ILibraryListService, LibraryListService>();
        services.AddScoped<IAuthorMonitoringService, AuthorMonitoringService>();
        services.AddScoped<ISeriesMonitoringService, SeriesMonitoringService>();
        services.AddScoped<IFileNamingService, FileNamingService>();
        services.AddScoped<IRenameService, RenameService>();
        services.AddScoped<IQualityProfileService, QualityProfileService>();
        return services;
    }

    public static IServiceCollection AddLibraryInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IAudiobookRepository, AudiobookRepository>();
        services.AddScoped<IQualityProfileRepository, QualityProfileRepository>();
        services.AddScoped<IAudiobookFileRepository, EfAudiobookFileRepository>();
        services.AddScoped<IMoveJobRepository, EfMoveJobRepository>();
        services.AddScoped<IMonitoredAuthorRepository, EfMonitoredAuthorRepository>();
        services.AddScoped<IMonitoredSeriesRepository, EfMonitoredSeriesRepository>();
        services.AddScoped<IRootFolderRepository, EfRootFolderRepository>();
        return services;
    }
}
