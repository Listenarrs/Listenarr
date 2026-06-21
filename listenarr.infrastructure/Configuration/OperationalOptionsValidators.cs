/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Options;

namespace Listenarr.Infrastructure.Configuration;

public sealed class FileMoverOptionsValidator : IValidateOptions<FileMoverOptions>
{
    public ValidateOptionsResult Validate(string? name, FileMoverOptions options)
    {
        var failures = new List<string>();
        if (options.RobocopyTimeoutMs is < 1_000 or > 3_600_000)
            failures.Add("FileMover:RobocopyTimeoutMs must be between 1000 and 3600000.");
        if (options.MaxRetries is < 0 or > 10)
            failures.Add("FileMover:MaxRetries must be between 0 and 10.");
        if (options.MinBackoffMs < 0)
            failures.Add("FileMover:MinBackoffMs cannot be negative.");
        if (options.MaxBackoffMs < options.MinBackoffMs)
            failures.Add("FileMover:MaxBackoffMs must be greater than or equal to MinBackoffMs.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class ExternalRequestOptionsValidator : IValidateOptions<ExternalRequestOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalRequestOptions options)
    {
        var failures = new List<string>();
        if (options.TimeoutSeconds is < 1 or > 600)
            failures.Add("ExternalRequests:TimeoutSeconds must be between 1 and 600.");
        if (options.MaxRetries is < 0 or > 10)
            failures.Add("ExternalRequests:MaxRetries must be between 0 and 10.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
