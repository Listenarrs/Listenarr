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
using System.Text.RegularExpressions;

namespace Listenarr.Infrastructure.Search.Providers.Torznab;

internal static class TorznabNewznabValueParser
{
    public static long ParseSize(string sizeStr)
    {
        if (string.IsNullOrWhiteSpace(sizeStr))
            return 0;

        // Try parsing as a plain number first (bytes)
        if (long.TryParse(sizeStr, out var bytes))
            return bytes;

        // Parse human-readable sizes like "1.5 GB", "3.7 GiB", "500 MB", etc.
        // Support both binary (GiB, MiB, TiB, KiB) and decimal (GB, MB, TB, KB) units
        var match = Regex.Match(sizeStr, @"([\d\.]+)\s*([KMGT]i?B)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return 0;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
            return 0;

        var unit = match.Groups[2].Value.ToUpper();
        return unit switch
        {
            "TIB" => (long)(size * 1024 * 1024 * 1024 * 1024),
            "TB" => (long)(size * 1024 * 1024 * 1024 * 1024),
            "GIB" => (long)(size * 1024 * 1024 * 1024),
            "GB" => (long)(size * 1024 * 1024 * 1024),
            "MIB" => (long)(size * 1024 * 1024),
            "MB" => (long)(size * 1024 * 1024),
            "KIB" => (long)(size * 1024),
            "KB" => (long)(size * 1024),
            "B" => (long)size,
            _ => 0
        };
    }

    public static string? ParseLanguageFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Normalize whitespace
        var normalized = Regex.Replace(text, "\\s+", " ", RegexOptions.Compiled | RegexOptions.IgnoreCase).Trim();

        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENG", "English" }, { "EN", "English" },
            { "DUT", "Dutch" },    { "NL", "Dutch" },
            { "GER", "German" },   { "DE", "German" },
            { "FRE", "French" },   { "FR", "French" }
        };

        // Build a joined alternation like ENG|EN|DUT|NL|...
        var alternation = string.Join("|", codes.Keys.Select(Regex.Escape));

        // Bracketed or parenthesis forms: [ ENG / ... ] or (EN)
        var bracketedPattern = $@"[\[\(]\s*(?:{alternation})\b";

        // Standalone word boundary pattern: \b(ENG|EN|DUT|NL|...)\b
        var standalonePattern = $@"\b(?:{alternation})\b";

        // Try bracketed first (higher confidence)
        var m = Regex.Match(normalized, bracketedPattern, RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var captured = Regex.Match(m.Value, $@"(?:{alternation})", RegexOptions.IgnoreCase);
            if (captured.Success && codes.TryGetValue(captured.Value, out var lang))
                return lang;
        }

        // Try standalone word boundary
        m = Regex.Match(normalized, standalonePattern, RegexOptions.IgnoreCase);
        if (m.Success && codes.TryGetValue(m.Value, out var lang2))
            return lang2;

        return null;
    }
}
