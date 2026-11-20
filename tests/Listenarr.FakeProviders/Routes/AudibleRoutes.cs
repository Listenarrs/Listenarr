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

namespace Listenarr.FakeProviders.Routes;

/// <summary>
/// Audible: the catalogue API (api.audible.{tld}) and the author pages of the store (www.audible.{tld}).
/// Fixtures in <c>Fixtures/Audible/</c>:
/// <list type="bullet">
/// <item><c>{ASIN}.json</c>: a product record, every region; <c>{ASIN}.{host}.json</c> overrides it for one host.</item>
/// <item><c>search-{title}[--{author}].json</c>: a title search; <c>search-author-{author}.json</c>: an author's
/// catalogue, whatever its sort; <c>search-keywords-{keywords}.json</c>: a keyword search (each slugged).</item>
/// <item><c>author-screen-{ASIN}.json</c>: the author detail screen of the mobile API.</item>
/// <item><c>author-page-{name}.html</c>: an author page of the store (<c>/author/{Name}/{ASIN}</c>).</item>
/// </list>
/// </summary>
internal sealed class AudibleRoutes : ProviderRoutes
{
    // Hand-written answers some tests rely on, tried after the fixtures above: a substring of the
    // decoded query, and the fixture answering it.
    private static readonly (string[] QueryContains, string Fixture)[] SearchScenarios =
    [
        (["keywords=Project Hail Mary"], "project-hail-mary-products.json"),
        (["title=Project Hail Mary"], "empty-products.json"),
        (["author=Stephen"], "stephen-king-products.json"),
        (["author=SenLinYu", "products_sort_by=Relevance"], "senlinyu-products.json"),
        (["author=SenLinYu", "products_sort_by=BestSellers"], "empty-products.json"),
    ];

    public override string Name => "Audible";

    public override bool Handles(Uri requestUri) =>
        requestUri.Host.Contains("audible.", StringComparison.OrdinalIgnoreCase);

    public override FixtureRoute? Resolve(Uri requestUri)
    {
        var segments = PathSegments(requestUri);

        // /1.0/catalog/products/{ASIN}
        if (segments.Length == 4 && segments[0] == "1.0" && IsEqual(segments[1], "catalog") &&
            IsEqual(segments[2], "products"))
        {
            var asin = segments[3].ToUpperInvariant();
            return new FixtureRoute([$"{asin}.{requestUri.Host}.json", $"{asin}.json"], $"{asin}.json");
        }

        // /1.0/catalog/products/?title=...&author=...
        if (requestUri.AbsolutePath.Equals("/1.0/catalog/products/", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSearch(requestUri);
        }

        // /1.0/screens/audible-android-author-detail/{ASIN}
        if (segments.Length == 4 && segments[0] == "1.0" && IsEqual(segments[1], "screens") &&
            IsEqual(segments[2], "audible-android-author-detail"))
        {
            return new FixtureRoute($"author-screen-{segments[3].ToUpperInvariant()}.json");
        }

        // /author/{Name}/{ASIN}
        if (segments.Length == 3 && IsEqual(segments[0], "author"))
        {
            return new FixtureRoute($"author-page-{Slug(segments[1])}.html");
        }

        return null;
    }

    private static FixtureRoute? ResolveSearch(Uri requestUri)
    {
        var parameters = ParseQuery(requestUri.Query);
        parameters.TryGetValue("author", out var author);
        var candidates = new List<string>();

        if (parameters.TryGetValue("title", out var title))
        {
            if (author != null)
            {
                candidates.Add($"search-{Slug(title)}--{Slug(author)}.json");
            }

            candidates.Add($"search-{Slug(title)}.json");
        }
        else if (parameters.TryGetValue("keywords", out var keywords))
        {
            candidates.Add($"search-keywords-{Slug(keywords)}.json");
        }
        else if (author != null)
        {
            candidates.Add($"search-author-{Slug(author)}.json");
        }

        var recordAs = candidates.FirstOrDefault();
        var query = Uri.UnescapeDataString(requestUri.Query.Replace("+", "%20", StringComparison.Ordinal));
        candidates.AddRange(SearchScenarios
            .Where(scenario => scenario.QueryContains.All(part => query.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .Select(scenario => scenario.Fixture)
            .Take(1));

        return recordAs == null && candidates.Count == 0
            ? null
            : new FixtureRoute(candidates, recordAs ?? candidates[0]);
    }

    private static bool IsEqual(string segment, string expected) =>
        segment.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
