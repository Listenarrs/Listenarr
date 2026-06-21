/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Audiobooks.Contracts;

public interface IMoveQueuePersistence
{
    Task<MoveJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<MoveJob?> GetActiveByKeyAsync(string deduplicationKey, CancellationToken cancellationToken = default);

    Task AddAsync(MoveJob job, CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid id,
        string status,
        string? error,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
