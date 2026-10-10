/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Search.Models;

public sealed record IndexerConnectionTestResult(
    bool Succeeded,
    string Message,
    string? Error = null,
    int? StatusCode = null,
    string? Collection = null,
    string? MamId = null)
{
    public static IndexerConnectionTestResult Success(
        string message,
        string? collection = null,
        string? mamId = null)
        => new(true, message, Collection: collection, MamId: mamId);

    public static IndexerConnectionTestResult Failure(
        string message,
        string? error = null,
        int? statusCode = null)
        => new(false, message, error, statusCode);
}
