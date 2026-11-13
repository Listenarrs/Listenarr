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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Indexers.Common;

/// <summary>
/// In-memory ledger of hourly request counts per indexer, written through to
/// <see cref="IIndexerRequestUsageStore"/> so it survives a restart. The in-memory count is the
/// authority: a failed write is logged and the search goes ahead. Assumes one process per
/// database; a second instance on the same database would spend the same budget twice.
/// </summary>
public sealed class IndexerRequestBudget : IIndexerRequestBudget
{
    public const int MinInteractiveReservePercent = 0;
    public const int MaxInteractiveReservePercent = 90;

    // A request counts while its hour bucket starts no more than 24 hours before the current
    // hour, so the window spans 24 to 25 hours. That can over-count against an indexer's own
    // 24-hour or calendar-day window, never under-count.
    private static readonly TimeSpan CountedHistory = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<int, IndexerLedger> _ledgers = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly IIndexerRequestUsageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IndexerRequestBudget> _logger;
    private volatile bool _loaded;
    private DateTime _lastPrunedHourUtc = DateTime.MinValue;

    public IndexerRequestBudget(
        IIndexerRequestUsageStore store,
        TimeProvider timeProvider,
        ILogger<IndexerRequestBudget> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The most requests <paramref name="origin"/> may have in the window: the whole limit for
    /// an interactive search, the limit less the reserve (rounded down) for anything else.
    /// </summary>
    public static int CeilingFor(int dailyLimit, SearchOrigin origin, int interactiveReservePercent)
    {
        if (origin == SearchOrigin.Interactive)
            return dailyLimit;

        var reserve = Math.Clamp(interactiveReservePercent, MinInteractiveReservePercent, MaxInteractiveReservePercent);
        return (int)((long)dailyLimit * (100 - reserve) / 100);
    }

    public async Task<bool> TryAcquireAsync(Indexer indexer, SearchOrigin origin, int interactiveReservePercent)
    {
        if (indexer.DailyRequestLimit is not { } limit || limit <= 0)
            return true;

        await EnsureLoadedAsync();

        var hour = CurrentHourUtc();
        var ceiling = CeilingFor(limit, origin, interactiveReservePercent);
        var ledger = _ledgers.GetOrAdd(indexer.Id, _ => new IndexerLedger());
        int used;
        bool admitted;
        var logRefusal = false;
        lock (ledger)
        {
            used = ledger.CountSince(hour - CountedHistory);
            admitted = used < ceiling;
            if (admitted)
                ledger.Add(hour, hour - CountedHistory);
            else
                logRefusal = ledger.ClaimRefusalLog(hour, origin);
        }

        if (!admitted)
        {
            if (logRefusal)
            {
                _logger.LogInformation(
                    "Indexer {Name} has used its daily request budget for {Origin} searches ({Used} of {Ceiling} in the last 24 hours, limit {Limit}); not sending the request",
                    indexer.Name, origin, used, ceiling, limit);
            }
            return false;
        }

        await PersistAsync(indexer.Id, ledger, hour);
        return true;
    }

    public async Task<bool> HasSweepHeadroomAsync(IReadOnlyCollection<Indexer> indexers, int interactiveReservePercent)
    {
        if (indexers.Count == 0 || indexers.Any(indexer => indexer.DailyRequestLimit is not > 0))
            return true;

        await EnsureLoadedAsync();
        var hour = CurrentHourUtc();
        foreach (var indexer in indexers)
        {
            var ceiling = CeilingFor(indexer.DailyRequestLimit!.Value, SearchOrigin.Sweep, interactiveReservePercent);
            if (!_ledgers.TryGetValue(indexer.Id, out var ledger))
            {
                if (ceiling > 0)
                    return true;
                continue;
            }

            lock (ledger)
            {
                if (ledger.CountSince(hour - CountedHistory) < ceiling)
                    return true;
            }
        }

        return false;
    }

    private DateTime CurrentHourUtc()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;

        await _loadGate.WaitAsync();
        try
        {
            if (_loaded)
                return;

            try
            {
                var rows = await _store.LoadSinceAsync(CurrentHourUtc() - CountedHistory);
                foreach (var row in rows)
                {
                    var ledger = _ledgers.GetOrAdd(row.IndexerId, _ => new IndexerLedger());
                    lock (ledger)
                    {
                        ledger.Set(DateTime.SpecifyKind(row.HourStartUtc, DateTimeKind.Utc), row.Count);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Could not load indexer request usage; daily budgets count only requests sent since this start, and the stored counts for this hour may be overwritten with lower ones");
            }

            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task PersistAsync(int indexerId, IndexerLedger ledger, DateTime hour)
    {
        // Writes are serialised and each carries the count as it stands when written, so the
        // last write for a bucket is never lower than an earlier one.
        await _persistGate.WaitAsync();
        try
        {
            int count;
            lock (ledger)
            {
                count = ledger.CountAt(hour);
            }

            await _store.SaveAsync(indexerId, hour, count);
            if (hour > _lastPrunedHourUtc)
            {
                await _store.DeleteBeforeAsync(hour - CountedHistory);
                _lastPrunedHourUtc = hour;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogWarning(ex, "Could not persist request usage for indexer {IndexerId}; the in-memory count still applies", indexerId);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private sealed class IndexerLedger
    {
        private readonly Dictionary<DateTime, int> _buckets = new();
        private readonly Dictionary<SearchOrigin, DateTime> _refusalLoggedForHour = new();

        public int CountSince(DateTime earliestHour) =>
            _buckets.Where(bucket => bucket.Key >= earliestHour).Sum(bucket => bucket.Value);

        public int CountAt(DateTime hour) => _buckets.GetValueOrDefault(hour);

        public void Set(DateTime hour, int count) => _buckets[hour] = count;

        public void Add(DateTime hour, DateTime earliestHour)
        {
            foreach (var expired in _buckets.Keys.Where(key => key < earliestHour).ToList())
            {
                _buckets.Remove(expired);
            }
            _buckets[hour] = CountAt(hour) + 1;
        }

        // One refusal line per indexer, origin and hour, so an exhausted sweep does not log a
        // line for every remaining book.
        public bool ClaimRefusalLog(DateTime hour, SearchOrigin origin)
        {
            if (_refusalLoggedForHour.TryGetValue(origin, out var loggedHour) && loggedHour == hour)
                return false;

            _refusalLoggedForHour[origin] = hour;
            return true;
        }
    }
}
