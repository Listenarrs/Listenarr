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
using Listenarr.Infrastructure.Adapters;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Adapters
{
    [Trait("Area", "UsenetAdapterFiltering")]
    public class UsenetAdapterFilteringTests
    {
        private sealed class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _httpClient;

            public TestHttpClientFactory(HttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            public HttpClient CreateClient(string name)
            {
                return _httpClient;
            }
        }

        [Fact]
        [Trait("Scenario", "SabnzbdQueueAndItemsRespectConfiguredCategory")]
        public async Task Sabnzbd_GetQueueAndItems_FilterByConfiguredCategory()
        {
            const string queueBody = """
            {
              "queue": {
                "speed": "5 M",
                "slots": [
                  {
                    "nzo_id": "SABnzbd_nzo_1",
                    "filename": "Book One",
                    "status": "Downloading",
                    "mb": "100",
                    "mbleft": "50",
                    "percentage": "50",
                    "timeleft": "0:30:00",
                    "cat": "audiobooks"
                  },
                  {
                    "nzo_id": "SABnzbd_nzo_2",
                    "filename": "Movie One",
                    "status": "Downloading",
                    "mb": "100",
                    "mbleft": "70",
                    "percentage": "30",
                    "timeleft": "0:45:00",
                    "cat": "movies"
                  }
                ]
              }
            }
            """;
            const string historyBody = """
            {
              "history": {
                "slots": [
                  { "nzo_id": "SABnzbd_nzo_1", "name": "Book One" },
                  { "nzo_id": "SABnzbd_nzo_2", "name": "Movie One" }
                ]
              }
            }
            """;
            using var queueResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(queueBody, Encoding.UTF8, "application/json")
            };
            using var historyResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(historyBody, Encoding.UTF8, "application/json")
            };
            using var notFoundResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                var query = req.RequestUri?.Query ?? string.Empty;

                if (query.Contains("mode=queue", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(queueResponse);
                }

                if (query.Contains("mode=history", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(historyResponse);
                }

                return Task.FromResult(notFoundResponse);
            });

            using var httpClient = new HttpClient(handler);
            var adapter = new SabnzbdAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<SabnzbdAdapter>.Instance,
                Mock.Of<IAppMetricsService>());

            var client = new DownloadClientConfiguration
            {
                Id = "sab-client",
                Type = "sabnzbd",
                Host = "localhost",
                Port = 8080,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "apikey",
                    ["category"] = "audiobooks"
                }
            };

            var queue = await adapter.GetQueueAsync(client, CancellationToken.None);
            var items = await adapter.GetItemsAsync(client, CancellationToken.None);

            Assert.Single(queue);
            Assert.Equal("Book One", queue[0].Title);

            Assert.Single(items);
            Assert.Equal("Book One", items[0].Title);
        }

        [Fact]
        [Trait("Scenario", "NzbgetQueueAndItemsRespectConfiguredCategory")]
        public async Task Nzbget_GetQueueAndItems_FilterByConfiguredCategory()
        {
            var xml = BuildNzbGetListGroupsResponse(
                ("101", "Book One", "audiobooks", "DOWNLOADING"),
                ("202", "Movie One", "movies", "DOWNLOADING"));
            using var queueResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "text/xml")
            };
            using var itemsResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "text/xml")
            };
            using var notFoundResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
            var responses = new Queue<HttpResponseMessage>(new[] { queueResponse, itemsResponse });
            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                return Task.FromResult(responses.Count > 0 ? responses.Dequeue() : notFoundResponse);
            });

            using var httpClient = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Id = "nzb-client",
                Type = "nzbget",
                Host = "localhost",
                Port = 6789,
                Settings = new Dictionary<string, object>
                {
                    ["category"] = "audiobooks"
                }
            };

            var queue = await adapter.GetQueueAsync(client, CancellationToken.None);
            var items = await adapter.GetItemsAsync(client, CancellationToken.None);

            Assert.Single(queue);
            Assert.Equal("Book One", queue[0].Title);

            Assert.Single(items);
            Assert.Equal("Book One", items[0].Title);
        }

        private static string BuildNzbGetListGroupsResponse(params (string Id, string Name, string Category, string Status)[] groups)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?><methodResponse><params><param><value><array><data>");

            foreach (var group in groups)
            {
                sb.Append("<value><struct>");
                sb.Append($"<member><name>GroupID</name><value><string>{WebUtility.HtmlEncode(group.Id)}</string></value></member>");
                sb.Append($"<member><name>NZBName</name><value><string>{WebUtility.HtmlEncode(group.Name)}</string></value></member>");
                sb.Append($"<member><name>Category</name><value><string>{WebUtility.HtmlEncode(group.Category)}</string></value></member>");
                sb.Append($"<member><name>Status</name><value><string>{WebUtility.HtmlEncode(group.Status)}</string></value></member>");
                sb.Append("<member><name>FileSizeMB</name><value><string>100</string></value></member>");
                sb.Append("<member><name>RemainingSizeMB</name><value><string>50</string></value></member>");
                sb.Append("<member><name>DownloadRate</name><value><string>1024</string></value></member>");
                sb.Append("<member><name>DestDir</name><value><string>/downloads</string></value></member>");
                sb.Append("</struct></value>");
            }

            sb.Append("</data></array></value></param></params></methodResponse>");
            return sb.ToString();
        }
    }
}
