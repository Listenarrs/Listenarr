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

internal static class NzbgetQueueFilter
{
    public static List<QueueItem> FilterByIds(
        List<QueueItem> items,
        List<string> ids,
        IReadOnlyList<NzbgetHistoryEnrichmentWorkflow.ActiveHistoryIdentity> activeIdentities)
    {
        if (ids.Count == 0)
        {
            return items;
        }

        var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = new List<QueueItem>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            if (idSet.Contains(item.Id))
            {
                filtered.Add(item);
                continue;
            }

            var activeIdentity = activeIdentities.FirstOrDefault(active => active.Matches(item.Id));
            var requestedId = activeIdentity?.MatchIds.FirstOrDefault(idSet.Contains);
            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                // NZBGet can expose GroupID or LastID on active queue rows while
                // Listenarr stores the canonical NZBID returned when the NZB was added.
                // For ID-filtered monitor polling, return the stored ID shape so the
                // generic gateway can reconcile the QueueItem back to the Download.
                item.Id = requestedId;
                filtered.Add(item);
            }
        }

        return filtered;
    }
}
