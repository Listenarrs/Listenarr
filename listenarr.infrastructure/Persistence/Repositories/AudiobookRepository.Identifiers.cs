/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class AudiobookRepository
{
    // Every ASIN identifier of the library in one query, grouped by book (for the library issues)
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
