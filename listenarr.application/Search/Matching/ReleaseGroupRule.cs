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

namespace Listenarr.Application.Search.Matching;

internal static class ReleaseGroupRule
{
    // Opaque group names are data. Position alone cannot distinguish a group from another work.
    private static readonly HashSet<string> Groups = new(["NMR", "PoF", "BoB", "BBtv", "miok", "JD07", "FerraBit", "CRAViNGS"], StringComparer.OrdinalIgnoreCase);
    public static bool Match(string value) => Groups.Contains(value);

    public static ReleaseAnnotationResult Recognize(string value, bool beforeTitle)
    {
        if (beforeTitle) return default;
        var name = value.EndsWith(" iNT", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
        return Match(name) ? new(AnnotationOutcome.Explained, ReleaseSpanRole.GroupMarker, "group.catalog", name, "catalog") : default;
    }
}
