/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class AudiobookRepository
{
    public async Task<Dictionary<(int AudiobookId, string Asin), string?>> GetAsinRegionsAsync(CancellationToken ct = default)
    {
        // Batch-loaded like the series memberships: the library list has no per-row Include
        var asinIdentifiers = await _db.AudiobookExternalIdentifiers
            .AsNoTracking()
            .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
            .OrderByDescending(i => i.IsPrimary)
            .Select(i => new { i.AudiobookId, i.ValueNormalized, i.Region })
            .ToListAsync(ct);

        return asinIdentifiers
            .GroupBy(i => (i.AudiobookId, i.ValueNormalized))
            .ToDictionary(g => g.Key, g => g.First().Region);
    }

    // Every ASIN identifier of the library in one query, grouped by book, the primary one first
    public async Task<Dictionary<int, List<AudiobookExternalIdentifier>>> GetAsinIdentifiersGroupedByAudiobookIdAsync(
        CancellationToken ct = default)
    {
        var identifiers = await _db.AudiobookExternalIdentifiers
            .AsNoTracking()
            .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
            .OrderByDescending(i => i.IsPrimary)
            .ThenBy(i => i.Id)
            .ToListAsync(ct);

        return identifiers
            .GroupBy(i => i.AudiobookId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}
