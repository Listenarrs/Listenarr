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
using System.Text.Json;
using Listenarr.Infrastructure.DownloadClients.Deluge;
using Listenarr.Infrastructure.Torrents;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Deluge
{
    [Trait("Name", "DelugeAdapterTests")]
    [Trait("Category", "DownloadClientAdapter")]
    [Trait("Third-Party", "Deluge")]
    public class DelugeAdapterTests
    {
        [Theory]
        [InlineData("Seeding", 100.0, DownloadItemStatus.Completed)]
        [InlineData("Paused", 100.0, DownloadItemStatus.Completed)]
        [InlineData("Queued", 100.0, DownloadItemStatus.Completed)]
        [InlineData("Downloading", 42.5, DownloadItemStatus.Downloading)]
        [InlineData("Downloading Metadata", 0.0, DownloadItemStatus.Downloading)]
        [InlineData("Checking", 100.0, DownloadItemStatus.Checking)]
        [InlineData("Error", 0.0, DownloadItemStatus.Failed)]
        public async Task GetItemsAsync_MapsDelugeStatesToNormalizedStatuses(string state, double progress, DownloadItemStatus expectedStatus)
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse(state, progress, "listenarr"));
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetItemsAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal(expectedStatus, items[0].Status);
            Assert.Equal("ABCDEF1234567890", items[0].DownloadId);
            Assert.Equal("Book.m4b", items[0].Title);
            Assert.Equal("/downloads/Book.m4b", items[0].OutputPath);
        }

        [Fact]
        public async Task GetItemsAsync_FiltersByConfiguredCategory()
        {
            var adapter = CreateAdapter("""
            {
              "id":1,
              "result":{
                "torrents":{
                  "HASH1":{
                    "name":"Book One",
                    "total_size":100,
                    "total_done":100,
                    "progress":100.0,
                    "download_payload_rate":0,
                    "eta":0,
                    "state":"Seeding",
                    "save_path":"/downloads",
                    "label":"listenarr",
                    "ratio":1.0,
                    "num_seeds":1,
                    "num_peers":0,
                    "time_added":1700000000,
                    "message":""
                  },
                  "HASH2":{
                    "name":"Movie One",
                    "total_size":100,
                    "total_done":100,
                    "progress":100.0,
                    "download_payload_rate":0,
                    "eta":0,
                    "state":"Seeding",
                    "save_path":"/downloads",
                    "label":"movies",
                    "ratio":1.0,
                    "num_seeds":1,
                    "num_peers":0,
                    "time_added":1700000000,
                    "message":""
                  }
                }
              },
              "error":null
            }
            """);
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetItemsAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal("HASH1", items[0].DownloadId);
            Assert.Equal("listenarr", items[0].Category);
        }

        [Fact]
        public async Task TestConnectionAsync_AuthenticatesAndRequiresDaemonConnection()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr"));
            var client = CreateClient(category: null);

            var (success, message) = await adapter.TestConnectionAsync(client, CancellationToken.None);

            Assert.True(success);
            Assert.Contains("daemon", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task FetchDownloadsAsync_MatchesDownloadsByExternalClientId()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr"));
            var client = CreateClient(category: "listenarr");
            var download = new Download
            {
                Id = "listenarr-download-1",
                DownloadClientId = client.Id,
                Status = DownloadStatus.Queued
            };
            download.SetExternalId("ABCDEF1234567890");

            var updated = await adapter.FetchDownloadsAsync(client, [download], CancellationToken.None);

            Assert.Single(updated);
            Assert.Equal(DownloadStatus.Completed, updated[0].Status);
            Assert.Equal(100m, updated[0].Progress);
            Assert.Equal(100, updated[0].DownloadedSize);
            Assert.Equal("/downloads/Book.m4b", updated[0].DownloadPath);
        }

        [Fact]
        public async Task GetImportItemAsync_MatchesQueueItemByExternalClientId()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr"));
            var client = CreateClient(category: "listenarr");
            var download = new Download
            {
                Id = "listenarr-download-1",
                DownloadClientId = client.Id
            };
            download.SetExternalId("ABCDEF1234567890");
            var fallback = new QueueItem
            {
                Id = "listenarr-download-1",
                Title = "Fallback"
            };

            var item = await adapter.GetImportItemAsync(client, download, fallback, null, CancellationToken.None);

            Assert.Equal("ABCDEF1234567890", item.Id);
            Assert.Equal("Book.m4b", item.Title);
            Assert.Equal("completed", item.Status);
        }

        [Fact]
        public async Task FetchDownloadsAsync_PreservesImportedStatusWhenClientStillReportsCompletedTorrent()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr"));
            var client = CreateClient(category: "listenarr");
            var download = new Download
            {
                Id = "listenarr-download-1",
                DownloadClientId = client.Id,
                Status = DownloadStatus.Moved
            };
            download.SetExternalId("ABCDEF1234567890");

            var updated = await adapter.FetchDownloadsAsync(client, [download], CancellationToken.None);

            Assert.Single(updated);
            Assert.Equal(DownloadStatus.Moved, updated[0].Status);
            Assert.Equal(100m, updated[0].Progress);
            Assert.Equal("/downloads/Book.m4b", updated[0].DownloadPath);
        }

        private static DelugeAdapter CreateAdapter(string updateUiResponse)
        {
            var handler = new DelegatingHandlerMock(async (request, ct) =>
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var document = JsonDocument.Parse(body);
                var method = document.RootElement.GetProperty("method").GetString();
                var responseBody = method switch
                {
                    "auth.login" => """
                    {
                      "id":1,
                      "result":true,
                      "error":null
                    }
                    """,
                    "web.connected" => """
                    {
                      "id":1,
                      "result":true,
                      "error":null
                    }
                    """,
                    "web.update_ui" => updateUiResponse,
                    _ => """
                    {
                      "id":1,
                      "result":null,
                      "error":null
                    }
                    """
                };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                };
            });
            var httpFactory = new Mock<IHttpClientFactory>();
            httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

            return new DelugeAdapter(httpFactory.Object, Mock.Of<ITorrentFileDownloader>(), NullLogger<DelugeAdapter>.Instance);
        }

        private static DownloadClientConfiguration CreateClient(string? category)
        {
            var client = new DownloadClientConfiguration
            {
                Id = "deluge-1",
                Name = "Deluge",
                Type = "deluge",
                Host = "localhost",
                Port = 8112,
                Password = "deluge"
            };

            if (!string.IsNullOrWhiteSpace(category))
            {
                client.Settings = new Dictionary<string, object>
                {
                    ["category"] = category
                };
            }

            return client;
        }

        private static string BuildUpdateUiResponse(string state, double progress, string label)
            => $$"""
            {
              "id":1,
              "result":{
                "torrents":{
                  "ABCDEF1234567890":{
                    "name":"Book.m4b",
                    "total_size":100,
                    "total_done":{{(long)Math.Round(progress)}},
                    "progress":{{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                    "download_payload_rate":25,
                    "eta":60,
                    "state":"{{state}}",
                    "save_path":"/downloads",
                    "label":"{{label}}",
                    "ratio":1.0,
                    "num_seeds":1,
                    "num_peers":0,
                    "time_added":1700000000,
                    "message":""
                  }
                }
              },
              "error":null
            }
            """;
    }
}
