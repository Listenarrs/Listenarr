/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.DependencyInjection.Downloads;
using Listenarr.Infrastructure.DependencyInjection.Library;
using Listenarr.Infrastructure.DependencyInjection.Metadata;
using Listenarr.Infrastructure.DependencyInjection.Notifications;
using Listenarr.Infrastructure.DependencyInjection.Search;
using Listenarr.Infrastructure.DependencyInjection.Security;
using Listenarr.Infrastructure.DependencyInjection.SystemDiagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection;

/// <summary>
/// Compatibility composition surface for application-facing feature services.
/// </summary>
public static class AppServiceRegistrationExtensions
{
    public static IServiceCollection AddListenarrAppServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddConfigurationAndSecurityServices();
        services.AddSearchServices();
        services.AddMetadataServices();
        services.AddLibraryServices();
        services.AddDownloadServices(configuration);
        services.AddNotificationAndRealtimeServices();
        services.AddSystemDiagnosticServices();
        return services;
    }
}
