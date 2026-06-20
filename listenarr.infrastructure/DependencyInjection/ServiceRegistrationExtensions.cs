/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.DependencyInjection.DownloadClients;
using Listenarr.Infrastructure.DependencyInjection.Downloads;
using Listenarr.Infrastructure.DependencyInjection.Metadata;
using Listenarr.Infrastructure.DependencyInjection.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection;

/// <summary>
/// Compatibility composition surface for infrastructure HTTP clients and adapters.
/// Feature modules own the individual registrations.
/// </summary>
public static class ServiceRegistrationExtensions
{
    public static IServiceCollection AddListenarrHttpClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddPlatformHttpClients(configuration);
        services.AddDownloadClientHttpClients();
        services.AddDownloadHttpClients();
        services.AddMetadataHttpClients(configuration);
        return services;
    }

    public static IServiceCollection AddListenarrAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDownloadClientAdapters(configuration);
        services.AddPlatformAdapters();
        return services;
    }
}
