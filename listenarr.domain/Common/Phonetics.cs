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
namespace Listenarr.Domain.Common
{
    /// <summary>
    /// Phonetic token keys for fuzzy proper-noun comparison (ADR-0001).
    /// Speech-to-text mangles names far more than common words ("Weir" → "Ware",
    /// "Kowal" → "Cowell"), so the verification matcher compares author/narrator
    /// tokens by sound class as well as by edit distance. This is a Soundex-style
    /// consonant-class code, kept full-length (no 4-character truncation) so longer
    /// names don't all collapse to the same key.
    /// </summary>
    public static class Phonetics
    {
        /// <summary>
        /// Compute the phonetic key of a single token. Returns an empty string
        /// for tokens with no usable letters. Two tokens that sound alike map to
        /// the same key ("weir"/"ware" → "w6", "smith"/"smyth" → "s53").
        /// </summary>
        public static string Key(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;

            Span<char> letters = stackalloc char[token.Length];
            var n = 0;
            foreach (var c in token)
            {
                var lower = char.ToLowerInvariant(c);
                if (lower is >= 'a' and <= 'z') letters[n++] = lower;
            }
            if (n == 0) return string.Empty;

            // Canonicalize the leading sound: Soundex keeps the first LETTER
            // verbatim, which misses K/C ("Kowal"/"Cowell") and PH/F
            // ("Philip"/"Filip") confusions STT produces constantly.
            if (letters[0] == 'c') letters[0] = 'k';
            if (n >= 2 && letters[0] == 'p' && letters[1] == 'h')
            {
                letters[1] = 'f';
                letters = letters[1..];
                n--;
            }

            var result = new System.Text.StringBuilder(n);
            result.Append(letters[0]);

            var previousCode = CodeOf(letters[0]);
            for (var i = 1; i < n; i++)
            {
                var code = CodeOf(letters[i]);
                // 'h' and 'w' are transparent: they neither emit nor break a run
                // of same-coded consonants (standard Soundex behavior).
                if (letters[i] is 'h' or 'w') continue;
                if (code != '0' && code != previousCode) result.Append(code);
                previousCode = code;
            }

            return result.ToString();
        }

        /// <summary>True when both tokens have a non-empty, identical phonetic key.</summary>
        public static bool SoundAlike(string? a, string? b)
        {
            var keyA = Key(a);
            return keyA.Length > 0 && keyA == Key(b);
        }

        private static char CodeOf(char c) => c switch
        {
            'b' or 'f' or 'p' or 'v' => '1',
            'c' or 'g' or 'j' or 'k' or 'q' or 's' or 'x' or 'z' => '2',
            'd' or 't' => '3',
            'l' => '4',
            'm' or 'n' => '5',
            'r' => '6',
            _ => '0' // vowels + h/w carry no consonant class
        };
    }
}
