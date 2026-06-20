/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.DependencyInjection.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection;

/// <summary>
/// Compatibility composition surface for hosted workers.
/// </summary>
public static class HostedServiceRegistrationExtensions
{
    public static IServiceCollection AddListenarrHostedServices(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services.AddFeatureWorkers(configuration);
}
