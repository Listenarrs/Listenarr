using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    [Trait("Area", "AdapterFiltering")]
    public class DownloadClientCategoryFilterTests
    {
        [Fact]
        public void GetConfiguredCategory_TrimmedValue_ReturnsCategory()
        {
            var client = new DownloadClientConfiguration
            {
                Settings = new Dictionary<string, object>
                {
                    ["category"] = "  audiobooks  "
                }
            };

            var category = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            Assert.Equal("audiobooks", category);
        }

        [Fact]
        public void Matches_NoConfiguredCategory_AllowsAny()
        {
            Assert.True(DownloadClientCategoryFilter.Matches(null, "anything"));
            Assert.True(DownloadClientCategoryFilter.Matches(string.Empty, "anything"));
        }

        [Fact]
        public void Matches_ConfiguredCategory_IsCaseInsensitive()
        {
            Assert.True(DownloadClientCategoryFilter.Matches("AudioBooks", "audiobooks"));
            Assert.False(DownloadClientCategoryFilter.Matches("audiobooks", "movies"));
        }

        [Fact]
        public void MatchesAny_ConfiguredCategory_FindsMatch()
        {
            var labels = new[] { "movies", "audiobooks", "tv" };

            var matches = DownloadClientCategoryFilter.MatchesAny("AudioBooks", labels);

            Assert.True(matches);
        }

        [Fact]
        [Trait("Scenario", "TransmissionQueueCategoryFilter")]
        public async Task Transmission_GetQueue_FiltersByConfiguredCategory()
        {
            const string body = """
            {
              "result":"success",
              "arguments":{
                "torrents":[
                  {
                    "id":1,
                    "hashString":"HASH1",
                    "name":"Book One",
                    "percentDone":0.5,
                    "status":4,
                    "totalSize":1000,
                    "leftUntilDone":500,
                    "rateDownload":25,
                    "eta":60,
                    "downloadDir":"/downloads",
                    "addedDate":1700000000,
                    "uploadRatio":0.1,
                    "labels":["audiobooks"]
                  },
                  {
                    "id":2,
                    "hashString":"HASH2",
                    "name":"Movie One",
                    "percentDone":0.6,
                    "status":4,
                    "totalSize":1000,
                    "leftUntilDone":400,
                    "rateDownload":20,
                    "eta":50,
                    "downloadDir":"/downloads",
                    "addedDate":1700000000,
                    "uploadRatio":0.1,
                    "labels":["movies"]
                  }
                ]
              }
            }
            """;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            var handler = new DelegatingHandlerMock((_, _) =>
            {
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var httpFactory = new Mock<IHttpClientFactory>();
            httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var pathMapping = new Mock<IRemotePathMappingService>();
            pathMapping
                .Setup(p => p.TranslatePathAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string _, string path) => path);

            var adapter = new TransmissionAdapter(httpFactory.Object, pathMapping.Object, Mock.Of<ITorrentFileDownloader>(), NullLogger<TransmissionAdapter>.Instance);
            var client = new DownloadClientConfiguration
            {
                Id = "tr-1",
                Name = "Transmission",
                Type = "transmission",
                Host = "localhost",
                Port = 9091,
                Settings = new Dictionary<string, object>
                {
                    ["category"] = "audiobooks"
                }
            };

            var queue = await adapter.GetQueueAsync(client, CancellationToken.None);

            Assert.Single(queue);
            Assert.Equal("Book One", queue[0].Title);
            Assert.Equal("audiobooks", queue[0].Quality);
        }

        [Fact]
        [Trait("Scenario", "TransmissionItemCategoryFilter")]
        public async Task Transmission_GetItems_FiltersByConfiguredCategory()
        {
            const string body = """
            {
              "result":"success",
              "arguments":{
                "torrents":[
                  {
                    "id":1,
                    "hashString":"HASH1",
                    "name":"Book One",
                    "percentDone":0.5,
                    "status":4,
                    "totalSize":1000,
                    "leftUntilDone":500,
                    "rateDownload":25,
                    "eta":60,
                    "downloadDir":"/downloads",
                    "addedDate":1700000000,
                    "uploadRatio":0.1,
                    "labels":["audiobooks"]
                  },
                  {
                    "id":2,
                    "hashString":"HASH2",
                    "name":"Movie One",
                    "percentDone":0.6,
                    "status":4,
                    "totalSize":1000,
                    "leftUntilDone":400,
                    "rateDownload":20,
                    "eta":50,
                    "downloadDir":"/downloads",
                    "addedDate":1700000000,
                    "uploadRatio":0.1,
                    "labels":["movies"]
                  }
                ]
              }
            }
            """;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            var handler = new DelegatingHandlerMock((_, _) =>
            {
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var httpFactory = new Mock<IHttpClientFactory>();
            httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var pathMapping = new Mock<IRemotePathMappingService>();
            pathMapping
                .Setup(p => p.TranslatePathAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string _, string path) => path);

            var adapter = new TransmissionAdapter(httpFactory.Object, pathMapping.Object, Mock.Of<ITorrentFileDownloader>(), NullLogger<TransmissionAdapter>.Instance);
            var client = new DownloadClientConfiguration
            {
                Id = "tr-1",
                Name = "Transmission",
                Type = "transmission",
                Host = "localhost",
                Port = 9091,
                Settings = new Dictionary<string, object>
                {
                    ["category"] = "audiobooks"
                }
            };

            var items = await adapter.GetItemsAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal("Book One", items[0].Title);
        }

        [Fact]
        [Trait("Scenario", "QbittorrentCategoryParameterConsistency")]
        public void QBittorrent_CategoryParameter_IsAvailableForQueueAndItemSurfaces()
        {
            var settings = new Dictionary<string, object>
            {
                ["category"] = "audiobooks"
            };

            var queueParam = QBittorrentHelpers.BuildCategoryParameter(settings, "&");
            var itemParam = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            Assert.Equal("&category=audiobooks", queueParam);
            Assert.Equal("&category=audiobooks", itemParam);
        }
    }
}
