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
/// OpenLibrary: the search API (openlibrary.org) and the covers (covers.openlibrary.org). Fixtures in
/// <c>Fixtures/OpenLibrary/</c>:
/// <list type="bullet">
/// <item><c>search-{q}.json</c>: <c>/search.json?q=</c>, or <c>search-{title}[--{author}].json</c> when the
/// query has no <c>q</c> (slugged; <c>limit</c> and <c>fields</c> ignored).</item>
/// <item><c>cover-{isbn|olid|id}-{value}-{size}.jpg</c>: <c>/b/{key}/{value}-{size}.jpg</c>.</item>
/// </list>
/// </summary>
internal sealed class OpenLibraryRoutes : ProviderRoutes
{
    public override string Name => "OpenLibrary";

    public override bool Handles(Uri requestUri) =>
        HostIs(requestUri, "openlibrary.org") || HostIs(requestUri, "covers.openlibrary.org");

    public override FixtureRoute? Resolve(Uri requestUri)
    {
        var segments = PathSegments(requestUri);

        if (HostIs(requestUri, "covers.openlibrary.org"))
        {
            // /b/{isbn|olid|id}/{value}-{size}.jpg
            return segments.Length == 3 && segments[0].Equals("b", StringComparison.OrdinalIgnoreCase)
                ? new FixtureRoute($"cover-{segments[1].ToLowerInvariant()}-{Slug(Path.GetFileNameWithoutExtension(segments[2]))}{Path.GetExtension(segments[2]).ToLowerInvariant()}")
                : null;
        }

        if (segments is ["search.json"])
        {
            var parameters = ParseQuery(requestUri.Query);
            if (parameters.TryGetValue("q", out var q))
            {
                return new FixtureRoute($"search-{Slug(q)}.json");
            }

            if (parameters.TryGetValue("title", out var title))
            {
                return parameters.TryGetValue("author", out var author)
                    ? new FixtureRoute($"search-{Slug(title)}--{Slug(author)}.json")
                    : new FixtureRoute($"search-{Slug(title)}.json");
            }
        }

        return null;
    }
}
