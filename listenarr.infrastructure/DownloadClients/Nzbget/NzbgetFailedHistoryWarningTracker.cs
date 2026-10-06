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

namespace Listenarr.Infrastructure.DownloadClients.Nzbget;

/// <summary>
/// Remembers the failed NZBGet history entries already warned about, per download client and
/// reading surface, so an entry NZBGet keeps in history is reported the first time it is seen
/// instead of on every poll. State lives in memory only, so each entry still in history warns
/// once more after a restart.
/// </summary>
internal sealed class NzbgetFailedHistoryWarningTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Surface, string ClientKey), HashSet<string>> _lastReadKeys = new();

    /// <summary>
    /// Records the failed entry keys one history read produced for a client and surface, and
    /// returns the ones not already recorded. Keys that are empty are not tracked. Pass
    /// isScopedRead when the read covered only part of the client's history, which is what a
    /// monitor poll does because it asks NZBGet about specific downloads.
    /// </summary>
    public IReadOnlySet<string> MarkFailed(
        string clientKey,
        string surface,
        IReadOnlyCollection<string> failedKeysThisRead,
        bool isScopedRead)
    {
        var currentKeys = failedKeysThisRead
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            var previousKeys = _lastReadKeys.GetValueOrDefault((surface, clientKey));

            // A scoped read saw only the entries it asked about, so absence from it is not
            // evidence that anything left NZBGet history. It may only add. Letting it replace
            // would drop every entry outside its scope, and the next unscoped read would then
            // report those as new all over again.
            //
            // An unscoped read did see the whole category, so replacing the stored set is what
            // evicts entries that have genuinely left history. That keeps memory bounded by
            // the size of the history itself with no expiry timer, and it still happens
            // regularly because the queue poller reads unscoped on a timer. An entry that
            // leaves history and later comes back is a new sighting and warns again, which is
            // the intent.
            //
            // The surface is part of the key because the surfaces read different slices, so
            // one surface's read must not evict what another surface saw.
            //
            // First sightings are computed before the stored set is touched, because the
            // scoped branch below unions into that same set in place.
            var firstSightings = currentKeys
                .Where(key => previousKeys?.Contains(key) != true)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // UnionWith rather than a collection expression: a collection expression builds a
            // fresh HashSet with the default comparer, which would silently drop the
            // OrdinalIgnoreCase comparison every other key path here relies on.
            if (isScopedRead && previousKeys != null)
            {
                previousKeys.UnionWith(currentKeys);
            }
            else
            {
                _lastReadKeys[(surface, clientKey)] = currentKeys;
            }

            return firstSightings;
        }
    }

    /// <summary>
    /// Collects the tracking keys of the failed entries in one history read, dropping the
    /// entries that carry neither an ID nor a title.
    /// </summary>
    public static IReadOnlyCollection<string> GetFailedEntryKeys(
        IEnumerable<NzbgetHistoryEntry> historyEntries)
    {
        return historyEntries
            .Where(entry => entry.Outcome == NzbgetHistoryOutcome.Failed)
            .Select(entry => GetEntryKey(entry.CanonicalNzbId, entry.Title))
            .Where(key => key.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Identifies a history entry by its canonical NZBID, falling back to its title when
    /// NZBGet supplies no ID. An entry with neither returns an empty key and is not tracked.
    /// </summary>
    public static string GetEntryKey(string canonicalNzbId, string title)
    {
        if (!string.IsNullOrWhiteSpace(canonicalNzbId))
        {
            return canonicalNzbId;
        }

        return string.IsNullOrWhiteSpace(title) ? string.Empty : title;
    }
}
