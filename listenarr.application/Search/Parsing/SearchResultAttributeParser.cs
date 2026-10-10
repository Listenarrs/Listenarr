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

using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Parsing;

public static class SearchResultAttributeParser
{
    // Unambiguous tokens — 3-letter ISO codes, full/native names, and short codes that are
    // not common words — are trusted anywhere in a release title. Deliberately broad so a
    // non-English release (e.g. a Danish "...-DK-...") is flagged rather than grabbed as if
    // it were English.
    private static readonly IReadOnlyDictionary<string, string> LanguageCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENG", "English" }, { "EN", "English" }, { "English", "English" },
            { "DAN", "Danish" }, { "DA", "Danish" }, { "DK", "Danish" }, { "Danish", "Danish" }, { "Dansk", "Danish" },
            { "DUT", "Dutch" }, { "NLD", "Dutch" }, { "NL", "Dutch" }, { "Dutch", "Dutch" }, { "Nederlands", "Dutch" },
            { "GER", "German" }, { "DEU", "German" }, { "DE", "German" }, { "German", "German" }, { "Deutsch", "German" },
            { "FRE", "French" }, { "FRA", "French" }, { "FR", "French" }, { "French", "French" }, { "Francais", "French" }, { "Français", "French" },
            { "SPA", "Spanish" }, { "ES", "Spanish" }, { "Spanish", "Spanish" }, { "Espanol", "Spanish" }, { "Español", "Spanish" }, { "Castellano", "Spanish" },
            { "ITA", "Italian" }, { "Italian", "Italian" }, { "Italiano", "Italian" },
            { "SWE", "Swedish" }, { "SV", "Swedish" }, { "Swedish", "Swedish" }, { "Svenska", "Swedish" },
            { "NOR", "Norwegian" }, { "NB", "Norwegian" }, { "Norwegian", "Norwegian" }, { "Norsk", "Norwegian" },
            { "FIN", "Finnish" }, { "FI", "Finnish" }, { "Finnish", "Finnish" }, { "Suomi", "Finnish" },
            { "POR", "Portuguese" }, { "Portuguese", "Portuguese" }, { "Portugues", "Portuguese" }, { "Português", "Portuguese" },
            { "POL", "Polish" }, { "PL", "Polish" }, { "Polish", "Polish" }, { "Polski", "Polish" },
            { "RUS", "Russian" }, { "RU", "Russian" }, { "Russian", "Russian" },
            { "JPN", "Japanese" }, { "JA", "Japanese" }, { "JP", "Japanese" }, { "Japanese", "Japanese" },
            { "CHI", "Chinese" }, { "ZHO", "Chinese" }, { "ZH", "Chinese" }, { "Chinese", "Chinese" }, { "Mandarin", "Chinese" },
            { "CES", "Czech" }, { "CZE", "Czech" }, { "CS", "Czech" }, { "CZ", "Czech" }, { "Czech", "Czech" },
            { "HUN", "Hungarian" }, { "HU", "Hungarian" }, { "Hungarian", "Hungarian" }, { "Magyar", "Hungarian" },
            { "TUR", "Turkish" }, { "TR", "Turkish" }, { "Turkish", "Turkish" },
            { "ELL", "Greek" }, { "GRE", "Greek" }, { "EL", "Greek" }, { "Greek", "Greek" },
            { "KOR", "Korean" }, { "KO", "Korean" }, { "Korean", "Korean" },
            { "ISL", "Icelandic" }, { "Icelandic", "Icelandic" },
        };

    // Short codes that are also ordinary words ("it", "no", "se", "pt") — trusted only
    // inside [brackets] or (parentheses), never as a bare title token, so a scene tag like
    // "-SE-" is not mistaken for Swedish.
    private static readonly IReadOnlyDictionary<string, string> AmbiguousLanguageCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IT", "Italian" }, { "NO", "Norwegian" }, { "SE", "Swedish" }, { "PT", "Portuguese" },
        };

    private static readonly IReadOnlyDictionary<string, string> AllLanguageCodes =
        LanguageCodes.Concat(AmbiguousLanguageCodes)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    public static string DetectQualityFromTags(string tags)
    {
        var lowerTags = tags.ToLowerInvariant();

        if (lowerTags.Contains("flac"))
            return "FLAC";
        if (lowerTags.Contains("320") || lowerTags.Contains("320kbps"))
            return "MP3 320kbps";
        if (lowerTags.Contains("256") || lowerTags.Contains("256kbps"))
            return "MP3 256kbps";
        if (lowerTags.Contains("192") || lowerTags.Contains("192kbps"))
            return "MP3 192kbps";
        if (lowerTags.Contains("128") || lowerTags.Contains("128kbps"))
            return "MP3 128kbps";
        if (lowerTags.Contains("64") || lowerTags.Contains("64kbps"))
            return "MP3 64kbps";
        if (lowerTags.Contains("m4b"))
            return "M4B";

        return "Unknown";
    }

    public static string DetectQualityFromFormat(string format)
    {
        if (string.IsNullOrEmpty(format))
            return "Unknown";

        var lowerFormat = format.ToLowerInvariant();

        if (lowerFormat.Contains("flac"))
            return "FLAC";
        if (lowerFormat.Contains("m4b") || lowerFormat.Contains("apple audiobook"))
            return "M4B";
        if (lowerFormat.Contains("320kbps") || lowerFormat.Contains("320 kbps"))
            return "MP3 320kbps";
        if (lowerFormat.Contains("256kbps") || lowerFormat.Contains("256 kbps"))
            return "MP3 256kbps";
        if (lowerFormat.Contains("192kbps") || lowerFormat.Contains("192 kbps"))
            return "MP3 192kbps";
        if (lowerFormat.Contains("128kbps") || lowerFormat.Contains("128 kbps"))
            return "MP3 128kbps";
        if (lowerFormat.Contains("64kbps") || lowerFormat.Contains("64 kbps"))
            return "MP3 64kbps";
        if (lowerFormat.Contains("vbr mp3") || lowerFormat.Contains("variable bitrate"))
            return "MP3 VBR";
        if (lowerFormat.Contains("ogg vorbis") || lowerFormat.Contains("ogg"))
            return "OGG Vorbis";
        if (lowerFormat.Contains("opus"))
            return "OPUS";
        if (lowerFormat.Contains("aac"))
            return "AAC";
        if (lowerFormat.Contains("mp3"))
            return "MP3";

        return "Unknown";
    }

    public static string DetectFormatFromTags(string tags)
    {
        var lowerTags = tags.ToLowerInvariant();

        if (lowerTags.Contains("m4b"))
            return "M4B";
        if (lowerTags.Contains("flac"))
            return "FLAC";
        if (lowerTags.Contains("mp3"))
            return "MP3";
        if (lowerTags.Contains("opus"))
            return "OPUS";
        if (lowerTags.Contains("aac"))
            return "AAC";

        return "MP3";
    }

    public static string? ParseLanguageFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = Regex.Replace(text, "\\s+", " ", RegexOptions.Compiled).Trim();

        // Bracketed/parenthesized tags are high-confidence; trust every code there,
        // including the short ambiguous ones.
        var bracketed = MatchLanguageToken(normalized, AllLanguageCodes, bracketedOnly: true);
        if (bracketed != null) return bracketed;

        // Elsewhere only trust unambiguous tokens, so a bare scene tag like "-SE-" is not
        // read as a language.
        return MatchLanguageToken(normalized, LanguageCodes, bracketedOnly: false);
    }

    private static string? MatchLanguageToken(
        string normalized,
        IReadOnlyDictionary<string, string> codes,
        bool bracketedOnly)
    {
        // Longest tokens first so full names win over their abbreviations.
        var alternation = string.Join("|", codes.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        if (alternation.Length == 0) return null;

        var pattern = bracketedOnly
            ? $@"[\[\(]\s*(?<tok>{alternation})\b"
            : $@"\b(?<tok>{alternation})\b";
        var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
        return match.Success && codes.TryGetValue(match.Groups["tok"].Value, out var language)
            ? language
            : null;
    }

    public static string? ParseLanguageFromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        return AllLanguageCodes.TryGetValue(code.Trim(), out var language)
            ? language
            : null;
    }
}
