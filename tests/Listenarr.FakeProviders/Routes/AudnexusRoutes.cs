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
/// Audnexus (api.audnex.us). Fixtures in <c>Fixtures/Audnexus/</c>, whatever the region asked:
/// <list type="bullet">
/// <item><c>book-{ASIN}.json</c>: <c>/books/{ASIN}</c>; <c>book-{ASIN}-chapters.json</c>: <c>/books/{ASIN}/chapters</c>.</item>
/// <item><c>author-{ASIN}.json</c>: <c>/authors/{ASIN}</c>; <c>search-author-{name}.json</c>: <c>/authors?name=</c> (slugged).</item>
/// </list>
/// </summary>
internal sealed class AudnexusRoutes : ProviderRoutes
{
    public override string Name => "Audnexus";

    public override bool Handles(Uri requestUri) =>
        HostIs(requestUri, "api.audnex.us") || HostIs(requestUri, "audnex.us");

    public override FixtureRoute? Resolve(Uri requestUri)
    {
        var segments = PathSegments(requestUri);
        if (segments.Length == 0)
        {
            return null;
        }

        var resource = segments[0].ToLowerInvariant();
        switch (resource, segments.Length)
        {
            case ("books", 2):
                return new FixtureRoute($"book-{segments[1].ToUpperInvariant()}.json");
            case ("books", 3) when segments[2].Equals("chapters", StringComparison.OrdinalIgnoreCase):
                return new FixtureRoute($"book-{segments[1].ToUpperInvariant()}-chapters.json");
            case ("authors", 2):
                return new FixtureRoute($"author-{segments[1].ToUpperInvariant()}.json");
            case ("authors", 1) when ParseQuery(requestUri.Query).TryGetValue("name", out var name):
                return new FixtureRoute($"search-author-{Slug(name)}.json");
            default:
                return null;
        }
    }
}
