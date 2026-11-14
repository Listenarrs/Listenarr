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
using Microsoft.Extensions.Logging;

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

    // Declared after MissingMetadata in the same file, so static initialisation
    // has already built the predicate it negates.
    private static readonly Expression<Func<AudiobookFile, bool>> HasMetadata =
        Expression.Lambda<Func<AudiobookFile, bool>>(
            Expression.Not(MissingMetadata.Body),
            MissingMetadata.Parameters);

    private const string CompletedOutcome = nameof(MetadataRescanOutcome.Completed);
    private const string NotOnThisHostOutcome = nameof(MetadataRescanOutcome.NotOnThisHost);

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

    public async Task<bool> ApplyMetadataRescanScheduleAsync(
        int fileId,
        AudiobookFileRescanCounts expectedCounts,
        AudiobookFileRescanScheduleWrite write,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (await WriteRescanScheduleAsync(
                RescanScheduleCompareAndSet(fileId, expectedCounts),
                fileId,
                write.DueUtc,
                write.Counts,
                write.LastOutcome,
                ct))
        {
            return true;
        }

        _logger.LogDebug(
            "Rescan outcome {Outcome} for file {FileId} was not written: its schedule changed after it was read, or the row is gone",
            write.LastOutcome,
            fileId);
        return false;
    }

    public async Task<AudiobookFileRescanRefreshRecord> RecordMetadataRescanRefreshedAsync(
        int fileId,
        AudiobookFileRescanCounts expectedCounts,
        AudiobookFileRescanScheduleWrite incompleteWrite,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(incompleteWrite);
        if (await WriteRescanScheduleAsync(
                RescanScheduleCompareAndSet(fileId, expectedCounts).Where(MissingMetadata),
                fileId,
                incompleteWrite.DueUtc,
                incompleteWrite.Counts,
                incompleteWrite.LastOutcome,
                ct))
        {
            return AudiobookFileRescanRefreshRecord.Incomplete;
        }

        // The row has its metadata now, or another outcome write won the
        // compare-and-set. Only the first is Completed; a row that still misses
        // metadata keeps what the other write recorded.
        if (await WriteRescanScheduleAsync(
                _db.AudiobookFiles.Where(file => file.Id == fileId).Where(HasMetadata),
                fileId,
                null,
                new AudiobookFileRescanCounts(0, 0, 0),
                CompletedOutcome,
                ct))
        {
            return AudiobookFileRescanRefreshRecord.Completed;
        }

        _logger.LogDebug(
            "Rescan refresh outcome for file {FileId} was not written: it still misses metadata and its schedule changed after it was read, or the row is gone",
            fileId);
        return AudiobookFileRescanRefreshRecord.NotRecorded;
    }

    public async Task<List<AudiobookFileRescanForeignRow>> GetDueForeignSyntaxRescanRowsAsync(
        DateTime nowUtc,
        FileSystemPathSyntax hostSyntax,
        int bound,
        CancellationToken ct = default)
    {
        if (bound <= 0)
        {
            return [];
        }

        return await DueForeignSyntaxRescanRows(nowUtc, hostSyntax)
            .AsNoTracking()
            .OrderBy(file => file.MetadataRescanDueUtc)
            .ThenBy(file => file.Id)
            .Take(bound)
            .Select(file => new AudiobookFileRescanForeignRow(file.Id, file.MetadataRescanUnavailableCount))
            .ToListAsync(ct);
    }

    public async Task<int> DeferForeignSyntaxRescanRowsAsync(
        IReadOnlyCollection<AudiobookFileRescanForeignRow> rows,
        DateTime nowUtc,
        FileSystemPathSyntax hostSyntax,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var written = 0;
        var completionToken = RequestCancellationBoundary.EnterNonCancelablePhase(ct);

        // One statement per step of the unavailable ladder. Below the cap a step
        // holds one count; the capped step holds every count from the cap on, so
        // the statement increments the count and guards on the step's count range.
        foreach (var step in rows.GroupBy(row =>
                     MetadataRescanSchedulePolicy.UnavailableDelay(row.UnavailableCount + 1)))
        {
            var ids = step.Select(row => row.Id).ToList();
            var lowestCount = step.Min(row => row.UnavailableCount);
            var highestCount = step.Max(row => row.UnavailableCount);
            var query = DueForeignSyntaxRescanRows(nowUtc, hostSyntax).Where(file =>
                ids.Contains(file.Id)
                && file.MetadataRescanUnavailableCount >= lowestCount
                && file.MetadataRescanUnavailableCount <= highestCount);
            if (_db.Database.IsRelational())
            {
                // NotOnThisHost's counter rule in MetadataRescanSchedulePolicy:
                // unavailable + 1, blocked 0, failure kept.
                var dueUtc = nowUtc + step.Key;
                written += await query.ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(file => file.MetadataRescanDueUtc, dueUtc)
                        .SetProperty(
                            file => file.MetadataRescanUnavailableCount,
                            file => file.MetadataRescanUnavailableCount + 1)
                        .SetProperty(file => file.MetadataRescanBlockedCount, 0)
                        .SetProperty(file => file.MetadataRescanLastOutcome, NotOnThisHostOutcome),
                    completionToken);
                continue;
            }

            var existing = await query.ToListAsync(completionToken);
            foreach (var file in existing)
            {
                var change = MetadataRescanSchedulePolicy.Apply(
                    MetadataRescanOutcome.NotOnThisHost,
                    new MetadataRescanCounters(
                        file.MetadataRescanFailureCount,
                        file.MetadataRescanUnavailableCount,
                        file.MetadataRescanBlockedCount),
                    nowUtc)!.Value;
                ApplyRescanSchedule(
                    file,
                    change.DueUtc,
                    new AudiobookFileRescanCounts(
                        change.Counters.Failure,
                        change.Counters.Unavailable,
                        change.Counters.Blocked),
                    NotOnThisHostOutcome);
            }

            await _db.SaveChangesAsync(completionToken);
            written += existing.Count;
        }

        if (_db.Database.IsRelational())
        {
            await SynchronizeTrackedRescanSchedulesAsync(
                rows.Select(row => row.Id).ToHashSet(),
                completionToken);
        }

        if (written < rows.Count)
        {
            _logger.LogDebug(
                "{Skipped} of {Rows} foreign-syntax rescan rows were not deferred: their schedule or path changed after they were read",
                rows.Count - written,
                rows.Count);
        }

        return written;
    }

    private IQueryable<AudiobookFile> RescanScheduleCompareAndSet(
        int fileId,
        AudiobookFileRescanCounts expectedCounts) =>
        _db.AudiobookFiles.Where(file =>
            file.Id == fileId
            && file.MetadataRescanFailureCount == expectedCounts.Failure
            && file.MetadataRescanUnavailableCount == expectedCounts.Unavailable
            && file.MetadataRescanBlockedCount == expectedCounts.Blocked);

    private IQueryable<AudiobookFile> DueForeignSyntaxRescanRows(
        DateTime nowUtc,
        FileSystemPathSyntax hostSyntax) =>
        _db.AudiobookFiles
            .Where(MissingMetadata)
            .Where(file => file.MetadataRescanDueUtc <= nowUtc)
            .Where(file => file.PathSyntax != null && file.PathSyntax != hostSyntax);

    // Writes the schedule to the one row the query matches. A null due time
    // leaves the stored due time as it is.
    private async Task<bool> WriteRescanScheduleAsync(
        IQueryable<AudiobookFile> query,
        int fileId,
        DateTime? dueUtc,
        AudiobookFileRescanCounts counts,
        string lastOutcome,
        CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            var completionToken = RequestCancellationBoundary.EnterNonCancelablePhase(ct);
            var updated = await query.ExecuteUpdateAsync(
                setters =>
                {
                    if (dueUtc.HasValue)
                    {
                        setters.SetProperty(file => file.MetadataRescanDueUtc, dueUtc.Value);
                    }

                    setters
                        .SetProperty(file => file.MetadataRescanFailureCount, counts.Failure)
                        .SetProperty(file => file.MetadataRescanUnavailableCount, counts.Unavailable)
                        .SetProperty(file => file.MetadataRescanBlockedCount, counts.Blocked)
                        .SetProperty(file => file.MetadataRescanLastOutcome, lastOutcome);
                },
                completionToken);
            if (updated != 1)
            {
                return false;
            }

            SynchronizeTrackedRescanSchedule(fileId, dueUtc, counts, lastOutcome);
            return true;
        }

        var existing = await query.SingleOrDefaultAsync(ct);
        if (existing == null)
        {
            return false;
        }

        ApplyRescanSchedule(existing, dueUtc, counts, lastOutcome);
        var nonRelationalCompletionToken = RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        await _db.SaveChangesAsync(nonRelationalCompletionToken);
        return true;
    }

    // Marks each property modified even when the tracked value already equals
    // the write: a stale tracked copy can hold a value the store no longer has.
    private void ApplyRescanSchedule(
        AudiobookFile target,
        DateTime? dueUtc,
        AudiobookFileRescanCounts counts,
        string lastOutcome)
    {
        var entry = _db.Entry(target);
        if (dueUtc.HasValue)
        {
            Write(nameof(AudiobookFile.MetadataRescanDueUtc), dueUtc.Value);
        }

        Write(nameof(AudiobookFile.MetadataRescanFailureCount), counts.Failure);
        Write(nameof(AudiobookFile.MetadataRescanUnavailableCount), counts.Unavailable);
        Write(nameof(AudiobookFile.MetadataRescanBlockedCount), counts.Blocked);
        Write(nameof(AudiobookFile.MetadataRescanLastOutcome), lastOutcome);

        void Write(string propertyName, object value)
        {
            var property = entry.Property(propertyName);
            property.CurrentValue = value;
            property.IsModified = true;
        }
    }

    // ExecuteUpdate bypasses tracking. A tracked copy of the row takes the
    // written schedule as both its current and original values, so UpdateAsync
    // (which restores the schedule to its original values) and any later save
    // of that copy keep the write instead of the values it was loaded with.
    private void SynchronizeTrackedRescanSchedule(
        int fileId,
        DateTime? dueUtc,
        AudiobookFileRescanCounts counts,
        string? lastOutcome)
    {
        var trackedEntry = _db.ChangeTracker.Entries<AudiobookFile>()
            .FirstOrDefault(entry => entry.Entity.Id == fileId);
        if (trackedEntry == null)
        {
            return;
        }

        if (dueUtc.HasValue)
        {
            Synchronize(nameof(AudiobookFile.MetadataRescanDueUtc), dueUtc.Value);
        }

        Synchronize(nameof(AudiobookFile.MetadataRescanFailureCount), counts.Failure);
        Synchronize(nameof(AudiobookFile.MetadataRescanUnavailableCount), counts.Unavailable);
        Synchronize(nameof(AudiobookFile.MetadataRescanBlockedCount), counts.Blocked);
        Synchronize(nameof(AudiobookFile.MetadataRescanLastOutcome), lastOutcome);

        void Synchronize(string propertyName, object? value)
        {
            var property = trackedEntry.Property(propertyName);
            property.CurrentValue = value;
            property.OriginalValue = value;
            property.IsModified = false;
        }
    }

    // The set-based deferral does not report which rows it matched, so tracked
    // copies are synchronised from the stored schedule.
    private async Task SynchronizeTrackedRescanSchedulesAsync(
        IReadOnlySet<int> fileIds,
        CancellationToken completionToken)
    {
        var trackedIds = _db.ChangeTracker.Entries<AudiobookFile>()
            .Select(entry => entry.Entity.Id)
            .Where(fileIds.Contains)
            .ToList();
        if (trackedIds.Count == 0)
        {
            return;
        }

        var stored = await _db.AudiobookFiles
            .AsNoTracking()
            .Where(file => trackedIds.Contains(file.Id))
            .Select(file => new
            {
                file.Id,
                file.MetadataRescanDueUtc,
                Counts = new AudiobookFileRescanCounts(
                    file.MetadataRescanFailureCount,
                    file.MetadataRescanUnavailableCount,
                    file.MetadataRescanBlockedCount),
                file.MetadataRescanLastOutcome
            })
            .ToListAsync(completionToken);
        foreach (var schedule in stored)
        {
            SynchronizeTrackedRescanSchedule(
                schedule.Id,
                schedule.MetadataRescanDueUtc,
                schedule.Counts,
                schedule.MetadataRescanLastOutcome);
        }
    }
}
