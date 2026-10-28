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

namespace Listenarr.Application.Metadata.Audible;

public static class AudibleRequestHelper
{
    private static readonly IReadOnlyDictionary<string, string> AudibleApiDomainMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["us"] = "api.audible.com",
            ["ca"] = "api.audible.ca",
            ["uk"] = "api.audible.co.uk",
            ["au"] = "api.audible.com.au",
            ["fr"] = "api.audible.fr",
            ["de"] = "api.audible.de",
            ["jp"] = "api.audible.co.jp",
            ["it"] = "api.audible.it",
            ["in"] = "api.audible.in",
            ["es"] = "api.audible.es",
            ["br"] = "api.audible.com.br",
        };

    private static readonly IReadOnlyDictionary<string, string> AudibleLocaleMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["us"] = "en-US",
            ["ca"] = "en-CA",
            ["uk"] = "en-GB",
            ["au"] = "en-AU",
            ["fr"] = "fr-FR",
            ["de"] = "de-DE",
            ["jp"] = "ja-JP",
            ["it"] = "it-IT",
            ["in"] = "en-IN",
            ["es"] = "es-ES",
            ["br"] = "pt-BR",
        };

    public static string BuildApiBaseUrl(string region)
    {
        var normalizedRegion = NormalizeRegion(region);
        return $"https://{(AudibleApiDomainMap.TryGetValue(normalizedRegion, out var domain) ? domain : AudibleApiDomainMap["us"])}";
    }

    public static string GetLocale(string region)
    {
        var normalizedRegion = NormalizeRegion(region);
        return AudibleLocaleMap.TryGetValue(normalizedRegion, out var locale)
            ? locale
            : AudibleLocaleMap["us"];
    }

    public static string NormalizeRegion(string region)
    {
        return string.IsNullOrWhiteSpace(region) ? "us" : region.Trim().ToLowerInvariant();
    }

    public static string BuildQueryString(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        return string.Join(
            "&",
            parameters
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
    }

    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string GenerateRandomSessionId()
    {
        static string RandomDigits()
        {
            return Random.Shared.Next(0, 10_000_000).ToString().PadLeft(7, '0');
        }

        return $"000-{RandomDigits()}-{RandomDigits()}";
    }

    public static string BuildAuthorPageUrl(string author, string authorAsin, string region)
    {
        var authorSlug = string.IsNullOrWhiteSpace(author)
            ? authorAsin
            : Uri.EscapeDataString(author.Trim().Replace(' ', '-'));
        return $"{GetBaseUrl(region)}/author/{authorSlug}/{Uri.EscapeDataString(authorAsin)}";
    }

    public static string GetBaseUrl(string region)
    {
        return $"https://{MarketDomainResolver.GetAudibleDomain(region)}";
    }
}
