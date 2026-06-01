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
            return region?.Trim().ToLowerInvariant() switch
            {
                "au" => "www.amazon.com.au",
                "br" => "www.amazon.com.br",
                "ca" => "www.amazon.ca",
                "de" => "www.amazon.de",
                "es" => "www.amazon.es",
                "fr" => "www.amazon.fr",
                "in" => "www.amazon.in",
                "it" => "www.amazon.it",
                "jp" => "www.amazon.co.jp",
                "uk" or "gb" => "www.amazon.co.uk",
                _ => "www.amazon.com"
            };
        }

        public static string GetAudibleDomain(string? region)
        {
            return region?.Trim().ToLowerInvariant() switch
            {
                "au" => "www.audible.com.au",
                "br" => "www.audible.com.br",
                "ca" => "www.audible.ca",
                "de" => "www.audible.de",
                "es" => "www.audible.es",
                "fr" => "www.audible.fr",
                "in" => "www.audible.in",
                "it" => "www.audible.it",
                "jp" => "www.audible.co.jp",
                "uk" or "gb" => "www.audible.co.uk",
                _ => "www.audible.com"
            };
        }
    }
}
