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

namespace Listenarr.Application.Search.Core
{
    /// <summary>
    /// Provides validation and filtering logic for search results and ASINs.
    /// </summary>
    public static class SearchValidation
    {
        /// <summary>
        /// Validates Amazon ASIN format: 10 characters, alphanumeric, starting with B0 or digit.
        /// </summary>
        public static bool IsValidAsin(string asin)
        {
            if (string.IsNullOrEmpty(asin))
                return false;

            // Amazon ASIN format: 10 characters, typically starting with B0 or digits
            return asin.Length == 10 &&
                (asin.StartsWith("B0") || char.IsDigit(asin[0])) &&
                asin.All(c => char.IsLetterOrDigit(c));
        }

        /// <summary>
        /// Detects if a title is navigation noise, UI elements, or invalid content.
        /// </summary>
        public static bool IsTitleNoise(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;

            var t = title.Trim();

            // Common noise phrases that appear in search results
            string[] noisePhrases = new[]
            {
                "No results", "Suggested Searches", "No results found", "Try again",
                "Browse categories", "Customer Service", "Help", "Search", "Menu",
                "Sign in", "Account", "Audible.com", "Language", "Currency"
            };

            // Check if title contains any noise phrases
            if (noisePhrases.Any(p => t.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            // Check if title is mostly whitespace/newlines
            if (t.All(c => char.IsWhiteSpace(c) || c == '\n' || c == '\r'))
                return true;

            // Check for excessive newlines (typical of scraped navigation elements)
            if (t.Count(c => c == '\n') > 2)
                return true;

            return false;
        }

        /// <summary>
        /// Detects promotional titles like "Unlock 15% savings" or "Visit the Store".
        /// </summary>
        public static bool IsPromotionalTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            var t = title.Trim();
            var lower = t.ToLowerInvariant();

            // Percent discounts like '15%'
            if (Regex.IsMatch(t, @"\b\d+%")) return true;

            // Phrases like 'Unlock 15% savings', 'Unlock X%'
            if (lower.Contains("unlock") && (lower.Contains("save") || lower.Contains("savings") || Regex.IsMatch(lower, "\\d+%"))) return true;

            // 'Visit the <brand> Store' promotional links
            if (Regex.IsMatch(lower, "visit the .*store")) return true;

            // Short promo starts
            if (lower.StartsWith("unlock ") || lower.StartsWith("save ") || lower.StartsWith("visit the ")) return true;

            return false;
        }

        /// <summary>
        /// Detects if author field contains noise or navigation elements.
        /// </summary>
        public static bool IsAuthorNoise(string? author)
        {
            if (string.IsNullOrWhiteSpace(author)) return true;
            var a = author.Trim();
            // Filter out common header/navigation noise in author fields
            if (a.Length < 2) return true;
            if (a.Equals("Authors", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.Equals("By:", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("Sort by", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.Contains("English - USD", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Heuristic: detects product-like titles that are unlikely to be audiobooks.
        /// </summary>
        public static bool IsProductLikeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            var t = title.Trim();
            var lower = t.ToLowerInvariant();

            // Quick rejects for extremely long descriptive product titles
            if (t.Length > 200) return true;

            // Common product keywords that rarely appear in audiobook titles
            string[] productKeywords = new[]
            {
                "led","lamp","light","charger","battery","watt","volt","usb","hdmi","case","cover","shirt",
                "socks","decor","decoration","decorations","gift","necklace","bracelet","ring","halloween",
                "christmas","remote","plug","adapter","holder","stand","tool","kit","pack of","set of","piece",
                "cm","mm","inch","inches","oz","ml","capacity","dimensions","material","fabric","men's","women's"
            };

            if (productKeywords.Any(k => lower.Contains(k))) return true;

            // Model / pack patterns: 'Pack of 2', 'Set of 3', 'x cm', numeric dimension patterns
            if (Regex.IsMatch(lower, "\\b(pack of|set of|set x|\bx\b|\bpcs?\b|\\bqty\\b|\\bpiece\\b)", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(t, "\\b\\d{1,4}\\s?(cm|mm|in|inches|oz|ml)\\b", RegexOptions.IgnoreCase)) return true;

            // Titles that contain 'store' in a way that suggests a vendor name are product-like
            if (lower.Contains("store") || lower.Contains("official store") || lower.Contains("visit the")) return true;

            // If title contains many commas and descriptive clauses it's more likely a product listing
            if (t.Count(c => c == ',') >= 3) return true;

            return false;
        }

        /// <summary>
        /// Heuristic: detects if the artist field refers to a store/seller rather than an author.
        /// </summary>
        public static bool IsSellerArtist(string? artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return false;
            var a = artist.Trim().ToLowerInvariant();
            if (a.Contains("store") || a.Contains("seller") || a.Contains("official store") || a.Contains("shop")) return true;
            if (a.StartsWith("visit the ") && a.EndsWith(" store")) return true;
            return false;
        }

        /// <summary>
        /// Detects if a title contains "Kindle Edition" indicating it's an ebook, not an audiobook.
        /// </summary>
        public static bool IsKindleEdition(string? title)
        {
            return title?.Contains("Kindle Edition", StringComparison.OrdinalIgnoreCase) == true;
        }

        /// <summary>
        /// Conservative heuristic: determines whether a SearchResult looks like a genuine audiobook.
        /// </summary>
        public static bool IsLikelyAudiobook(SearchResult r)
        {
            if (r == null) return false;
            // Positive signals: runtime, narrator, or trusted audio metadata
            var hasRuntime = r.Runtime.HasValue && r.Runtime.Value > 5;
            var hasNarrator = !string.IsNullOrWhiteSpace(r.Narrator);
            var metadataSource = (r.MetadataSource ?? string.Empty).ToLowerInvariant();
            var sourceIsTrustedAudio = metadataSource.Contains("audible") || metadataSource.Contains("audnex") || metadataSource.Contains("audnexus") || metadataSource.Contains("audible");

            if (hasRuntime || hasNarrator || sourceIsTrustedAudio) return true;

            // Negative indicators in title/format that strongly suggest non-audio products
            var title = (r.Title ?? string.Empty);
            // Positive audio-format indicators (accept these even when metadata isn't from a trusted audio source)
            string[] audioFormatIndicators = new[] { "audio cd", "audio cassette", "radio production", "radio collection", "radio play", "bbc radio", "radio" };
            if (audioFormatIndicators.Any(p => title.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            string[] negativeIndicators = new[] {
                "paperback", "hardcover", "mass market paperback", "kindle edition", "ebook", "box set",
                "three volume", "three volume set", "3 books", "3 book", "3-book", "slipcase", "collector's edition"
            };

            if (negativeIndicators.Any(p => title.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return false;
            }

            // If none of the above apply, be conservative: don't assume it's an audiobook
            return false;
        }
    }
}
