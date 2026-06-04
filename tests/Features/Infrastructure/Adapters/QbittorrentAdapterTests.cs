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
using System.Text.Json;
using Listenarr.Domain.Models;
using Listenarr.Domain.Common;
using Listenarr.Tests.Common;
using Moq;
using Xunit;
using Listenarr.Infrastructure.Adapters;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.DependencyInjection;
using Listenarr.Application.Interfaces;
using Listenarr.Tests.Mocks.Api;
using Listenarr.Application.Downloads;
using Listenarr.Infrastructure.Torrents;

namespace Listenarr.Tests.Features.Infrastructure.Adapters
{
    public class QbittorrentAdapterTests : BaseTests
    {
        private DownloadClientConfiguration _client = null!;

        public override async Task InitializeAsync()
        {
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(443)
                .WithSsl()
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_Then_LoginSucceeds_ReturnsSuccess()
        {
            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var (success, message) = await adapter.TestConnectionAsync(_client);

            Assert.True(success);
            Assert.Contains("Successfully connected to qBittorrent", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_And_NoCredentials_ReturnsForbidden()
        {
            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(443)
                .WithSsl()
                .WithType("qbittorrent")
                .Build());

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.False(success);
            Assert.Contains("Forbidden", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_NormalizesHostWithSchemeAndPath()
        {
            var mock = _provider.GetRequiredService<QbittorrentApiMock>();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("192.168.50.111")
                .WithPort(8080)
                .WithoutSsl()
                .WithType("qbittorrent")
                .WithUsername("admin")
                .WithPassword("admin")
                .Build());

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.Contains("Successfully connected to qBittorrent", message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(mock.GetLastRequest());

            var uri = mock.GetLastRequest().RequestUri;
            Assert.Equal("http", uri.Scheme);
            Assert.Equal("192.168.50.111", uri.Host);
            Assert.Equal(8080, uri.Port);
            Assert.Equal("/api/v2/app/version", uri.AbsolutePath);
        }

        [Fact]
        public async Task AddAsync_WhenMagnetAndTorrentUrlAreProvided_PredownloadsTorrentUrlFirst()
        {
            string? requestedTorrentUrl = null;
            var downloader = new Mock<ITorrentFileDownloader>(MockBehavior.Strict);
            downloader
                .Setup(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((url, _) => requestedTorrentUrl = url)
                .ReturnsAsync(TorrentDownloadResult.Empty);
            _services.AddSingleton(downloader.Object);
            Init();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890",
                TorrentUrl = "https://indexer.example.com/book.torrent"
            };

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var hash = await adapter.AddAsync(client, searchResult);

            Assert.Equal("ABCDEF1234567890".ToLowerInvariant(), hash);
            Assert.Equal("https://indexer.example.com/book.torrent", requestedTorrentUrl);
            downloader.Verify(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AddAsync_WhenTorrentUrlUsesInvalidScheme_ThrowsArgumentException()
        {
            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                TorrentUrl = "ftp://indexer.example.com/book.torrent"
            };

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => adapter.AddAsync(client, searchResult));

            Assert.Contains("HTTP or HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "SingleFileResolvesContentFilePath")]
        public async Task GetImportItemAsync_SingleFileTorrent_ResolvesSpecificFilePath()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads", "audiobooks");

            var files = ParseFiles("[{\"name\":\"Book.m4b\"}]");
            var resolvedPath = QbittorrentAdapter.ResolveTorrentContentPath(savePath, files);

            var expectedPath = FileUtils.CombineWithOptionalBase(savePath, "Book.m4b");
            Assert.Equal(expectedPath, FileUtils.NormalizeStoredPath(resolvedPath));
            await Task.CompletedTask;
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "MultiFileResolvesTopLevelDirectory")]
        public async Task GetImportItemAsync_MultiFileTorrent_ResolvesTopLevelFolderPath()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads", "audiobooks");

            var files = ParseFiles("[{\"name\":\"Series Book/file1.m4b\"},{\"name\":\"Series Book/file2.m4b\"}]");

            var resolvedPath = QbittorrentAdapter.ResolveTorrentContentPath(savePath, files);

            var expectedPath = FileUtils.CombineWithOptionalBase(savePath, "Series Book");
            Assert.Equal(expectedPath, FileUtils.NormalizeStoredPath(resolvedPath));
            await Task.CompletedTask;
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "LocalAutoImportKeepsExistingPath")]
        public async Task GetImportItemAsync_PrepopulatedContentPath_KeepsLocalPath_ForNonDockerAutoImport()
        {
            string localPath = FileUtils.GetAbsolutePath("media", "downloads", "Stephen King", "It.m4b");

            using var http = new HttpClient(new DelegatingHandlerMock((_, _) =>
                throw new InvalidOperationException("HTTP should not be called when qBittorrent content_path is already available.")));

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var queueItem = new QueueItem
            {
                Id = "dl-qbit-local",
                Title = "It",
                Status = "completed",
                ContentPath = localPath,
                DownloadClientId = client.Id
            };

            var adapter = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var qbittorrentAdapter = (QbittorrentAdapter)adapter.ResolveAdapter(client);
            var resolved = await qbittorrentAdapter.GetImportItemAsync(client, new Download { Id = queueItem.Id }, queueItem);

            Assert.Equal(localPath, resolved.ContentPath);
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

        [Fact]
        public async Task AddAsync_ComputeHash_FromTorrentFile()
        {
            var filePath = TestUtils.GetDataPath("big-buck-bunny.torrent");
            var content = await File.ReadAllBytesAsync(filePath);

            var searchResult = new SearchResultBuilder()
                .WithTorrentData(content)
                .Build();

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var hash = await adapter.AddAsync(_client, searchResult);

            Assert.Equal("DD8255ECDC7CA55FB0BBF81323D87062DB1F6D1C", hash);
        }
    }
}
