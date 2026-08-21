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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace Listenarr.Infrastructure.DependencyInjection.DownloadClients;

internal static class DownloadClientRegistrationExtensions
{
    public static IServiceCollection AddDownloadClientHttpClients(this IServiceCollection services)
    {
        // The retry policy is stateless, so one instance shared by every client is correct and is
        // how Polly is meant to be used. A circuit breaker is not: its open/closed state and its
        // failure count live inside the policy instance. Sharing one gives all these clients a
        // single global breaker rather than one each, so a run of failures against any one of them
        // stops polling for all of them. Each client gets its own.
        var retryPolicy = HttpPolicyExtensions.HandleTransientHttpError()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        services.AddHttpClient("DownloadClient")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .AddPolicyHandler(retryPolicy)
            .AddPolicyHandler(CreateCircuitBreakerPolicy());

        AddAdapterClient(services, DownloadClientTypes.Qbittorrent, useCookies: true, retryPolicy);
        AddAdapterClient(services, DownloadClientTypes.Transmission, useCookies: false, retryPolicy);
        AddAdapterClient(services, DownloadClientTypes.Sabnzbd, useCookies: false, retryPolicy);
        AddAdapterClient(services, DownloadClientTypes.Nzbget, useCookies: false, retryPolicy);
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
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<INzbUrlResolver, NzbUrlResolver>();
        services.AddScoped<ITorrentFileDownloader, TorrentFileDownloader>();

        services.AddQbittorrentWorkflows();
        services.AddTransmissionWorkflows();
        services.AddSabnzbdWorkflows();
        services.AddNzbgetWorkflows();

        services.AddScoped<IDownloadClientAdapter>(sp => new QbittorrentAdapter(
            sp.GetRequiredService<QbittorrentConnectionTester>(),
            sp.GetRequiredService<QbittorrentAddWorkflow>(),
            sp.GetRequiredService<QbittorrentImportMarkerWorkflow>(),
            sp.GetRequiredService<QbittorrentRemovalWorkflow>(),
            sp.GetRequiredService<QbittorrentQueueFetchWorkflow>(),
            sp.GetRequiredService<QbittorrentItemFetchWorkflow>(),
            sp.GetRequiredService<QbittorrentImportItemResolver>()));
        services.AddScoped<IDownloadClientAdapter>(sp => new TransmissionAdapter(
            sp.GetRequiredService<TransmissionConnectionTester>(),
            sp.GetRequiredService<TransmissionAddWorkflow>(),
            sp.GetRequiredService<TransmissionRemovalWorkflow>(),
            sp.GetRequiredService<TransmissionQueueFetchWorkflow>(),
            sp.GetRequiredService<TransmissionItemFetchWorkflow>(),
            sp.GetRequiredService<TransmissionImportItemResolver>()));
        services.AddScoped<IDownloadClientAdapter>(sp => new SabnzbdAdapter(
            sp.GetRequiredService<SabnzbdConnectionTester>(),
            sp.GetRequiredService<SabnzbdAddWorkflow>(),
            sp.GetRequiredService<SabnzbdRemovalWorkflow>(),
            sp.GetRequiredService<SabnzbdQueueFetchWorkflow>(),
            sp.GetRequiredService<SabnzbdHistoryFetchWorkflow>(),
            sp.GetRequiredService<SabnzbdItemFetchWorkflow>(),
            sp.GetRequiredService<SabnzbdImportItemResolver>()));
        services.AddScoped<IDownloadClientAdapter>(sp => new NzbgetAdapter(
            sp.GetRequiredService<NzbgetConnectionTester>(),
            sp.GetRequiredService<NzbgetAddWorkflow>(),
            sp.GetRequiredService<NzbgetRemovalWorkflow>(),
            sp.GetRequiredService<NzbgetQueueFetchWorkflow>(),
            sp.GetRequiredService<NzbgetHistoryFetchWorkflow>(),
            sp.GetRequiredService<NzbgetItemFetchWorkflow>(),
            sp.GetRequiredService<NzbgetImportItemResolver>()));
        services.AddScoped<IDownloadClientAdapterFactory, DownloadClientAdapterFactory>();
        services.AddScoped<IDownloadItemService, DownloadItemService>();
        return services;
    }

    private static IServiceCollection AddQbittorrentWorkflows(this IServiceCollection services)
    {
        services.AddScoped<QbittorrentAuthSession>(sp =>
            new QbittorrentAuthSession(sp.GetRequiredService<ILogger<QbittorrentAdapter>>()));
        services.AddScoped<QbittorrentConnectionTester>(sp =>
            new QbittorrentConnectionTester(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<QbittorrentAdapter>>(),
                DownloadClientTypes.Qbittorrent));
        services.AddScoped<QbittorrentAddWorkflow>(sp =>
            new QbittorrentAddWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<QbittorrentAuthSession>(),
                sp.GetRequiredService<ILogger<QbittorrentAdapter>>(),
                DownloadClientTypes.Qbittorrent));
        services.AddScoped<QbittorrentImportMarkerWorkflow>(sp =>
            new QbittorrentImportMarkerWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<QbittorrentAdapter>>(),
                DownloadClientTypes.Qbittorrent));
        services.AddScoped<QbittorrentRemovalWorkflow>(sp =>
            new QbittorrentRemovalWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<QbittorrentAdapter>>(),
                DownloadClientTypes.Qbittorrent));
        services.AddScoped<QbittorrentQueueFetchWorkflow>(sp =>
            new QbittorrentQueueFetchWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<QbittorrentAuthSession>(),
                sp.GetRequiredService<ILogger<QbittorrentAdapter>>(),
                DownloadClientTypes.Qbittorrent));
        services.AddScoped<QbittorrentItemFetchWorkflow>(sp =>
            new QbittorrentItemFetchWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<QbittorrentAuthSession>(),
                sp.GetRequiredService<ILogger<QbittorrentAdapter>>(),
                DownloadClientTypes.Qbittorrent));
        services.AddScoped<QbittorrentImportItemResolver>(sp =>
            new QbittorrentImportItemResolver(sp.GetRequiredService<ILogger<QbittorrentAdapter>>()));
        return services;
    }

    private static IServiceCollection AddTransmissionWorkflows(this IServiceCollection services)
    {
        services.AddScoped<TransmissionRpcClient>(sp =>
            new TransmissionRpcClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                DownloadClientTypes.Transmission,
                sp.GetRequiredService<ILogger<TransmissionAdapter>>()));
        services.AddScoped<TransmissionConnectionTester>(sp =>
            new TransmissionConnectionTester(
                sp.GetRequiredService<TransmissionRpcClient>(),
                sp.GetRequiredService<ILogger<TransmissionAdapter>>()));
        services.AddScoped<TransmissionAddWorkflow>(sp =>
            new TransmissionAddWorkflow(
                sp.GetRequiredService<TransmissionRpcClient>(),
                sp.GetRequiredService<ILogger<TransmissionAdapter>>()));
        services.AddScoped<TransmissionRemovalWorkflow>(sp =>
            new TransmissionRemovalWorkflow(
                sp.GetRequiredService<TransmissionRpcClient>(),
                sp.GetRequiredService<ILogger<TransmissionAdapter>>()));
        services.AddScoped<TransmissionQueueFetchWorkflow>(sp =>
            new TransmissionQueueFetchWorkflow(
                sp.GetRequiredService<TransmissionRpcClient>(),
                sp.GetRequiredService<ILogger<TransmissionAdapter>>()));
        services.AddScoped<TransmissionItemFetchWorkflow>(sp =>
            new TransmissionItemFetchWorkflow(
                sp.GetRequiredService<TransmissionRpcClient>(),
                sp.GetRequiredService<ILogger<TransmissionAdapter>>()));
        services.AddScoped<TransmissionImportItemResolver>(sp =>
            new TransmissionImportItemResolver(
                sp.GetRequiredService<TransmissionRpcClient>(),
                sp.GetRequiredService<ILogger<TransmissionAdapter>>()));
        return services;
    }

    private static IServiceCollection AddSabnzbdWorkflows(this IServiceCollection services)
    {
        services.AddScoped<SabnzbdRequestBuilder>();
        services.AddScoped<SabnzbdConnectionTester>(sp =>
            new SabnzbdConnectionTester(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SabnzbdRequestBuilder>(),
                sp.GetRequiredService<ILogger<SabnzbdAdapter>>(),
                DownloadClientTypes.Sabnzbd));
        services.AddScoped<SabnzbdAddWorkflow>(sp =>
            new SabnzbdAddWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SabnzbdRequestBuilder>(),
                sp.GetRequiredService<ILogger<SabnzbdAdapter>>(),
                DownloadClientTypes.Sabnzbd));
        services.AddScoped<SabnzbdRemovalWorkflow>(sp =>
            new SabnzbdRemovalWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SabnzbdRequestBuilder>(),
                sp.GetRequiredService<ILogger<SabnzbdAdapter>>(),
                DownloadClientTypes.Sabnzbd));
        services.AddScoped<SabnzbdQueueFetchWorkflow>(sp =>
            new SabnzbdQueueFetchWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SabnzbdRequestBuilder>(),
                sp.GetRequiredService<ILogger<SabnzbdAdapter>>(),
                DownloadClientTypes.Sabnzbd));
        services.AddScoped<SabnzbdHistoryFetchWorkflow>(sp =>
            new SabnzbdHistoryFetchWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SabnzbdRequestBuilder>(),
                sp.GetRequiredService<ILogger<SabnzbdAdapter>>(),
                DownloadClientTypes.Sabnzbd));
        services.AddScoped<SabnzbdItemFetchWorkflow>(sp =>
            new SabnzbdItemFetchWorkflow(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SabnzbdRequestBuilder>(),
                sp.GetRequiredService<ILogger<SabnzbdAdapter>>(),
                DownloadClientTypes.Sabnzbd));
        services.AddScoped<SabnzbdImportItemResolver>(sp =>
            new SabnzbdImportItemResolver(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<SabnzbdRequestBuilder>(),
                sp.GetRequiredService<ILogger<SabnzbdAdapter>>(),
                DownloadClientTypes.Sabnzbd));
        return services;
    }

    private static IServiceCollection AddNzbgetWorkflows(this IServiceCollection services)
    {
        services.AddScoped<NzbgetXmlRpcClient>(sp =>
            new NzbgetXmlRpcClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                DownloadClientTypes.Nzbget));
        services.AddScoped<NzbgetHistoryReader>();
        services.AddScoped<NzbgetHistoryEnrichmentWorkflow>(sp =>
            new NzbgetHistoryEnrichmentWorkflow(
                sp.GetRequiredService<NzbgetHistoryReader>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<NzbgetConnectionTester>(sp =>
            new NzbgetConnectionTester(
                sp.GetRequiredService<NzbgetXmlRpcClient>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>()));
        services.AddScoped<NzbgetAddWorkflow>(sp =>
            new NzbgetAddWorkflow(
                sp.GetRequiredService<NzbgetXmlRpcClient>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>()));
        services.AddScoped<NzbgetRemovalWorkflow>(sp =>
            new NzbgetRemovalWorkflow(
                sp.GetRequiredService<NzbgetXmlRpcClient>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>()));
        services.AddScoped<NzbgetQueueFetchWorkflow>(sp =>
            new NzbgetQueueFetchWorkflow(
                sp.GetRequiredService<NzbgetXmlRpcClient>(),
                sp.GetRequiredService<NzbgetHistoryEnrichmentWorkflow>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>()));
        services.AddScoped<NzbgetHistoryFetchWorkflow>(sp =>
            new NzbgetHistoryFetchWorkflow(
                sp.GetRequiredService<NzbgetXmlRpcClient>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>()));
        services.AddScoped<NzbgetItemFetchWorkflow>(sp =>
            new NzbgetItemFetchWorkflow(
                sp.GetRequiredService<NzbgetXmlRpcClient>(),
                sp.GetRequiredService<NzbgetHistoryEnrichmentWorkflow>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>()));
        services.AddScoped<NzbgetImportItemResolver>(sp =>
            new NzbgetImportItemResolver(
                sp.GetRequiredService<NzbgetXmlRpcClient>(),
                sp.GetRequiredService<ILogger<NzbgetAdapter>>()));
        return services;
    }

    // A new breaker per call. Returning a fresh instance is the whole point: one shared instance
    // would put every download client behind a single circuit.
    internal static IAsyncPolicy<HttpResponseMessage> CreateCircuitBreakerPolicy() =>
        HttpPolicyExtensions.HandleTransientHttpError()
            .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30));

    private static void AddAdapterClient(
        IServiceCollection services,
        string name,
        bool useCookies,
        IAsyncPolicy<HttpResponseMessage> retryPolicy)
    {
        services.AddHttpClient(name)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => CreateHandler(useCookies))
            .SetHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddPolicyHandler(CreateCircuitBreakerPolicy())
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
