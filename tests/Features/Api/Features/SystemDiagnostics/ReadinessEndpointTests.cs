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
using System.Net.Http.Json;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Api.Features.SystemDiagnostics;

public sealed class ReadinessEndpointTests(ListenarrWebApplicationFactory factory)
    : IClassFixture<ListenarrWebApplicationFactory>
{
    [Fact]
    public async Task Ready_ReturnsDatabaseStateAndCorrelationHeader()
    {
        var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/system/ready");
        request.Headers.Add("X-Correlation-ID", "readiness-test-correlation");

        using var response = await client.SendAsync(request);
        var readiness = await response.Content.ReadFromJsonAsync<SystemReadiness>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(readiness);
        Assert.True(readiness.IsReady);
        Assert.True(readiness.DatabaseConnected);
        Assert.True(readiness.MigrationsCurrent);
        Assert.Equal(
            "readiness-test-correlation",
            response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Ready_Returns200_WhileFilesystemInitializationIsRunning()
    {
        using var initializingFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddScoped<ISystemReadinessService>(_ =>
                    Mock.Of<ISystemReadinessService>(service =>
                        service.CheckAsync(It.IsAny<CancellationToken>()) ==
                        Task.FromResult(new SystemReadiness(
                            true,
                            "ready",
                            true,
                            true,
                            null,
                            false,
                            "Running",
                            "AudiobookFileIdentities"))));
            });
        });
        var apiBase = TestUtils.ResolveApiBasePath(initializingFactory.Services);
        using var client = initializingFactory.CreateClient();

        using var response = await client.GetAsync($"{apiBase}/system/ready");
        var readiness = await response.Content.ReadFromJsonAsync<SystemReadiness>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(readiness);
        Assert.True(readiness.IsReady);
        Assert.False(readiness.FilesystemReady);
        Assert.Equal("Running", readiness.FilesystemStatus);
        Assert.Equal("AudiobookFileIdentities", readiness.FilesystemPhase);
    }

    [Fact]
    public async Task Ready_Returns503_WhenReadinessServiceRejectsHost()
    {
        using var notReadyFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddScoped<ISystemReadinessService>(_ =>
                    Mock.Of<ISystemReadinessService>(service =>
                        service.CheckAsync(It.IsAny<CancellationToken>()) ==
                        Task.FromResult(new SystemReadiness(
                            false,
                            "not_ready",
                            true,
                            false,
                            "pending_migrations"))));
            });
        });
        var apiBase = TestUtils.ResolveApiBasePath(notReadyFactory.Services);
        using var client = notReadyFactory.CreateClient();

        using var response = await client.GetAsync($"{apiBase}/system/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Ready_ReplacesInvalidCorrelationHeader()
    {
        var apiBase = TestUtils.ResolveApiBasePath(factory.Services);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/system/ready");
        request.Headers.Add("X-Correlation-ID", "invalid correlation value");

        using var response = await client.SendAsync(request);
        var correlationId = response.Headers.GetValues("X-Correlation-ID").Single();

        Assert.NotEqual("invalid correlation value", correlationId);
        Assert.All(
            correlationId,
            character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
    }
}
