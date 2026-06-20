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
using Listenarr.Infrastructure.DependencyInjection.Persistence;
using Listenarr.Infrastructure.DependencyInjection.Search;
using Listenarr.Infrastructure.DependencyInjection.Security;
using Listenarr.Infrastructure.DependencyInjection.SystemDiagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection;

/// <summary>
/// Compatibility composition surface for infrastructure implementations.
/// </summary>
public static class InfrastructureServiceRegistrationExtensions
{
    public static IServiceCollection AddListenarrInfrastructure(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder>? configureDb = null,
        string? contentRootPath = null)
    {
        services.AddPersistenceServices(configureDb);
        services.AddDownloadInfrastructure();
        services.AddLibraryInfrastructure();
        services.AddSearchInfrastructure();
        services.AddMetadataInfrastructure();
        services.AddSecurityInfrastructure();
        services.AddSystemDiagnosticInfrastructure(contentRootPath);
        return services;
    }
}
