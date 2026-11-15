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

internal sealed class AudibleApiStubHandler : HttpMessageHandler
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

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No Audible API fixture matches this request.", Encoding.UTF8, "text/plain")
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
            string? fixtureName = Uri.UnescapeDataString(pathSegments[3]).ToUpperInvariant() switch
            {
                "B008DFUGCQ" => "B008DFUGCQ.json",
                "B005FRGT44" => "B005FRGT44.json",
                "0593396960" => "0593396960.json",
                _ => null
            };
            if (fixtureName == null)
            {
                return false;
            }

            responseBody = LoadFixture(fixtureName);
            return true;
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

    private static HttpResponseMessage CreateResponse(string body, string mediaType)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
    }

    private static string LoadFixture(string fixtureName)
    {
        var resourceName = $"Listenarr.TestHost.AudibleFixtures.{fixtureName}";
        using var stream = typeof(AudibleApiStubHandler).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded Audible fixture resource {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
