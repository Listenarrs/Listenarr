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

using System.Net;
using System.Text;

namespace Listenarr.TestHost.Metadata;

internal sealed class ExternalApiStubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Method == HttpMethod.Get && TryGetStubResponse(request.RequestUri, out var responseBody, out var mediaType))
        {
            return Task.FromResult(CreateResponse(responseBody, mediaType));
        }

        // Any other request (another host, an Audible request without a fixture) is answered 404 and
        // written to the container log, so a suite's author sees which fixture a request needs.
        Console.Error.WriteLine($"External API stub: no fixture for {request.Method} {request.RequestUri}");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No test fixture matches this request.", Encoding.UTF8, "text/plain")
        });
    }

    private static bool TryGetStubResponse(Uri? requestUri, out string responseBody, out string mediaType)
    {
        responseBody = string.Empty;
        mediaType = "application/json";
        if (requestUri == null)
        {
            return false;
        }

        var pathSegments = requestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length == 4 &&
            pathSegments[0].Equals("1.0", StringComparison.OrdinalIgnoreCase) &&
            pathSegments[1].Equals("catalog", StringComparison.OrdinalIgnoreCase) &&
            pathSegments[2].Equals("products", StringComparison.OrdinalIgnoreCase))
        {
            // A product record: the region's own fixture ({ASIN}.{host}.json, for a record that region
            // lacks or tells differently), else the one every region shares ({ASIN}.json).
            var asin = Uri.UnescapeDataString(pathSegments[3]).ToUpperInvariant();
            return TryLoadFixture(out responseBody, $"{asin}.{requestUri.Host}.json", $"{asin}.json");
        }

        if (pathSegments.Length == 4 &&
            pathSegments[0].Equals("1.0", StringComparison.OrdinalIgnoreCase) &&
            pathSegments[1].Equals("screens", StringComparison.OrdinalIgnoreCase) &&
            pathSegments[2].Equals("audible-android-author-detail", StringComparison.OrdinalIgnoreCase) &&
            new[] { "B00G0WYW92", "B004XRR8Z6", "B0DTNVW7SG" }.Contains(pathSegments[3], StringComparer.OrdinalIgnoreCase))
        {
            responseBody = LoadFixture("empty-author-screen.json");
            return true;
        }

        if (requestUri.AbsolutePath.Equals("/1.0/catalog/products/", StringComparison.OrdinalIgnoreCase))
        {
            var query = Uri.UnescapeDataString(requestUri.Query.Replace("+", "%20", StringComparison.Ordinal));
            // A title search, with or without an author: search-{title}[--{author}].json; an author's
            // catalogue (an author search, whatever its sort): search-author-{author}.json; each slugged.
            var parameters = ParseQuery(requestUri.Query);
            parameters.TryGetValue("author", out var author);
            if (parameters.TryGetValue("title", out var title))
            {
                var titleSlug = Slug(title);
                var fixtureNames = author != null
                    ? new[] { $"search-{titleSlug}--{Slug(author)}.json", $"search-{titleSlug}.json" }
                    : new[] { $"search-{titleSlug}.json" };
                if (TryLoadFixture(out responseBody, fixtureNames))
                {
                    return true;
                }
            }
            else if (author != null && !parameters.ContainsKey("keywords") &&
                TryLoadFixture(out responseBody, $"search-author-{Slug(author)}.json"))
            {
                return true;
            }

            if (query.Contains("keywords=Project Hail Mary", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = LoadFixture("project-hail-mary-products.json");
                return true;
            }

            if (query.Contains("title=Project Hail Mary", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = LoadFixture("empty-products.json");
                return true;
            }

            if (query.Contains("author=Stephen", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = LoadFixture("stephen-king-products.json");
                return true;
            }

            if (query.Contains("author=SenLinYu", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("products_sort_by=Relevance", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = LoadFixture("senlinyu-products.json");
                return true;
            }

            if (query.Contains("author=SenLinYu", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("products_sort_by=BestSellers", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = LoadFixture("empty-products.json");
                return true;
            }
        }

        if (requestUri.AbsolutePath.Equals("/author/Andy-Weir/B00G0WYW92", StringComparison.OrdinalIgnoreCase))
        {
            responseBody = LoadFixture("andy-weir-author.html");
            mediaType = "text/html";
            return true;
        }

        if (requestUri.AbsolutePath.Equals("/author/Ernest-Cline/B004XRR8Z6", StringComparison.OrdinalIgnoreCase))
        {
            responseBody = LoadFixture("ernest-cline-author.html");
            mediaType = "text/html";
            return true;
        }

        if (requestUri.AbsolutePath.Equals("/author/SenLinYu/B0DTNVW7SG", StringComparison.OrdinalIgnoreCase))
        {
            responseBody = LoadFixture("senlinyu-author.html");
            mediaType = "text/html";
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> ParseQuery(string query)
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

    // Lower case, letters and digits, one dash between words: "The Gods of Mars" -> "the-gods-of-mars".
    private static string Slug(string value)
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

    private static bool TryLoadFixture(out string responseBody, params string[] fixtureNames)
    {
        foreach (var fixtureName in fixtureNames)
        {
            if (typeof(ExternalApiStubHandler).Assembly.GetManifestResourceInfo(ResourceName(fixtureName)) != null)
            {
                responseBody = LoadFixture(fixtureName);
                return true;
            }
        }

        responseBody = string.Empty;
        return false;
    }

    private static string ResourceName(string fixtureName) => $"Listenarr.TestHost.AudibleFixtures.{fixtureName}";

    private static HttpResponseMessage CreateResponse(string body, string mediaType)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
    }

    private static string LoadFixture(string fixtureName)
    {
        var resourceName = ResourceName(fixtureName);
        using var stream = typeof(ExternalApiStubHandler).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded Audible fixture resource {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
