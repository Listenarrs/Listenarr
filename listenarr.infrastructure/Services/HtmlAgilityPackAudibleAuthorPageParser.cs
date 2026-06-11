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

using System.Text.Json;
using HtmlAgilityPack;
using Listenarr.Application.Interfaces;

namespace Listenarr.Infrastructure.Services
{
    public class HtmlAgilityPackAudibleAuthorPageParser : IAudibleAuthorPageParser
    {
        public List<AudibleSearchResult> ParseAuthorPage(string html, string author, string authorAsin, string region)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return new List<AudibleSearchResult>();
            }

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            var parsedTiles = new List<AudibleSearchResult>();
            var seenAsins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tiles = htmlDoc.DocumentNode.SelectNodes("//adbl-full-width-product-tile");
            var legacyProductListItems = htmlDoc.DocumentNode.SelectNodes("//li[contains(@class, 'productListItem')]");

            if (tiles != null)
            {
                foreach (var tile in tiles)
                {
                    AddParsedResult(parsedTiles, seenAsins, ParseAudibleAuthorTile(tile, author, authorAsin, region));
                }
            }

            if (legacyProductListItems != null)
            {
                foreach (var item in legacyProductListItems)
                {
                    AddParsedResult(parsedTiles, seenAsins, ParseAudibleAuthorListItem(item, author, authorAsin, region));
                }
            }

            return parsedTiles;
        }

        private static void AddParsedResult(List<AudibleSearchResult> results, HashSet<string> seenAsins, AudibleSearchResult? parsed)
        {
            if (parsed == null)
            {
                return;
            }

            var key = string.IsNullOrWhiteSpace(parsed.Asin)
                ? $"{parsed.Title}|{parsed.Link}"
                : parsed.Asin;
            if (seenAsins.Add(key))
            {
                results.Add(parsed);
            }
        }

        private static AudibleSearchResult? ParseAudibleAuthorTile(HtmlNode tile, string author, string authorAsin, string region)
        {
            var productImageNode = tile.SelectSingleNode(".//adbl-product-image")
                ?? tile.SelectSingleNode(".//adbl-full-bleed-image");
            var asin = productImageNode?.GetAttributeValue("data-asin", string.Empty);
            if (string.IsNullOrWhiteSpace(asin))
            {
                asin = tile.SelectSingleNode(".//*[@data-asin]")?.GetAttributeValue("data-asin", string.Empty);
            }
            if (string.IsNullOrWhiteSpace(asin)) return null;

            var title = HtmlEntity.DeEntitize(tile.SelectSingleNode(".//*[@slot='title']")?.InnerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title)) return null;

            var subtitle = HtmlEntity.DeEntitize(tile.SelectSingleNode(".//*[@slot='subtitle']")?.InnerText ?? string.Empty).Trim();
            var imageUrl = productImageNode?.SelectSingleNode(".//img")?.GetAttributeValue("src", string.Empty);
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                imageUrl = productImageNode?.GetAttributeValue("portrait-src", string.Empty);
            }
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                imageUrl = productImageNode?.GetAttributeValue("landscape-src", string.Empty);
            }

            var relativeUrl = productImageNode?.GetAttributeValue("data-url", string.Empty);
            if (string.IsNullOrWhiteSpace(relativeUrl))
            {
                relativeUrl = tile.SelectSingleNode(".//adbl-button[@href]")?.GetAttributeValue("href", string.Empty)
                    ?? tile.SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", string.Empty);
            }

            var authors = ParseAudibleAuthorTileAuthors(tile, author, authorAsin, region);
            if (authors.Count == 0 && !string.IsNullOrWhiteSpace(author))
            {
                authors.Add(new AudibleAuthor { Asin = authorAsin, Name = author, Region = region });
            }

            return new AudibleSearchResult
            {
                Asin = asin,
                Title = title,
                Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
                Authors = authors,
                ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl,
                Link = NormalizeAudibleUrl(relativeUrl, region)
            };
        }

        private static AudibleSearchResult? ParseAudibleAuthorListItem(HtmlNode listItem, string author, string authorAsin, string region)
        {
            var asin = listItem.SelectSingleNode(".//*[@data-asin]")?.GetAttributeValue("data-asin", string.Empty);
            if (string.IsNullOrWhiteSpace(asin))
            {
                return null;
            }

            var title = HtmlEntity.DeEntitize(listItem.GetAttributeValue("aria-label", string.Empty)).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = HtmlEntity.DeEntitize(
                    listItem.SelectSingleNode(".//h2")?.InnerText ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var imageUrl = listItem.SelectSingleNode(".//img[@src]")?.GetAttributeValue("src", string.Empty);
            var relativeUrl = listItem.SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", string.Empty);

            return new AudibleSearchResult
            {
                Asin = asin,
                Title = title,
                Authors = new List<AudibleAuthor>
                {
                    new()
                    {
                        Asin = authorAsin,
                        Name = author,
                        Region = region
                    }
                },
                ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl,
                Link = NormalizeAudibleUrl(relativeUrl, region)
            };
        }

        private static List<AudibleAuthor> ParseAudibleAuthorTileAuthors(HtmlNode tile, string author, string authorAsin, string region)
        {
            var authors = new List<AudibleAuthor>();
            var metadataJson = tile.SelectSingleNode(".//adbl-product-metadata/script[@type='application/json']")?.InnerText;
            if (string.IsNullOrWhiteSpace(metadataJson)) return authors;

            try
            {
                var metadata = JsonSerializer.Deserialize<AudibleAuthorTileMetadata>(metadataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata?.Authors == null) return authors;

                foreach (var metadataAuthor in metadata.Authors.Where(metadataAuthor => !string.IsNullOrWhiteSpace(metadataAuthor.Name)))
                {
                    authors.Add(new AudibleAuthor
                    {
                        Asin = string.Equals(metadataAuthor.Name, author, StringComparison.OrdinalIgnoreCase) ? authorAsin : null,
                        Name = metadataAuthor.Name,
                        Region = region
                    });
                }
            }
            catch (JsonException)
            {
            }

            return authors;
        }

        private static string? NormalizeAudibleUrl(string? url, string region)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri)
                && !string.Equals(absoluteUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                return absoluteUri.ToString();
            }
            return $"{GetAudibleBaseUrl(region)}{url}";
        }

        private static string GetAudibleBaseUrl(string region)
        {
            return region?.Trim().ToLowerInvariant() switch
            {
                "au" => "https://www.audible.com.au",
                "ca" => "https://www.audible.ca",
                "de" => "https://www.audible.de",
                "es" => "https://www.audible.es",
                "fr" => "https://www.audible.fr",
                "in" => "https://www.audible.in",
                "it" => "https://www.audible.it",
                "jp" => "https://www.audible.co.jp",
                "uk" => "https://www.audible.co.uk",
                _ => "https://www.audible.com"
            };
        }
    }
}
