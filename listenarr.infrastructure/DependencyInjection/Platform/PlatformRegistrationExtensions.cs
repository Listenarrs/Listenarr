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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Listenarr.Infrastructure.DependencyInjection.Platform;

internal static class PlatformRegistrationExtensions
{
    public static IServiceCollection AddPlatformHttpClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpClient("default")
            .ConfigureHttpClient((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(
                    provider.GetRequiredService<IOptions<ExternalRequestOptions>>().Value.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(CreateExternalHandler);
        services.AddTransient(provider =>
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("default"));
        services.AddHttpClient("us")
            .ConfigureHttpClient((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(
                    provider.GetRequiredService<IOptions<ExternalRequestOptions>>().Value.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(CreateExternalHandler);
        return services;
    }

    public static IServiceCollection AddPlatformAdapters(this IServiceCollection services)
    {
        services.AddSingleton<IFileStorage, FileStorage>();
        services.AddSingleton<IFileSystem, LocalFileSystem>();
        services.AddSingleton<IFileSystemSemanticsResolver, FileSystemSemanticsResolver>();
        services.AddSingleton<IFileSystemVolumeResolver, FileSystemVolumeResolver>();
        return services;
    }

    internal static HttpClientHandler CreateExternalHandler() =>
        new()
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false
        };
}
