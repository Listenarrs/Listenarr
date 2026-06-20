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

namespace Listenarr.Infrastructure.DependencyInjection.Search;

internal static class SearchRegistrationExtensions
{
    public static IServiceCollection AddSearchServices(this IServiceCollection services)
    {
        services.AddScoped<IIndexerSearchProvider, InternetArchiveSearchProvider>();
        services.AddScoped<IIndexerSearchProvider, TorznabNewznabSearchProvider>();
        services.AddScoped<IIndexerSearchProvider, MyAnonamouseSearchProvider>();
        services.AddScoped<IMyAnonamouseConnectionTester, MyAnonamouseConnectionTester>();
        services.AddScoped<IndexerAdditionalSettingsParser>();
        services.AddScoped<IndexerSearchWorkflow>();
        services.AddScoped<MetadataSourceCatalog>();
        services.AddScoped<SearchFinalDispositionLogger>();
        services.AddScoped<ISearchService, SearchService>();
        return services;
    }

    public static IServiceCollection AddSearchInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IIndexerRepository, EfIndexerRepository>();
        return services;
    }
}
