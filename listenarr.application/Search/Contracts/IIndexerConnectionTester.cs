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

/// <summary>
/// Defines provider-adjacent connection testing for a specific indexer implementation.
/// </summary>
public interface IIndexerConnectionTester
{
    string IndexerType { get; }

    Task<IndexerConnectionTestResult> TestAsync(
        Indexer indexer,
        CancellationToken cancellationToken = default);
}
