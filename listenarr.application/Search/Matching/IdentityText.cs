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
using System.Text;

namespace Listenarr.Application.Search.Matching;

/// <summary>Complete ordered text comparison. No stop words, length floor or accent removal.</summary>
public static class IdentityText
{
    public static string Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (value.Length > 16384) throw new ArgumentException("Identity text exceeds assessment budget");
        var text = value.Normalize(NormalizationForm.FormC);
        var output = new StringBuilder(text.Length);
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar) throw new ArgumentException("Invalid identity text");
            if (Rune.IsWhiteSpace(rune)) { space = output.Length > 0; continue; }
            if (space) output.Append(' ');
            space = false;
            output.Append(rune.Value switch
            {
                0x2018 or 0x2019 or 0x02bc => "'",
                0xff1a => ":",
                0xff0c => ",",
                0xff1b => ";",
                0xff08 => "(",
                0xff09 => ")",
                0xff3b => "[",
                0xff3d => "]",
                _ => rune.ToString()
            });
        }
        return output.ToString();
    }

    public static bool Equal(string? a, string? b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsContributorName(string value) => value.Length > 0
        && !string.Equals(value, "Unknown Author", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase);

    // Explicit presentation variants, never word deletion/reordering or accent folding.
    public static string Presentation(string value) =>
        Canonical(value).Replace("'", "", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);

    public static bool PersonEqual(string a, string b) =>
        Equal(Canonical(a).Replace(".", "", StringComparison.Ordinal),
            Canonical(b).Replace(".", "", StringComparison.Ordinal));
}
