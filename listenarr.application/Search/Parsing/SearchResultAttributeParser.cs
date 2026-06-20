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
    private static readonly IReadOnlyDictionary<string, string> LanguageCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENG", "English" }, { "EN", "English" },
            { "DUT", "Dutch" },   { "NL", "Dutch" },
            { "GER", "German" },  { "DE", "German" },
            { "FRE", "French" },  { "FR", "French" }
        };

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

        var normalized = Regex.Replace(text, "\\s+", " ", RegexOptions.Compiled | RegexOptions.IgnoreCase).Trim();
        var alternation = string.Join("|", LanguageCodes.Keys.Select(Regex.Escape));
        var bracketedPattern = $@"[\[\(]\s*(?:{alternation})\b";
        var wordBoundaryPattern = $"\\b(?:{alternation})\\b";

        var bracketMatch = Regex.Match(normalized, bracketedPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        if (bracketMatch.Success)
        {
            var code = bracketMatch.Value.TrimStart('[', '(').Trim().Split(' ', '/', ',')[0];
            if (LanguageCodes.TryGetValue(code.ToUpperInvariant(), out var language)) return language;
        }

        var wordMatch = Regex.Match(normalized, wordBoundaryPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        if (wordMatch.Success)
        {
            var code = wordMatch.Value.Trim();
            if (LanguageCodes.TryGetValue(code.ToUpperInvariant(), out var language)) return language;
        }

        return null;
    }

    public static string? ParseLanguageFromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        return LanguageCodes.TryGetValue(code.ToUpperInvariant(), out var language)
            ? language
            : null;
    }
}
