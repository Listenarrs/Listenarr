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

namespace Listenarr.Application.Search
{
    public static class MarketDomainResolver
    {
        // Single source of truth: add new markets here once, not twice.
        private static readonly IReadOnlyDictionary<string, (string Audible, string Amazon)> _markets =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["au"] = ("www.audible.com.au", "www.amazon.com.au"),
                ["br"] = ("www.audible.com.br", "www.amazon.com.br"),
                ["ca"] = ("www.audible.ca", "www.amazon.ca"),
                ["de"] = ("www.audible.de", "www.amazon.de"),
                ["es"] = ("www.audible.es", "www.amazon.es"),
                ["fr"] = ("www.audible.fr", "www.amazon.fr"),
                ["in"] = ("www.audible.in", "www.amazon.in"),
                ["it"] = ("www.audible.it", "www.amazon.it"),
                ["jp"] = ("www.audible.co.jp", "www.amazon.co.jp"),
                ["uk"] = ("www.audible.co.uk", "www.amazon.co.uk"),
                ["gb"] = ("www.audible.co.uk", "www.amazon.co.uk"),
            };

        public static string BuildAmazonProductUrl(string asin, string? region)
        {
            return $"https://{GetAmazonDomain(region)}/dp/{Uri.EscapeDataString(asin)}";
        }

        public static string BuildAudibleProductUrl(string asin, string? region)
        {
            return $"https://{GetAudibleDomain(region)}/pd/{Uri.EscapeDataString(asin)}";
        }

        public static string GetAmazonDomain(string? region)
        {
            var key = region?.Trim();
            return key != null && _markets.TryGetValue(key, out var m) ? m.Amazon : "www.amazon.com";
        }

        public static string GetAudibleDomain(string? region)
        {
            var key = region?.Trim();
            return key != null && _markets.TryGetValue(key, out var m) ? m.Audible : "www.audible.com";
        }
    }
}
