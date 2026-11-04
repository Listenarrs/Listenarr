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

namespace Listenarr.Application.Search.Matching;

internal static class ReleaseTechnicalRule
{
    private const string Token = @"(?:a novel|novels?|edition|unabridged|abridged|unabr|unb|m4b|m4a|mp3|flac|aac|opus|audiobook|audio book|audio|chapterized|variable|dcrypt\d{2,4}k|\d{2,4}\s*(?:kbps|kb/s|k)|\d{4})";
    private static readonly Regex Technical = new(
        @"^\.?" + Token + @"(?:[\s.!。]+" + Token + @")*[\s.!。]*$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool Match(string value) => Technical.IsMatch(value);

    private static readonly Regex Size = new(
        @"^(?<size>\d{1,9}(?:\.\d{1,3})?)\s*(?:KB|MB|GB|KiB|MiB|GiB)$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SceneSuffix = new(
        @"^WEB[- ](?:SE|EN|DE|FR|ES|IT|NL)[- ]\d{4}[- ](?<group>[a-z0-9]{2,32}) iNT$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Genres = new(
        ["Horror", "Thriller", "Sci-Fi", "Sci Fi", "Science Fiction", "Classic Fiction"], StringComparer.OrdinalIgnoreCase);

    public static ReleaseAnnotationResult Recognize(string value, bool bracketed, bool signed)
    {
        if (Match(value)) return new(AnnotationOutcome.Explained, ReleaseSpanRole.TechnicalMetadata, "technical.tokens");
        var size = !signed && value.Length > 0 && char.IsAsciiDigit(value[0]) ? Size.Match(value) : null;
        if (size?.Success == true && decimal.TryParse(size.Groups["size"].Value,
            System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount > 0)
            return new(AnnotationOutcome.Explained, ReleaseSpanRole.TechnicalMetadata, "technical.size");
        var scene = value.StartsWith("WEB", StringComparison.OrdinalIgnoreCase) ? SceneSuffix.Match(value) : null;
        if (scene?.Success == true && ReleaseGroupRule.Match(scene.Groups["group"].Value))
            return new(AnnotationOutcome.Explained, ReleaseSpanRole.TechnicalMetadata, "technical.scene_suffix");
        return bracketed && Genres.Contains(value)
            ? new(AnnotationOutcome.Explained, ReleaseSpanRole.TechnicalMetadata, "technical.genre") : default;
    }
}
