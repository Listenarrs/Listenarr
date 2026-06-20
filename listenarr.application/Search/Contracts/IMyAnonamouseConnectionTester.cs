/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Application.Search.Contracts;

public interface IMyAnonamouseConnectionTester
{
    Task<MyAnonamouseConnectionTestResult> TestAsync(
        Indexer indexer,
        string mamId,
        CancellationToken cancellationToken = default);
}

public sealed record MyAnonamouseConnectionTestResult(
    bool Succeeded,
    string Message,
    int? StatusCode = null,
    string? RefreshedMamId = null)
{
    public static MyAnonamouseConnectionTestResult Success(string? refreshedMamId = null)
        => new(true, "MyAnonamouse authentication successful", RefreshedMamId: refreshedMamId);

    public static MyAnonamouseConnectionTestResult Failure(string message, int? statusCode = null)
        => new(false, message, statusCode);
}
