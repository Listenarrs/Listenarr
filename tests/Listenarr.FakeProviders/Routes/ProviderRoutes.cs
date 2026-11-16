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

namespace Listenarr.FakeProviders.Routes;

/// <summary>
/// The fixture a request maps to: the files tried in order (in the provider's fixture folder), and the
/// one the recorder writes the real response to when none exists.
/// </summary>
internal sealed record FixtureRoute(IReadOnlyList<string> Candidates, string RecordAs)
{
    public FixtureRoute(string fixtureName)
        : this([fixtureName], fixtureName)
    {
    }
}

/// <summary>
/// One metadata provider's routes: which hosts it serves, and which fixture file of
/// <c>Fixtures/{Name}/</c> answers each request. A fixture name is derived from the request
/// alone, so a missing one can be recorded from the real API (scripts/test-fixtures/record-provider-fixtures.mjs).
/// </summary>
internal abstract class ProviderRoutes
{
    /// <summary>The provider, also the name of its fixture folder.</summary>
    public abstract string Name { get; }

    public abstract bool Handles(Uri requestUri);

    /// <summary>The fixture a GET request maps to, or null when the provider has no such route.</summary>
    public abstract FixtureRoute? Resolve(Uri requestUri);

    protected static string[] PathSegments(Uri requestUri) =>
        requestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

    protected static bool HostIs(Uri requestUri, string host) =>
        requestUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

    protected static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            parameters[Uri.UnescapeDataString(pair[..separator].Replace('+', ' '))] =
                Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return parameters;
    }

    /// <summary>Lower case, letters and digits, one dash between words: "The Gods of Mars" -> "the-gods-of-mars".</summary>
    protected static string Slug(string value)
    {
        var slug = new StringBuilder();
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }
}
