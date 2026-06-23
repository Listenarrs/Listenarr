/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;
using Listenarr.Infrastructure.Torrents;
using Listenarr.Domain.Downloads.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Deluge
{
    [Trait("Name", "DelugeAdapterTests")]
    [Trait("Category", "DownloadClientAdapter")]
    [Trait("Third-Party", "Deluge")]
    public class DelugeAdapterTests : BaseTests
    {
        private DownloadClientConfiguration _client = null!;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("deluge-1")
                .WithName("Deluge")
                .WithType("deluge")
                .WithHost("localhost")
                .WithPort(8112)
                .WithPassword("deluge")
                .Build());
        }

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
            var mock = _provider.GetRequiredService<DelugeApiMock>();
            mock.UpdateUiResponseOverride = BuildUpdateUiResponse(state, progress, "listenarr");

            var adapter = MockUtils.CreateDelugeAdapter(_provider);
            var items = await adapter.GetItemsAsync(_client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal(expectedStatus, items[0].Status);
            Assert.Equal("ABCDEF1234567890", items[0].DownloadId);
            Assert.Equal("Book.m4b", items[0].Title);
            Assert.Equal("/downloads/Book.m4b", items[0].OutputPath);
        }

        [Fact]
        public async Task GetItemsAsync_FiltersByConfiguredCategory()
        {
            var mock = _provider.GetRequiredService<DelugeApiMock>();
            mock.UpdateUiResponseOverride = """
            {
              "id": 1,
              "result": {
                "torrents": {
                  "HASH1": {
                    "name": "Book One",
                    "total_size": 100,
                    "total_done": 100,
                    "progress": 100.0,
                    "download_payload_rate": 0,
                    "eta": 0,
                    "state": "Seeding",
                    "save_path": "/downloads",
                    "label": "listenarr",
                    "ratio": 1.0,
                    "num_seeds": 1,
                    "num_peers": 0,
                    "time_added": 1700000000,
                    "message": ""
                  },
                  "HASH2": {
                    "name": "Movie One",
                    "total_size": 100,
                    "total_done": 100,
                    "progress": 100.0,
                    "download_payload_rate": 0,
                    "eta": 0,
                    "state": "Seeding",
                    "save_path": "/downloads",
                    "label": "movies",
                    "ratio": 1.0,
                    "num_seeds": 1,
                    "num_peers": 0,
                    "time_added": 1700000000,
                    "message": ""
                  }
                }
              },
              "error": null
            }
            """;

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("deluge-category-test")
                .WithName("Deluge")
                .WithType("deluge")
                .WithHost("localhost")
                .WithPort(8112)
                .WithPassword("deluge")
                .WithSettings("category", "listenarr")
                .Build());

            var adapter = MockUtils.CreateDelugeAdapter(_provider);
            var items = await adapter.GetItemsAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal("HASH1", items[0].DownloadId);
            Assert.Equal("listenarr", items[0].Category);
        }

        [Fact]
        public async Task TestConnectionAsync_AuthenticatesAndRequiresDaemonConnection()
        {
            var mock = _provider.GetRequiredService<DelugeApiMock>();
            mock.UpdateUiResponseOverride = BuildUpdateUiResponse("Seeding", 100.0, "listenarr");

            var adapter = MockUtils.CreateDelugeAdapter(_provider);
            var (success, message) = await adapter.TestConnectionAsync(_client, CancellationToken.None);

            Assert.True(success);
            Assert.Contains("connected to Web UI and daemon", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetImportItemAsync_MatchesQueueItemByExternalClientId()
        {
            var mock = _provider.GetRequiredService<DelugeApiMock>();
            mock.UpdateUiResponseOverride = BuildUpdateUiResponse("Seeding", 100.0, "listenarr");

            var download = new Download
            {
                Id = "listenarr-download-1",
                DownloadClientId = _client.Id
            };
            download.SetExternalId("ABCDEF1234567890");

            var fallback = new QueueItem
            {
                Id = "listenarr-download-1",
                Title = "Fallback"
            };

            var adapter = MockUtils.CreateDelugeAdapter(_provider);
            var item = await adapter.GetImportItemAsync(_client, download, fallback, null, CancellationToken.None);

            Assert.Equal("ABCDEF1234567890", item.Id);
            Assert.Equal("Book.m4b", item.Title);
            Assert.Equal("completed", item.Status);
        }

        [Fact]
        public async Task GetQueueAsync_WithTrackedIds_ResolvesNormally()
        {
            var mock = _provider.GetRequiredService<DelugeApiMock>();
            mock.UpdateUiResponseOverride = BuildUpdateUiResponse("Seeding", 100.0, "listenarr");

            var adapter = MockUtils.CreateDelugeAdapter(_provider);
            var items = await adapter.GetQueueAsync(_client, ["ABCDEF1234567890"], CancellationToken.None);

            Assert.Single(items);
            Assert.Equal("ABCDEF1234567890", items[0].Id);
            Assert.Equal("completed", items[0].Status);
        }

        [Fact]
        public async Task GetQueueAsync_WithIds_ThrowsPollingException_OnQueueRequestFailure()
        {
            var mock = _provider.GetRequiredService<DelugeApiMock>();
            mock.FailRequests = true;

            var adapter = MockUtils.CreateDelugeAdapter(_provider);
            await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => adapter.GetQueueAsync(_client, ["ABCDEF1234567890"]));
        }

        [Fact]
        public async Task GetQueueAsync_WithoutIds_ReturnsEmpty_OnQueueRequestFailure()
        {
            var mock = _provider.GetRequiredService<DelugeApiMock>();
            mock.FailRequests = true;

            var adapter = MockUtils.CreateDelugeAdapter(_provider);
            var items = await adapter.GetQueueAsync(_client);

            Assert.Empty(items);
        }

        private static string BuildUpdateUiResponse(string state, double progress, string label)
            => $$"""
            {
              "id": 1,
              "result": {
                "torrents": {
                  "ABCDEF1234567890": {
                    "name": "Book.m4b",
                    "total_size": 100,
                    "total_done": {{(long)Math.Round(progress)}},
                    "progress": {{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                    "download_payload_rate": 25,
                    "eta": 60,
                    "state": "{{state}}",
                    "save_path": "/downloads",
                    "label": "{{label}}",
                    "ratio": 1.0,
                    "num_seeds": 1,
                    "num_peers": 0,
                    "time_added": 1700000000,
                    "message": ""
                  }
                }
              },
              "error": null
            }
            """;
    }
}
