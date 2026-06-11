/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
// csharp
using System.Net;
using Listenarr.Application.Interfaces;
using Listenarr.Infrastructure.Factories;
using Listenarr.Infrastructure.Adapters;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Application.Notification;
using Listenarr.Infrastructure.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Polly.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Polly;
using Microsoft.Extensions.Options;
using Listenarr.Infrastructure.Torrents;

namespace Listenarr.Infrastructure.Extensions
{
    /// <summary>
    /// Helper extension methods to split Program.cs registrations into focused modules.
    /// This file centralizes common HttpClient registrations so callers (Program.cs / tests)
    /// can reuse a single consistent registration surface.
    /// </summary>
    public static partial class ServiceRegistrationExtensions
    {
        /// <summary>
        /// Registers common HttpClients (named/typed) and Polly policies used by the app.
        /// This centralizes policy creation so Program.cs doesn't duplicate logic.
        /// Adds named clients for download adapters and typed clients used by services.
        /// </summary>
        public static IServiceCollection AddListenarrHttpClients(this IServiceCollection services, IConfiguration config)
        {
            // Shared retry/circuit-breaker policy builders
            var retryPolicy = HttpPolicyExtensions.HandleTransientHttpError()
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (outcome, timespan, retryAttempt, context) =>
                    {
                        // Logging can be done inside services that consume HttpClient via ILogger
                    });

            var circuitBreakerPolicy = HttpPolicyExtensions.HandleTransientHttpError()
                .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30));

            // Default HTTP client (bypass system proxy unless explicitly configured)
            services.AddHttpClient("default")
                .ConfigurePrimaryHttpMessageHandler(() => CreateExternalHandler(config));
            services.AddTransient(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("default"));

            // Generic named client used by legacy code paths for downloads
            services.AddHttpClient("DownloadClient")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = false
                })
                .AddPolicyHandler(retryPolicy)
                .AddPolicyHandler(circuitBreakerPolicy);

            // Per-adapter named clients (qbittorrent, transmission, sabnzbd, nzbget)
            services.AddHttpClient("qbittorrent")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    CookieContainer = new CookieContainer(),
                    UseCookies = true
                })
                .SetHandlerLifetime(TimeSpan.FromMinutes(5))
                .AddPolicyHandler(circuitBreakerPolicy)
                .AddPolicyHandler(retryPolicy);

            services.AddHttpClient("transmission")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = false
                })
                .SetHandlerLifetime(TimeSpan.FromMinutes(5))
                .AddPolicyHandler(circuitBreakerPolicy)
                .AddPolicyHandler(retryPolicy);

            services.AddHttpClient("sabnzbd")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = false
                })
                .SetHandlerLifetime(TimeSpan.FromMinutes(5))
                .AddPolicyHandler(circuitBreakerPolicy)
                .AddPolicyHandler(retryPolicy);

            services.AddHttpClient("nzbget")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = false
                })
                .SetHandlerLifetime(TimeSpan.FromMinutes(5))
                .AddPolicyHandler(circuitBreakerPolicy)
                .AddPolicyHandler(retryPolicy);

            // Direct download client with extended timeout for large files
            services.AddHttpClient("DirectDownload")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromHours(2);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All
                })
                .AddPolicyHandler(HttpPolicyExtensions
                    .HandleTransientHttpError()
                    .Or<TaskCanceledException>()
                    .CircuitBreakerAsync(3, TimeSpan.FromMinutes(1)))
                .AddPolicyHandler(HttpPolicyExtensions
                    .HandleTransientHttpError()
                    .Or<TaskCanceledException>()
                    .WaitAndRetryAsync(2, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

            // US-origin client (supports optional proxy via configuration)
            services.AddHttpClient("us")
                .ConfigurePrimaryHttpMessageHandler(() => CreateExternalHandler(config));

            // Typed clients used by metadata services. Add consistent handlers + policies.

            services.AddHttpClient<AudibleService>()
                .ConfigurePrimaryHttpMessageHandler(() => CreateExternalHandler(config))
                .AddPolicyHandler(retryPolicy);

            services.AddHttpClient<AudnexusService>()
                .ConfigurePrimaryHttpMessageHandler(() => CreateExternalHandler(config))
                .AddPolicyHandler(retryPolicy);

            return services;
        }

        // Centralized handler to avoid inheriting system proxies; uses app settings when enabled.
        private static HttpClientHandler CreateExternalHandler(IConfiguration config)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = false
            };

            // Proxy support removed; always use direct handler without custom proxy

            return handler;
        }

        /// <summary>
        /// Registers adapters, their options and validators.
        /// </summary>
        public static IServiceCollection AddListenarrAdapters(this IServiceCollection services, IConfiguration config)
        {
            // Bind download client definitions from configuration and expose via IOptions
            services.Configure<DownloadClientsOptions>(config.GetSection("DownloadClients"));

            // Validate download client configuration at startup, surface errors early
            services.AddSingleton<IValidateOptions<DownloadClientsOptions>, DownloadClientsOptionsValidator>();

            // Shared helpers
            services.AddScoped<INzbUrlResolver, NzbUrlResolver>();
            services.AddScoped<ITorrentFileDownloader, TorrentFileDownloader>();

            // Register available adapter implementations. Keep adapters scoped because they may depend on scoped services.
            services.AddScoped<IDownloadClientAdapter, QbittorrentAdapter>();
            services.AddScoped<IDownloadClientAdapter, TransmissionAdapter>();
            services.AddScoped<IDownloadClientAdapter, SabnzbdAdapter>();
            services.AddScoped<IDownloadClientAdapter, NzbgetAdapter>();

            // Register the concrete factory as scoped so it can safely resolve scoped adapters via DI.
            services.AddScoped<IDownloadClientAdapterFactory, DownloadClientAdapterFactory>();

            // Register import item resolution service (ProvideImportItemService pattern)
            services.AddScoped<IDownloadItemService, DownloadItemService>();

            // Register notification payload builder adapter for DI so callers can inject/mokc payload construction.
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();

            // File storage abstraction used throughout services to isolate System.IO for testing
            services.AddSingleton<IFileStorage, FileStorage>();

            // SignalR broadcaster abstraction used to centralize broadcast logic and simplify testing
            services.AddSingleton<IHubBroadcaster, SignalRHubBroadcaster>();

            return services;
        }
    }
}
