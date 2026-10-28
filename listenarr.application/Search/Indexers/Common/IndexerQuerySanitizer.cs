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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Indexers.Common;

public static class IndexerQuerySanitizer
{
    public static string Sanitize(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        const string forbidden = "*/\\<>:?|^~`$#%&+={}[]'\"!()";

        var sb = new StringBuilder(query.Length);
        foreach (var ch in query)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsControl(ch) || category == UnicodeCategory.Format)
                continue;

            // Normalize hyphens and dashes to spaces so a hyphenated title token (e.g.
            // "Full-Cast") is searched as separate words ("Full Cast"), matching how
            // Prowlarr/Sonarr clean query terms. This keeps tokenization consistent across
            // indexers and avoids passing a bare "-token" through to any indexer or proxy that
            // interprets a leading '-' as a query operator. Dropping the hyphen instead would
            // wrongly join to "FullCast".
            if (ch is '-' or '‐' or '‑' or '‒' or '–'
                or '—' or '―' or '−')
            {
                sb.Append(' ');
                continue;
            }

            if (forbidden.IndexOf(ch) >= 0)
                continue;

            if (ch == '\u2018' || ch == '\u2019' || ch == '\u201C' || ch == '\u201D')
                continue;

            sb.Append(ch);
        }

        return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
    }
}
