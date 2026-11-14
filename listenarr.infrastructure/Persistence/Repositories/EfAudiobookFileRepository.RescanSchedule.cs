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
using System.Linq.Expressions;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class EfAudiobookFileRepository
{
    // A size that is missing, or is the proc-link lstat sentinel an earlier
    // registration recorded, is missing metadata too. The filter of
    // IX_AudiobookFiles_MetadataRescanDue is the SQL EF emits for this
    // predicate, term for term; the query-plan test fails if they drift apart.
    private static readonly Expression<Func<AudiobookFile, bool>> MissingMetadata = file =>
        file.DurationSeconds == null
        || file.Format == null
        || file.SampleRate == null
        || file.Size == null
        || file.Size == AudiobookFile.ProcLinkLstatSize;

    public async Task<List<AudiobookFileRescanCandidate>> GetMetadataRescanDuePageAsync(
        DateTime nowUtc,
        IReadOnlyCollection<int> excludedIds,
        int pageSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(excludedIds);
        if (pageSize <= 0)
        {
            return [];
        }

        var hostSyntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        return await _db.AudiobookFiles
            .AsNoTracking()
            .Where(MissingMetadata)
            .Where(file => file.MetadataRescanDueUtc <= nowUtc)
            .Where(file => file.PathSyntax == null || file.PathSyntax == hostSyntax)
            .Where(file => !excludedIds.Contains(file.Id))
            .OrderBy(file => file.MetadataRescanDueUtc)
            .ThenBy(file => file.Id)
            .Take(pageSize)
            .Select(file => new AudiobookFileRescanCandidate(
                file,
                file.Audiobook == null ? null : file.Audiobook.BasePath))
            .ToListAsync(ct);
    }
}
