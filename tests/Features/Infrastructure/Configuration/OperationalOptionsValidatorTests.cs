/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Infrastructure.Configuration;

public sealed class OperationalOptionsValidatorTests
{
    [Fact]
    public void FileMoverOptions_InvalidBackoffAndTimeout_FailValidation()
    {
        var result = new FileMoverOptionsValidator().Validate(
            null,
            new FileMoverOptions
            {
                RobocopyTimeoutMs = 10,
                MaxRetries = 20,
                MinBackoffMs = 5_000,
                MaxBackoffMs = 1_000
            });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Contains("RobocopyTimeoutMs", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("MaxRetries", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("MaxBackoffMs", StringComparison.Ordinal));
    }

    [Fact]
    public void FileMoverOptions_UnknownWeakPublicationMode_FailsValidation()
    {
        var result = new FileMoverOptionsValidator().Validate(
            null,
            new FileMoverOptions
            {
                WeakPublicationMode = (WeakPublicationMode)999
            });

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "WeakPublicationMode",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ExternalRequestOptions_InvalidTimeoutAndRetries_FailValidation()
    {
        var result = new ExternalRequestOptionsValidator().Validate(
            null,
            new ExternalRequestOptions
            {
                TimeoutSeconds = 0,
                MaxRetries = 11
            });

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Failures.Count());
    }

    [Fact]
    public async Task ValidateOnStart_InvalidOperationalOptions_PreventHostStartup()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IValidateOptions<FileMoverOptions>, FileMoverOptionsValidator>();
                services.AddOptions<FileMoverOptions>()
                    .Configure(options => options.RobocopyTimeoutMs = 10)
                    .ValidateOnStart();
            })
            .Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("RobocopyTimeoutMs", StringComparison.Ordinal));
    }
}
