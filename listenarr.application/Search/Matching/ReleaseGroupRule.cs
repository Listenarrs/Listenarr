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
    public static bool Match(string value, ReleaseAnnotationCatalog catalog) => catalog.Find(value) is { Kind: AnnotationKind.Group } entry
        && (entry.Placements & (AnnotationPlacement.Suffix | AnnotationPlacement.TechnicalSuffix)) != 0;

    public static ReleaseAnnotationResult Recognize(string value, bool beforeTitle, bool bracketed,
        bool technical, bool delimitedPrefix, ReleaseAnnotationCatalog catalog)
    {
        var entry = catalog.Find(value);
        if (entry == null && value.EndsWith(" iNT", StringComparison.OrdinalIgnoreCase)
            && catalog.Find(value[..^4]) is { Kind: AnnotationKind.Group } group) entry = group;
        if (entry == null) return default;
        var allowed = beforeTitle ? delimitedPrefix && entry.Placements.HasFlag(AnnotationPlacement.DelimitedPrefix)
            : entry.Placements.HasFlag(AnnotationPlacement.Suffix)
                || technical && entry.Placements.HasFlag(AnnotationPlacement.TechnicalSuffix)
                || bracketed && entry.Placements.HasFlag(AnnotationPlacement.BracketedSuffix);
        if (!allowed) return default;
        return new(AnnotationOutcome.Explained,
            entry.Kind == AnnotationKind.Group ? ReleaseSpanRole.GroupMarker : ReleaseSpanRole.TechnicalMetadata,
            entry.Id, entry.Name, "catalog");
    }
}
