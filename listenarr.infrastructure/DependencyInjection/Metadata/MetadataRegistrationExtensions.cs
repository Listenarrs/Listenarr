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
using Listenarr.Infrastructure.DependencyInjection.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace Listenarr.Infrastructure.DependencyInjection.Metadata;

internal static class MetadataRegistrationExtensions
{
    private const int MaxRetryAttempts = 3;

    private static readonly TimeSpan MaxRetryAfterHonored = TimeSpan.FromSeconds(60);

    public static IServiceCollection AddMetadataHttpClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var retryPolicy = CreateExternalMetadataRetryPolicy();
        services.AddHttpClient<AudibleService>()
            .ConfigurePrimaryHttpMessageHandler(PlatformRegistrationExtensions.CreateExternalHandler)
            .AddPolicyHandler(retryPolicy);
        services.AddHttpClient<IAudnexusService, AudnexusService>()
            .ConfigurePrimaryHttpMessageHandler(PlatformRegistrationExtensions.CreateExternalHandler)
            .AddPolicyHandler(retryPolicy);
        return services;
    }

    public static IServiceCollection AddMetadataServices(this IServiceCollection services)
    {
        services.AddScoped<IMetadataService, MetadataService>();
        services.AddScoped<IAsinLookupService, AsinLookupService>();
        services.AddScoped<IAudiobookMetadataService, AudiobookMetadataService>();
        services.AddHttpClient<IOpenLibraryService, OpenLibraryService>()
            .AddPolicyHandler(CreateExternalMetadataRetryPolicy());
        services.AddSingleton<MetadataExtractionLimiter>();
        services.AddHttpClient("Ffmpeg");
        services.AddSingleton<IFfmpegService>(provider =>
            new FfmpegService(
                provider.GetRequiredService<ILogger<FfmpegService>>(),
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("Ffmpeg"),
                provider.GetRequiredService<IStartupConfigService>(),
                provider.GetRequiredService<IProcessRunner>(),
                provider.GetRequiredService<IApplicationPathService>()));
        return services;
    }

    /// <summary>
    /// Retry policy for third-party metadata APIs.
    /// </summary>
    /// <remarks>
    /// HandleTransientHttpError covers 5xx and 408 but deliberately excludes 429, which is the
    /// only status a rate limiter actually returns - so without the extra OrResult clause a
    /// throttled request fails immediately and the caller records it as "no match found".
    /// </remarks>
    private static IAsyncPolicy<HttpResponseMessage> CreateExternalMetadataRetryPolicy() =>
        HttpPolicyExtensions.HandleTransientHttpError()
            .OrResult(response => response.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                MaxRetryAttempts,
                (attempt, outcome, _) => ComputeRetryDelay(attempt, outcome),
                (_, _, _, _) => Task.CompletedTask);

    /// <summary>
    /// Exponential backoff, overridden by a longer Retry-After hint when the server sends one.
    /// </summary>
    private static TimeSpan ComputeRetryDelay(int attempt, DelegateResult<HttpResponseMessage> outcome)
    {
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));

        // Result is null when the attempt threw rather than returning a response.
        var retryAfter = outcome.Result?.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return backoff;
        }

        // Retry-After is either delta-seconds or an HTTP-date; both forms are in the wild.
        var hinted = retryAfter.Delta
            ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (hinted is null || hinted <= TimeSpan.Zero)
        {
            return backoff;
        }

        // Cap the hint so a mistaken or hostile header cannot stall the pipeline indefinitely.
        var capped = hinted.Value > MaxRetryAfterHonored ? MaxRetryAfterHonored : hinted.Value;
        return capped > backoff ? capped : backoff;
    }

    public static IServiceCollection AddMetadataInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IHtmlTextExtractor, HtmlAgilityPackTextExtractor>();
        services.AddSingleton<IAudibleAuthorPageParser, HtmlAgilityPackAudibleAuthorPageParser>();
        services.AddScoped<IAudioTagWriter, TagLibAudioTagWriter>();
        services.AddHttpClient<ICoverImageProbe, ImageSharpCoverImageProbe>();
        services.AddHttpClient<ImageCacheService>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            });
        services.AddSingleton<IImageCacheService, ImageCacheService>();
        return services;
    }
}
