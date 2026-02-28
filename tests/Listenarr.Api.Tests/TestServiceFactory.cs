using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Microsoft.AspNetCore.SignalR;
using Listenarr.Api.Services;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Tests
{
    /// <summary>
    /// Lightweight test service factory to build an IServiceProvider for unit tests.
    /// Call BuildServiceProvider and pass additional registrations via the configure callback.
    /// This keeps tests using DI instead of directly new-ing large services.
    /// </summary>
    public static class TestServiceFactory
    {
        /// <summary>
        /// Build a ServiceProvider for tests.
        /// - Registers Logging and an empty IConfiguration by default.
        /// - Allows the caller to add/override registrations via the configure callback.
        /// </summary>
        public static ServiceProvider BuildServiceProvider(Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();

            // Basic infrastructure commonly used in tests
            services.AddLogging();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            services.AddSingleton<IConfiguration>(configuration);

            // Provide default test-friendly implementations for metrics and notification
            services.AddSingleton<Listenarr.Api.Services.IAppMetricsService, Listenarr.Api.Services.NoopAppMetricsService>();
            // Allow access to HttpContext in tests
            services.AddHttpContextAccessor();
            // Minimal notification payload builder used to construct a NotificationService instance
            services.AddSingleton<Listenarr.Api.Services.INotificationPayloadBuilder, TestNotificationPayloadBuilder>();
            // Minimal IConfigurationService fallback for tests that don't register a real one
            services.AddSingleton<Listenarr.Api.Services.IConfigurationService, TestConfigurationService>();
            // Register a concrete NotificationService so types requesting the concrete class resolve during tests
            services.AddSingleton<Listenarr.Api.Services.NotificationService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<Listenarr.Api.Services.NotificationService>>();
                var config = sp.GetRequiredService<Listenarr.Api.Services.IConfigurationService>();
                var payloadBuilder = sp.GetRequiredService<Listenarr.Api.Services.INotificationPayloadBuilder>();
                var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
                // Use a simple HttpClient for tests
                var httpClient = new HttpClient();
                return new Listenarr.Api.Services.NotificationService(httpClient, logger, config, payloadBuilder, httpContextAccessor);
            });

            // Let the test add or override services (mocks, fakes, concrete implementations)
            // Ensure a default download client gateway is available for legacy tests
            // that construct DownloadService without registering the gateway explicitly.
            // Register as a singleton in tests so the root provider can resolve DownloadService
            // (many tests call GetRequiredService<DownloadService>() on the root provider).
            // Register a lightweight test gateway that will use any test-provided HttpClient/IHttpClientFactory
            // This allows tests that register a DelegatingHandlerMock to exercise SABnzbd queue/history paths
            services.AddSingleton<IDownloadClientGateway>(sp =>
            {
                var httpFactory = sp.GetService<IHttpClientFactory>();
                var httpClient = sp.GetService<HttpClient>();
                return new TestDownloadClientGateway(httpFactory, httpClient);
            });

            // Provide a test-friendly IDownloadRepository so tests that resolve DownloadService
            // from the root provider don't need to register it explicitly. Prefer an existing
            // ListenArrDbContext if present, otherwise fall back to an in-memory test repo.
            services.AddSingleton<Listenarr.Api.Repositories.IDownloadRepository>(sp =>
            {
                var dbFactory = sp.GetService<IDbContextFactory<ListenArrDbContext>>();
                if (dbFactory != null)
                {
                    var logger = sp.GetRequiredService<ILogger<Listenarr.Api.Repositories.EfDownloadRepository>>();
                    return new Listenarr.Api.Repositories.EfDownloadRepository(dbFactory, logger);
                }

                var db = sp.GetService<ListenArrDbContext>();
                return new TestDownloadRepository(db);
            });

            // Provide a test-friendly IDownloadProcessingJobRepository for tests.
            services.AddSingleton<Listenarr.Api.Repositories.IDownloadProcessingJobRepository>(sp =>
            {
                var dbFactory = sp.GetService<IDbContextFactory<ListenArrDbContext>>();
                if (dbFactory != null)
                {
                    var logger = sp.GetRequiredService<ILogger<Listenarr.Api.Repositories.EfDownloadProcessingJobRepository>>();
                    return new Listenarr.Api.Repositories.EfDownloadProcessingJobRepository(dbFactory, logger);
                }

                var db = sp.GetService<ListenArrDbContext>();
                return new TestDownloadProcessingJobRepository(db);
            });

            // Provide a test-friendly IFileFinalizer so DownloadService can be resolved from the root provider.
            // Prefer the real FileFinalizer when an IImportService is available; otherwise fall back to a lightweight test finalizer.
            services.AddSingleton<Listenarr.Api.Services.IFileFinalizer>(sp =>
            {
                var import = sp.GetService<Listenarr.Api.Services.IImportService>();
                if (import != null)
                {
                    var downloadRepo = sp.GetRequiredService<Listenarr.Api.Repositories.IDownloadRepository>();
                    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                    var logger = sp.GetRequiredService<ILogger<Listenarr.Api.Services.FileFinalizer>>();
                    return new Listenarr.Api.Services.FileFinalizer(import, downloadRepo, scopeFactory, logger);
                }

                // Lightweight fallback that simulates successful imports when no IImportService is registered.
                return new TestFileFinalizer(import);
            });

            // Provide a test-friendly IFfmpegService to avoid actual ffprobe extraction in tests
            // This eliminates race conditions and file access issues in CI environments
            services.AddSingleton<Listenarr.Api.Services.IFfmpegService>(sp =>
            {
                var ffmpegMock = new Mock<Listenarr.Api.Services.IFfmpegService>();
                var mockPath = Path.Combine(Path.GetTempPath(), "mock-ffprobe");
                ffmpegMock.Setup(f => f.GetFfprobePathAsync(It.IsAny<bool>()))
                    .ReturnsAsync(mockPath);
                ffmpegMock.Setup(f => f.EnsureFfprobeInstalledAsync())
                    .ReturnsAsync(mockPath);
                return ffmpegMock.Object;
            });

            // Provide a test-friendly IDownloadQueueService so DownloadService can be resolved
            services.AddSingleton<Listenarr.Api.Services.IDownloadQueueService>(sp =>
            {
                var downloadRepo = sp.GetService<Listenarr.Api.Repositories.IDownloadRepository>();
                var clientGateway = sp.GetService<IDownloadClientGateway>();
                var config = sp.GetService<IConfigurationService>();
                var logger = sp.GetService<ILogger<TestDownloadQueueService>>();
                var metrics = sp.GetService<IAppMetricsService>();
                var httpClient = sp.GetService<HttpClient>();

                if (downloadRepo != null && clientGateway != null && config != null && logger != null)
                {
                    return new TestDownloadQueueService(downloadRepo, clientGateway, config, logger, metrics, httpClient);
                }

                // Fallback: a minimal implementation that returns empty queue (pass through any optional services)
                return new TestDownloadQueueService(downloadRepo!, clientGateway!, config!, logger, metrics, httpClient);
            });

            // Provide a test-friendly ICompletedDownloadProcessor so DownloadService can be resolved in tests.
            services.AddSingleton<Listenarr.Api.Services.ICompletedDownloadProcessor>(sp =>
            {
                var downloadRepo = sp.GetService<Listenarr.Api.Repositories.IDownloadRepository>();
                var fileFinalizer = sp.GetService<Listenarr.Api.Services.IFileFinalizer>();
                var config = sp.GetService<IConfigurationService>();
                var scopeFactory = sp.GetService<IServiceScopeFactory>();
                var importService = sp.GetService<Listenarr.Api.Services.IImportService>();
                var archiveExtractor = sp.GetService<Listenarr.Api.Services.IArchiveExtractor>();
                var downloadQueue = sp.GetService<Listenarr.Api.Services.IDownloadQueueService>();
                var hubContext = sp.GetService<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
                var logger = sp.GetService<ILogger<Listenarr.Api.Services.CompletedDownloadProcessor>>();

                if (downloadRepo != null && fileFinalizer != null && config != null && scopeFactory != null && importService != null && archiveExtractor != null && downloadQueue != null && hubContext != null && logger != null)
                {
                    return new Listenarr.Api.Services.CompletedDownloadProcessor(downloadRepo, fileFinalizer, config, scopeFactory, importService, archiveExtractor, downloadQueue, hubContext, logger);
                }

                // Fallback: lightweight test processor that simply marks downloads completed in the repo
                return new TestCompletedDownloadProcessor(downloadRepo!);
            });

            configure?.Invoke(services);

            return services.BuildServiceProvider(validateScopes: true);
        }

        /// <summary>
        /// Convenience accessor.
        /// </summary>
        public static T GetRequiredService<T>(ServiceProvider provider) where T : notnull
            => provider.GetRequiredService<T>();
    }
}

internal class TestConfigurationService : Listenarr.Api.Services.IConfigurationService
{
    public Task<List<Listenarr.Domain.Models.ApiConfiguration>> GetApiConfigurationsAsync()
        => Task.FromResult(new List<Listenarr.Domain.Models.ApiConfiguration>());

    public Task<Listenarr.Domain.Models.ApiConfiguration?> GetApiConfigurationAsync(string id)
        => Task.FromResult<Listenarr.Domain.Models.ApiConfiguration?>(null);

    public Task<string> SaveApiConfigurationAsync(Listenarr.Domain.Models.ApiConfiguration config)
        => Task.FromResult(config.Id ?? string.Empty);

    public Task<bool> DeleteApiConfigurationAsync(string id)
        => Task.FromResult(false);

    public Task<List<Listenarr.Domain.Models.DownloadClientConfiguration>> GetDownloadClientConfigurationsAsync()
        => Task.FromResult(new List<Listenarr.Domain.Models.DownloadClientConfiguration>());

    public Task<Listenarr.Domain.Models.DownloadClientConfiguration?> GetDownloadClientConfigurationAsync(string id)
        => Task.FromResult<Listenarr.Domain.Models.DownloadClientConfiguration?>(null);

    public Task<string> SaveDownloadClientConfigurationAsync(Listenarr.Domain.Models.DownloadClientConfiguration config)
        => Task.FromResult(config.Id ?? string.Empty);

    public Task<bool> DeleteDownloadClientConfigurationAsync(string id)
        => Task.FromResult(false);

    public Task<Listenarr.Domain.Models.ApplicationSettings> GetApplicationSettingsAsync()
        => Task.FromResult(new Listenarr.Domain.Models.ApplicationSettings());

    public Task SaveApplicationSettingsAsync(Listenarr.Domain.Models.ApplicationSettings settings)
        => Task.CompletedTask;

    public Task<Listenarr.Domain.Models.StartupConfig> GetStartupConfigAsync()
        => Task.FromResult(new Listenarr.Domain.Models.StartupConfig());

    public Task SaveStartupConfigAsync(Listenarr.Domain.Models.StartupConfig config)
        => Task.CompletedTask;

    public Task<List<Listenarr.Domain.Models.WebhookConfiguration>> GetWebhookConfigurationsAsync()
        => Task.FromResult(new List<Listenarr.Domain.Models.WebhookConfiguration>());

    // Other IConfigurationService members (if added later) should be implemented here with sensible defaults.
}

// Minimal payload builder used by NotificationService during tests. Returns an empty payload and no attachment.
internal class TestNotificationPayloadBuilder : Listenarr.Api.Services.INotificationPayloadBuilder
{
    public JsonNode CreateDiscordPayload(string trigger, object data, string? startupBaseUrl)
    {
        return new JsonObject();
    }

    public Task<(JsonObject payload, Listenarr.Api.Services.NotificationAttachmentInfo? attachment)> CreateDiscordPayloadWithAttachmentAsync(
        string trigger,
        object data,
        string? startupBaseUrl,
        HttpClient httpClient,
        Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor = null,
        Action<string>? logInfo = null,
        Action<Exception, string>? logDebug = null)
    {
        var obj = new JsonObject();
        return Task.FromResult<(JsonObject, Listenarr.Api.Services.NotificationAttachmentInfo?)>((obj, null));
    }
}
