// csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Models;
using Listenarr.Api.Services.Adapters;
using Listenarr.Api.Services;
using Microsoft.Extensions.Options;

namespace Listenarr.Api.Extensions
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
                    UseCookies = false
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

            services.AddHttpClient<Listenarr.Api.Services.AudimetaService>()
                .ConfigurePrimaryHttpMessageHandler(() => CreateExternalHandler(config))
                .AddPolicyHandler(retryPolicy);

            services.AddHttpClient<Listenarr.Api.Services.AudnexusService>()
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
        /// Register persistence (DbContextFactory + compatibility DbContext).
        /// This is intentionally minimal and safe for test hosts.
        /// </summary>
        public static IServiceCollection AddListenarrPersistence(this IServiceCollection services, IConfiguration configuration, string sqliteDbPath)
        {
            // Build DbContextOptions once and register as a singleton so factories
            // and background services can create contexts without forcing EF to
            // resolve scoped option-configurators from the root provider.
            var dbOptionsBuilder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ListenArrDbContext>();
            dbOptionsBuilder.UseSqlite($"Data Source={sqliteDbPath}", sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(Listenarr.Infrastructure.Repositories.QualityProfileRepository).Assembly.GetName().Name);
            });

            services.AddSingleton<Microsoft.EntityFrameworkCore.DbContextOptions<ListenArrDbContext>>(sp => dbOptionsBuilder.Options);

            // Provide a simple IDbContextFactory implementation that uses the
            // singleton DbContextOptions to construct contexts on demand.
            services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ListenArrDbContext>>(sp =>
                new SimpleDbContextFactory(sp.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<ListenArrDbContext>>()));

            // Register the scoped DbContext for controllers but register the
            // options/configuration with a Singleton lifetime so EF's
            // CreateDbContextOptions can resolve any IDbContextOptionsConfiguration
            // instances from the application (root) provider during request handling.
            services.AddDbContext<ListenArrDbContext>(options =>
            {
                options.UseSqlite($"Data Source={sqliteDbPath}", sqliteOptions =>
                {
                    sqliteOptions.MigrationsAssembly(typeof(Listenarr.Infrastructure.Repositories.QualityProfileRepository).Assembly.GetName().Name);
                });
            }, ServiceLifetime.Scoped, ServiceLifetime.Singleton);

            // Register infrastructure repository implementations
            services.AddScoped<Listenarr.Application.Repositories.IQualityProfileRepository, Listenarr.Infrastructure.Repositories.QualityProfileRepository>();

            return services;
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

            // Title matching service extracted from DownloadService for easier testing
            services.AddScoped<ITitleMatchingService, Listenarr.Api.Services.Adapters.TitleMatchingService>();

            // Shared helpers
            services.AddScoped<INzbUrlResolver, NzbUrlResolver>();

            // Register available adapter implementations. Keep adapters scoped because they may depend on scoped services.
            services.AddScoped<IDownloadClientAdapter, Listenarr.Api.Services.Adapters.QbittorrentAdapter>();
            services.AddScoped<IDownloadClientAdapter, Listenarr.Api.Services.Adapters.TransmissionAdapter>();
            services.AddScoped<IDownloadClientAdapter, Listenarr.Api.Services.Adapters.SabnzbdAdapter>();
            services.AddScoped<IDownloadClientAdapter, Listenarr.Api.Services.Adapters.NzbgetAdapter>();

            // Register the concrete factory as scoped so it can safely resolve scoped adapters via DI.
            services.AddScoped<IDownloadClientAdapterFactory, Listenarr.Api.Services.Adapters.DownloadClientAdapterFactory>();

            // Register import item resolution service (ProvideImportItemService pattern)
            services.AddScoped<IImportItemResolutionService, ImportItemResolutionService>();

            // Register notification payload builder adapter for DI so callers can inject/mokc payload construction.
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();

            // File storage abstraction used throughout services to isolate System.IO for testing
            services.AddSingleton<IFileStorage, Listenarr.Api.Services.FileStorage>();

            // SignalR broadcaster abstraction used to centralize broadcast logic and simplify testing
            services.AddSingleton<Listenarr.Application.Services.IHubBroadcaster, Listenarr.Api.Services.SignalRHubBroadcaster>();

            return services;
        }
    }

    /// <summary>
    /// Extension methods for System.Text.Json types
    /// </summary>
    public static class JsonExtensions
    {
        /// <summary>
        /// Gets a property value from a JsonElement, returning defaultValue if the property doesn't exist or is null.
        /// </summary>
        public static T GetPropertyOrDefault<T>(this System.Text.Json.JsonElement element, string propertyName, T defaultValue = default!)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<T>(prop.GetRawText()) ?? defaultValue;
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }
    }

    /// <summary>
    /// Lightweight factory that creates ListenArrDbContext instances from a
    /// pre-built DbContextOptions instance. This avoids EF registering
    /// IDbContextOptionsConfiguration services with scoped lifetimes that
    /// would otherwise be resolved from the root provider.
    /// </summary>
    internal class SimpleDbContextFactory : Microsoft.EntityFrameworkCore.IDbContextFactory<ListenArrDbContext>
    {
        private readonly Microsoft.EntityFrameworkCore.DbContextOptions<ListenArrDbContext> _options;

        public SimpleDbContextFactory(Microsoft.EntityFrameworkCore.DbContextOptions<ListenArrDbContext> options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public ListenArrDbContext CreateDbContext()
        {
            return new ListenArrDbContext(_options);
        }

        public System.Threading.Tasks.Task<ListenArrDbContext> CreateDbContextAsync()
        {
            return System.Threading.Tasks.Task.FromResult(CreateDbContext());
        }
    }
}
