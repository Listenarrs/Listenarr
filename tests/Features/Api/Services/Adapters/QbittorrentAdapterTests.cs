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
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Listenarr.Domain.Utils;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services.Adapters
{
    public class QbittorrentAdapterTests
    {
        private class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public TestHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_Then_LoginSucceeds_ReturnsSuccess()
        {
            var loggedIn = false;
            using var forbiddenVersionResponse = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Forbidden")
            };
            using var successVersionResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("v5.0.2")
            };
            using var loginResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Ok")
            };
            loginResponse.Headers.Add("Set-Cookie", "SID=1; HttpOnly; Path=/");
            using var notFoundResponse = new HttpResponseMessage(HttpStatusCode.NotFound);

            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.StartsWith("/api/v2/app/version", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(loggedIn ? successVersionResponse : forbiddenVersionResponse);
                }

                if (req.Method == HttpMethod.Post && req.RequestUri.PathAndQuery.StartsWith("/api/v2/auth/login", StringComparison.OrdinalIgnoreCase))
                {
                    loggedIn = true;
                    return Task.FromResult(loginResponse);
                }

                return Task.FromResult(notFoundResponse);
            });

            using var http = new HttpClient(handler);
            var factory = new TestHttpClientFactory(http);
            var pathMapMock = new Mock<Listenarr.Api.Services.IRemotePathMappingService>();
            var adapter = new QbittorrentAdapter(factory, pathMapMock.Object, Mock.Of<ITorrentFileDownloader>(), NullLogger<QbittorrentAdapter>.Instance);

            var cfg = new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 443,
                UseSSL = true,
                Username = "admin",
                Password = "admin"
            };

            var (success, message) = await adapter.TestConnectionAsync(cfg);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_And_NoCredentials_ReturnsForbidden()
        {
            using var forbiddenVersionResponse = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Forbidden")
            };
            using var notFoundResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.StartsWith("/api/v2/app/version", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(forbiddenVersionResponse);
                }

                return Task.FromResult(notFoundResponse);
            });

            using var http = new HttpClient(handler);
            var factory = new TestHttpClientFactory(http);
            var pathMapMock = new Mock<Listenarr.Api.Services.IRemotePathMappingService>();
            var adapter = new QbittorrentAdapter(factory, pathMapMock.Object, Mock.Of<ITorrentFileDownloader>(), NullLogger<QbittorrentAdapter>.Instance);

            var cfg = new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 443,
                UseSSL = true,
                Username = null,
                Password = null
            };

            var (success, message) = await adapter.TestConnectionAsync(cfg);

            Assert.False(success);
            Assert.Contains("Forbidden", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_NormalizesHostWithSchemeAndPath()
        {
            Uri? capturedUri = null;
            using var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("v5.0.2")
            };
            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(okResponse);
            });

            using var http = new HttpClient(handler);
            var factory = new TestHttpClientFactory(http);
            var pathMapMock = new Mock<Listenarr.Api.Services.IRemotePathMappingService>();
            var adapter = new QbittorrentAdapter(factory, pathMapMock.Object, Mock.Of<ITorrentFileDownloader>(), NullLogger<QbittorrentAdapter>.Instance);

            var cfg = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111/qbt",
                Port = 8080,
                UseSSL = false
            };

            var (success, message) = await adapter.TestConnectionAsync(cfg);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(capturedUri);
            Assert.Equal("http", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(8080, capturedUri.Port);
            Assert.Equal("/api/v2/app/version", capturedUri.AbsolutePath);
        }

        [Fact]
        public async Task AddAsync_WhenMagnetAndTorrentUrlAreProvided_PredownloadsTorrentUrlFirst()
        {
            string? requestedTorrentUrl = null;
            var port = GetAvailablePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var serverTask = Task.Run(async () =>
            {
                for (var requestIndex = 0; requestIndex < 4; requestIndex++)
                {
                    var context = await listener.GetContextAsync();
                    var pathAndQuery = context.Request.Url!.PathAndQuery;

                    if (context.Request.HttpMethod == HttpMethod.Post.Method &&
                        pathAndQuery.StartsWith("/api/v2/auth/login", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(context.Response, HttpStatusCode.OK, "Ok", "text/plain");
                        continue;
                    }

                    if (context.Request.HttpMethod == HttpMethod.Get.Method &&
                        pathAndQuery.StartsWith("/api/v2/torrents/info?fields=hash,name", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(context.Response, HttpStatusCode.OK, """[{"hash":"NEWHASH","name":"Book"}]""");
                        continue;
                    }

                    if (context.Request.HttpMethod == HttpMethod.Get.Method &&
                        pathAndQuery.StartsWith("/api/v2/torrents/info?fields=hash", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(context.Response, HttpStatusCode.OK, "[]");
                        continue;
                    }

                    if (context.Request.HttpMethod == HttpMethod.Post.Method &&
                        pathAndQuery.StartsWith("/api/v2/torrents/add", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(context.Response, HttpStatusCode.OK, "Ok.", "text/plain");
                        continue;
                    }

                    throw new InvalidOperationException($"Unexpected qBittorrent request: {context.Request.HttpMethod} {context.Request.Url}");
                }
            });

            using var http = new HttpClient();
            var downloader = new Mock<ITorrentFileDownloader>(MockBehavior.Strict);
            downloader
                .Setup(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((url, _) => requestedTorrentUrl = url)
                .ReturnsAsync(TorrentDownloadResult.Empty);

            var adapter = new QbittorrentAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<Listenarr.Api.Services.IRemotePathMappingService>(),
                downloader.Object,
                NullLogger<QbittorrentAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Host = "127.0.0.1",
                Port = port,
                Username = "admin",
                Password = "admin"
            };

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890",
                TorrentUrl = "https://indexer.example.com/book.torrent"
            };

            var addedId = await adapter.AddAsync(client, searchResult);
            await serverTask;

            Assert.Equal("NEWHASH", addedId);
            Assert.Equal("https://indexer.example.com/book.torrent", requestedTorrentUrl);
            downloader.Verify(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AddAsync_WhenTorrentUrlUsesInvalidScheme_ThrowsArgumentException()
        {
            using var http = new HttpClient(new DelegatingHandlerMock((_, _) =>
                throw new InvalidOperationException("Network should not be hit for invalid torrent URLs.")));

            var adapter = new QbittorrentAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<Listenarr.Api.Services.IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<QbittorrentAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 8080
            };

            var searchResult = new SearchResult
            {
                Title = "Book",
                TorrentUrl = "ftp://indexer.example.com/book.torrent"
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => adapter.AddAsync(client, searchResult));

            Assert.Contains("HTTP or HTTPS", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "SingleFileResolvesContentFilePath")]
        public async Task GetImportItemAsync_SingleFileTorrent_ResolvesSpecificFilePath()
        {
            var files = ParseFiles("[{\"name\":\"Book.m4b\"}]");
            var resolvedPath = QbittorrentAdapter.ResolveTorrentContentPath("/downloads/audiobooks", files);
            Assert.Equal("/downloads/audiobooks/Book.m4b", NormalizePath(resolvedPath));
            await Task.CompletedTask;
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "MultiFileResolvesTopLevelDirectory")]
        public async Task GetImportItemAsync_MultiFileTorrent_ResolvesTopLevelFolderPath()
        {
            var files = ParseFiles("[{\"name\":\"Series Book/file1.m4b\"},{\"name\":\"Series Book/file2.m4b\"}]");
            var resolvedPath = QbittorrentAdapter.ResolveTorrentContentPath("/downloads/audiobooks", files);
            Assert.Equal("/downloads/audiobooks/Series Book", NormalizePath(resolvedPath));
            await Task.CompletedTask;
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "DockerAutoImportAppliesRemotePathMapping")]
        public async Task GetImportItemAsync_PrepopulatedContentPath_AppliesRemoteMapping_ForDockerAutoImport()
        {
            string localPath = FileUtils.GetAbsolutePath("media", "downloads", "Stephen King", "It.m4b");
            var pathMapMock = new Mock<Listenarr.Api.Services.IRemotePathMappingService>(MockBehavior.Strict);
            pathMapMock
                .Setup(m => m.TranslatePathAsync("qbit-client", "/qbit-downloads/Stephen King/It.m4b"))
                .ReturnsAsync(localPath);

            using var http = new HttpClient(new DelegatingHandlerMock((_, _) =>
                throw new InvalidOperationException("HTTP should not be called when qBittorrent content_path is already available.")));

            var adapter = new QbittorrentAdapter(
                new TestHttpClientFactory(http),
                pathMapMock.Object,
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<QbittorrentAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Id = "qbit-client",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080
            };

            var queueItem = new QueueItem
            {
                Id = "dl-qbit-docker",
                Title = "It",
                Status = "completed",
                ContentPath = "/qbit-downloads/Stephen King/It.m4b",
                DownloadClientId = client.Id
            };

            var resolved = await adapter.GetImportItemAsync(client, new Download { Id = queueItem.Id }, queueItem);

            Assert.Equal(localPath, resolved.ContentPath);
            pathMapMock.Verify(
                m => m.TranslatePathAsync("qbit-client", "/qbit-downloads/Stephen King/It.m4b"),
                Times.Once);
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "LocalAutoImportKeepsExistingPath")]
        public async Task GetImportItemAsync_PrepopulatedContentPath_KeepsLocalPath_ForNonDockerAutoImport()
        {
            string localPath = FileUtils.GetAbsolutePath("media", "downloads", "Stephen King", "It.m4b");
            var pathMapMock = new Mock<Listenarr.Api.Services.IRemotePathMappingService>(MockBehavior.Strict);
            pathMapMock
                .Setup(m => m.TranslatePathAsync("qbit-client", localPath))
                .ReturnsAsync(localPath);

            using var http = new HttpClient(new DelegatingHandlerMock((_, _) =>
                throw new InvalidOperationException("HTTP should not be called when qBittorrent content_path is already available.")));

            var adapter = new QbittorrentAdapter(
                new TestHttpClientFactory(http),
                pathMapMock.Object,
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<QbittorrentAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Id = "qbit-client",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080
            };

            var queueItem = new QueueItem
            {
                Id = "dl-qbit-local",
                Title = "It",
                Status = "completed",
                ContentPath = localPath,
                DownloadClientId = client.Id
            };

            var resolved = await adapter.GetImportItemAsync(client, new Download { Id = queueItem.Id }, queueItem);

            Assert.Equal(localPath, resolved.ContentPath);
            pathMapMock.Verify(m => m.TranslatePathAsync("qbit-client", localPath), Times.Once);
        }

        private static List<Dictionary<string, JsonElement>> ParseFiles(string json)
        {
            var root = JsonDocument.Parse(json).RootElement;
            var files = new List<Dictionary<string, JsonElement>>();

            foreach (var element in root.EnumerateArray())
            {
                var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = property.Value;
                }
                files.Add(map);
            }

            return files;
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, string body, string contentType = "application/json")
        {
            var payload = Encoding.UTF8.GetBytes(body);
            response.StatusCode = (int)statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload, 0, payload.Length);
            response.Close();
        }
    }
}
