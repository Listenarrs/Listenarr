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
using Listenarr.Infrastructure.Factories;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace Listenarr.Infrastructure.DependencyInjection.DownloadClients;

internal static class DownloadClientRegistrationExtensions
{
    public static IServiceCollection AddDownloadClientHttpClients(this IServiceCollection services)
    {
        var retryPolicy = HttpPolicyExtensions.HandleTransientHttpError()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
        var circuitBreakerPolicy = HttpPolicyExtensions.HandleTransientHttpError()
            .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30));

        services.AddHttpClient("DownloadClient")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .AddPolicyHandler(retryPolicy)
            .AddPolicyHandler(circuitBreakerPolicy);

        AddAdapterClient(services, "qbittorrent", useCookies: true, retryPolicy, circuitBreakerPolicy);
        AddAdapterClient(services, "transmission", useCookies: false, retryPolicy, circuitBreakerPolicy);
        AddAdapterClient(services, "sabnzbd", useCookies: false, retryPolicy, circuitBreakerPolicy);
        AddAdapterClient(services, "nzbget", useCookies: false, retryPolicy, circuitBreakerPolicy);
        return services;
    }

    public static IServiceCollection AddDownloadClientAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DownloadClientsOptions>()
            .Bind(configuration.GetSection("DownloadClients"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DownloadClientsOptions>, DownloadClientsOptionsValidator>();
        services.AddScoped<INzbUrlResolver, NzbUrlResolver>();
        services.AddScoped<ITorrentFileDownloader, TorrentFileDownloader>();
        services.AddScoped<IDownloadClientAdapter, QbittorrentAdapter>();
        services.AddScoped<IDownloadClientAdapter, TransmissionAdapter>();
        services.AddScoped<IDownloadClientAdapter, SabnzbdAdapter>();
        services.AddScoped<IDownloadClientAdapter, NzbgetAdapter>();
        services.AddScoped<IDownloadClientAdapterFactory, DownloadClientAdapterFactory>();
        services.AddScoped<IDownloadItemService, DownloadItemService>();
        return services;
    }

    private static void AddAdapterClient(
        IServiceCollection services,
        string name,
        bool useCookies,
        IAsyncPolicy<HttpResponseMessage> retryPolicy,
        IAsyncPolicy<HttpResponseMessage> circuitBreakerPolicy)
    {
        services.AddHttpClient(name)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => CreateHandler(useCookies))
            .SetHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddPolicyHandler(circuitBreakerPolicy)
            .AddPolicyHandler(retryPolicy);
    }

    private static HttpClientHandler CreateHandler() => CreateHandler(useCookies: false);

    private static HttpClientHandler CreateHandler(bool useCookies)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = useCookies
        };

        if (useCookies)
        {
            handler.CookieContainer = new CookieContainer();
        }

        return handler;
    }
}
