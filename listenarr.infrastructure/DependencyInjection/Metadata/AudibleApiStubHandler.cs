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

namespace Listenarr.Infrastructure.DependencyInjection.Metadata;

internal sealed class AudibleApiStubHandler : HttpMessageHandler
{
    private const string StubAsin = "B008DFUGCQ";
    private const string FixtureResourceName = "Listenarr.Infrastructure.AudibleFixtures.B008DFUGCQ.json";
    private const string ReadyPlayerOneResponse = """
        {
            "product": {
                "asin": "B005FRGT44",
                "title": "Ready Player One",
                "language": "english",
                "authors": [{ "name": "Ernest Cline", "asin": "B004XRR8Z6" }],
                "sku": "sku1",
                "merchandising_summary": "desc1",
                "publisher_name": "Random House Audio",
                "runtime_length_min": 960
            }
        }
        """;
    private const string ReadyPlayerTwoResponse = """
        {
            "product": {
                "asin": "0593396960",
                "title": "Ready Player Two",
                "language": "english",
                "authors": [{ "name": "Ernest Cline", "asin": "B004XRR8Z6" }],
                "sku": "sku2",
                "merchandising_summary": "desc2",
                "publisher_name": "Random House Audio",
                "runtime_length_min": 900
            }
        }
        """;
    private const string StephenKingProductsResponse = """
        {
            "products": [
                {
                    "asin": "B0077DEH7A",
                    "title": "The Stand",
                    "language": "english",
                    "authors": [{ "name": "Stephen King" }],
                    "content_type": "Product",
                    "content_delivery_type": "MultiPartBook",
                    "sku": "sku-stand",
                    "merchandising_summary": "desc1",
                    "publisher_name": "Random House Audio",
                    "runtime_length_min": 2867
                },
                {
                    "asin": "B005UR3VFO",
                    "title": "11-22-63",
                    "language": "english",
                    "authors": [{ "name": "Stephen King" }],
                    "content_type": "Product",
                    "content_delivery_type": "MultiPartBook",
                    "sku": "sku-112263",
                    "merchandising_summary": "desc2",
                    "publisher_name": "Simon & Schuster Audio",
                    "runtime_length_min": 1840
                },
                {
                    "asin": "B00NOJ47PU",
                    "title": "Not Stephen King",
                    "language": "english",
                    "authors": [{ "name": "Somebody Else" }],
                    "content_type": "Product",
                    "content_delivery_type": "MultiPartBook",
                    "sku": "sku-other",
                    "merchandising_summary": "desc3",
                    "publisher_name": "Elsewhere",
                    "runtime_length_min": 600
                },
                {
                    "asin": "B019WPM4ZM",
                    "title": "It",
                    "language": "english",
                    "authors": [{ "name": "Stephen King" }],
                    "content_type": "Product",
                    "content_delivery_type": "MultiPartBook",
                    "sku": "sku-it",
                    "merchandising_summary": "desc4",
                    "publisher_name": "Simon & Schuster Audio",
                    "runtime_length_min": 2683
                }
            ],
            "total_results": 4
        }
        """;
    private const string SenLinYuProductsResponse = """
        {
            "products": [
                {
                    "asin": "B0DQR9D4YG",
                    "title": "Alchemised",
                    "authors": [{ "name": "SenLinYu", "asin": "B0DTNVW7SG" }],
                    "content_type": "Product",
                    "content_delivery_type": "MultiPartBook"
                }
            ],
            "total_results": 1
        }
        """;
    private const string ProjectHailMaryProductsResponse = """
        {
            "products": [
                {
                    "asin": "BBBBBBBBBB",
                    "title": "Project Hail Mary",
                    "authors": [{ "name": "Andy Weir", "asin": "AAAAAAAAAA" }],
                    "content_type": "Product",
                    "content_delivery_type": "MultiPartBook"
                }
            ],
            "total_results": 1
        }
        """;
    private const string EmptyProductsResponse = """
        { "products": [], "total_results": 0 }
        """;
    private const string EmptyAuthorScreenResponse = """
        {
            "sections": [
                {
                    "model": {
                        "rows": []
                    }
                }
            ]
        }
        """;
    private const string AndyWeirAuthorPageResponse = """
        <html><body>
        <adbl-full-width-product-tile>
            <adbl-full-bleed-image data-asin="B08G9PRS1K"
                                    data-url="/pd/Project-Hail-Mary-Audiobook/B08G9PRS1K"
                                    portrait-src="https://m.media-amazon.com/images/I/B1jkwD8awiL.png">
            </adbl-full-bleed-image>
            <h2 slot="title">Project Hail Mary</h2>
            <adbl-product-metadata slot="metadata">
            <script type="application/json">
                {"authors":[{"name":"Andy Weir"}]}
            </script>
            </adbl-product-metadata>
            <adbl-button href="/pd/Project-Hail-Mary-Audiobook/B08G9PRS1K">View Details</adbl-button>
        </adbl-full-width-product-tile>
        </body></html>
        """;
    private const string ErnestClineAuthorPageResponse = """
        <html><body><ul class="bc-list bc-list-nostyle">
        <li class="bc-list-item productListItem" id="product-list-item-B005FRGT44" aria-label="Ready Player One">
            <a href="/pd/Ready-Player-One-Audiobook/B005FRGT44">
            <div class="adbl-asin-impression" data-asin="B005FRGT44">
                <img src="https://m.media-amazon.com/images/I/41Eptolyo+L._SL500_.jpg" />
            </div>
            </a>
            <h2>Ready Player One</h2>
        </li>
        <li class="bc-list-item productListItem" id="product-list-item-0593396960" aria-label="Ready Player Two">
            <a href="/pd/Ready-Player-Two-Audiobook/0593396960">
            <div class="adbl-asin-impression" data-asin="0593396960">
                <img src="https://m.media-amazon.com/images/I/51XI-UQzsAL._SL500_.jpg" />
            </div>
            </a>
            <h2>Ready Player Two</h2>
        </li>
        </ul></body></html>
        """;
    private const string SenLinYuAuthorPageResponse = """
        <html>
          <body>
            <adbl-full-width-product-tile>
              <adbl-product-image data-asin="B0DQR9D4YG" data-url="/pd/Alchemised-Audiobook/B0DQR9D4YG" slot="image">
                <img src="https://m.media-amazon.com/images/I/51IrMtF6fzL._SL500_.jpg" alt="" />
              </adbl-product-image>
              <h2 slot="title">Alchemised</h2>
              <adbl-product-metadata slot="metadata">
                <script type="application/json">
                  {"authors":[{"name":"SenLinYu"}]}
                </script>
              </adbl-product-metadata>
            </adbl-full-width-product-tile>
          </body>
        </html>
        """;
    private static readonly string StubResponse = LoadStubResponse();

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
            responseBody = Uri.UnescapeDataString(pathSegments[3]).ToUpperInvariant() switch
            {
                StubAsin => StubResponse,
                "B005FRGT44" => ReadyPlayerOneResponse,
                "0593396960" => ReadyPlayerTwoResponse,
                _ => string.Empty
            };
            return responseBody.Length > 0;
        }

        if (pathSegments.Length == 4 &&
            pathSegments[0].Equals("1.0", StringComparison.OrdinalIgnoreCase) &&
            pathSegments[1].Equals("screens", StringComparison.OrdinalIgnoreCase) &&
            pathSegments[2].Equals("audible-android-author-detail", StringComparison.OrdinalIgnoreCase) &&
            new[] { "B00G0WYW92", "B004XRR8Z6", "B0DTNVW7SG" }.Contains(pathSegments[3], StringComparer.OrdinalIgnoreCase))
        {
            responseBody = EmptyAuthorScreenResponse;
            return true;
        }

        if (requestUri.AbsolutePath.Equals("/1.0/catalog/products/", StringComparison.OrdinalIgnoreCase))
        {
            var query = Uri.UnescapeDataString(requestUri.Query.Replace("+", "%20", StringComparison.Ordinal));
            if (query.Contains("keywords=Project Hail Mary", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = ProjectHailMaryProductsResponse;
                return true;
            }

            if (query.Contains("title=Project Hail Mary", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = EmptyProductsResponse;
                return true;
            }

            if (query.Contains("author=Stephen", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = StephenKingProductsResponse;
                return true;
            }

            if (query.Contains("author=SenLinYu", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("products_sort_by=Relevance", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = SenLinYuProductsResponse;
                return true;
            }

            if (query.Contains("author=SenLinYu", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("products_sort_by=BestSellers", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = EmptyProductsResponse;
                return true;
            }
        }

        if (requestUri.AbsolutePath.Equals("/author/Andy-Weir/B00G0WYW92", StringComparison.OrdinalIgnoreCase))
        {
            responseBody = AndyWeirAuthorPageResponse;
            mediaType = "text/html";
            return true;
        }

        if (requestUri.AbsolutePath.Equals("/author/Ernest-Cline/B004XRR8Z6", StringComparison.OrdinalIgnoreCase))
        {
            responseBody = ErnestClineAuthorPageResponse;
            mediaType = "text/html";
            return true;
        }

        if (requestUri.AbsolutePath.Equals("/author/SenLinYu/B0DTNVW7SG", StringComparison.OrdinalIgnoreCase))
        {
            responseBody = SenLinYuAuthorPageResponse;
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

    private static string LoadStubResponse()
    {
        using var stream = typeof(AudibleApiStubHandler).Assembly.GetManifestResourceStream(FixtureResourceName)
            ?? throw new InvalidOperationException($"Missing embedded Audible fixture resource {FixtureResourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}