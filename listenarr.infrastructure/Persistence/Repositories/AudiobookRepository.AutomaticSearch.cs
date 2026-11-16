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

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// The books the automatic search selects.
    ///
    /// Its own file because AudiobookRepository.cs sits at the 500-line cap that
    /// BackendArchitectureTests enforces.
    /// </summary>
    public partial class AudiobookRepository
    {
        public async Task<List<Audiobook>> GetMonitoredAudiobooksForSearchAsync(DateTime cutoff, CancellationToken ct = default)
        {
            // The check before an automatic download rebuilds the book's target fingerprint from a
            // read that carries its series memberships; assessed without them, a book in a second
            // series never matches and is never grabbed.
            return await _db.Audiobooks
                .Include(a => a.QualityProfile)
                .Include(a => a.SeriesMemberships)
                .Where(a => a.Monitored &&
                            a.QualityProfileId != null &&
                            (a.LastSearchTime == null || a.LastSearchTime < cutoff))
                .ToListAsync(ct);
        }
    }
}
