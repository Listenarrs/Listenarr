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
using Listenarr.Application.Integrations.Audiobookshelf.Contracts;
using Listenarr.Infrastructure.Integrations.Audiobookshelf;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection.Integrations;

internal static class IntegrationRegistrationExtensions
{
    public static IServiceCollection AddIntegrationServices(this IServiceCollection services)
    {
        // Redirects are validated hop-by-hop via OutboundRequestSecurity, so the
        // handler must not follow them on its own.
        services.AddHttpClient<AudiobookshelfService>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = false,
                AllowAutoRedirect = false
            });
        services.AddTransient<IAudiobookshelfService>(provider =>
            provider.GetRequiredService<AudiobookshelfService>());

        services.AddSingleton<AudiobookshelfScanScheduler>();
        services.AddSingleton<IAudiobookshelfScanScheduler>(provider =>
            provider.GetRequiredService<AudiobookshelfScanScheduler>());
        services.AddHostedService(provider => provider.GetRequiredService<AudiobookshelfScanScheduler>());
        return services;
    }
}
