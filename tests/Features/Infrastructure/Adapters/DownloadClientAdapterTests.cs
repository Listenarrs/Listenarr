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
using Listenarr.Domain.Models;
using Listenarr.Domain.Common;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Adapters
{
    [Trait("Name", "DownloadClientAdapterTests")]
    [Trait("Category", "DownloadClientAdapter")]
    public class DownloadClientAdapterTests : BaseTests
    {
        private DownloadClientConfiguration? _transmissionClient;
        private DownloadClientConfiguration? _sabnzbdClient;
        private DownloadClientConfiguration? _nzbgetClient;

        public override async Task InitializeAsync()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}-{ChapterNumber:00}")
                .Build());

            _transmissionClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("trans-client")
                .WithType("transmission")
                .WithHost("localhost")
                .WithPort(9091)
                .Build());

            _sabnzbdClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("sab-client")
                .WithType("sabnzbd")
                .WithHost("localhost")
                .WithPort(8080)
                .WithApiKey("apiKey")
                .Build());

            _nzbgetClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("nzb-client")
                .WithType("nzbget")
                .WithHost("localhost")
                .WithPort(6789)
                .WithApiKey("apiKey")
                .Build());

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithRemotePath(FileUtils.GetAbsolutePath("downloads"))
                .WithLocalPath(FileUtils.GetAbsolutePath("import"))
                .WithDownloadClientConfiguration(_transmissionClient)
                .Build());

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithRemotePath(FileUtils.GetAbsolutePath("completed"))
                .WithLocalPath(FileUtils.GetAbsolutePath("imports", "sab"))
                .WithDownloadClientConfiguration(_sabnzbdClient)
                .Build());

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithRemotePath(FileUtils.GetAbsolutePath("nzbget", "completed"))
                .WithLocalPath(FileUtils.GetAbsolutePath("imports"))
                .WithDownloadClientConfiguration(_nzbgetClient)
                .Build());
        }

        public static TheoryData<int, string> TransmissionGetImportItemAsyncCases => new() {
            { TransmissionApiMock.SINGLE_FILE_TORRENT, FileUtils.GetAbsolutePath("downloads", "Book.m4b") },
            { TransmissionApiMock.MULTI_FILE_TORRENT, FileUtils.GetAbsolutePath("downloads", "Book Folder") }
        };

        public static TheoryData<string, string> SabnzbdGetImportItemAsyncCases => new() {
            { SabnzbdApiMock.SINGLE_FILE_SABNZBD, FileUtils.GetAbsolutePath("completed", "Book.m4b")  },
            { SabnzbdApiMock.MULTI_FILE_SABNZBD, FileUtils.GetAbsolutePath("completed", "Book Folder") }
        };

        public static TheoryData<string, string> NzbgetGetImportItemAsyncCases => new() {
            { NzbgetApiMock.SINGLE_FILE_NZBGET, FileUtils.GetAbsolutePath("nzbget", "completed", "Book.m4b") },
            { NzbgetApiMock.MULTI_FILE_NZBGET, FileUtils.GetAbsolutePath("nzbget", "completed", "Book Folder") }
        };

        [Theory]
        [Trait("Third-Party", "Transmission")]
        [Trait("Method", "GetImportItemAsync")]
        [MemberData(nameof(TransmissionGetImportItemAsyncCases))]
        public async Task Transmission_GetImportItemAsync_ResolvesPath(int downloadId, string expectedPath)
        {
            var item = new DownloadClientItem
            {
                DownloadId = downloadId.ToString(),
                OutputPath = string.Empty
            };

            var adapter = MockUtils.CreateTransmissionAdapter(_provider);
            var resolved = await adapter.GetImportItemAsync(_transmissionClient, item);

            Assert.Equal(expectedPath, resolved.OutputPath);
        }

        [Fact]
        [Trait("Third-Party", "Transmission")]
        [Trait("Method", "GetImportItemAsync")]
        public async Task Transmission_LegacyGetImportItemAsync_PopulatesClientReportedSourceFiles()
        {
            var item = new QueueItem
            {
                Id = TransmissionApiMock.MULTI_FILE_TORRENT.ToString(),
                ContentPath = string.Empty
            };

            var download = new DownloadBuilder().Build();

            await _downloadRepository.AddAsync(download);

            var adapter = MockUtils.CreateTransmissionAdapter(_provider);
            var resolved = await adapter.GetImportItemAsync(_transmissionClient, download, item);

            Assert.Equal(FileUtils.GetAbsolutePath("downloads", "Book Folder"), resolved.ContentPath);
            Assert.Equal(
                new[]
                {
                    FileUtils.GetAbsolutePath("downloads", "Book Folder", "chapter1.m4b"),
                    FileUtils.GetAbsolutePath("downloads", "Book Folder", "book.txt")
                },
                resolved.SourceFiles);
        }

        [Theory]
        [Trait("Third-Party", "Sabnzbd")]
        [Trait("Method", "GetImportItemAsync")]
        [MemberData(nameof(SabnzbdGetImportItemAsyncCases))]
        public async Task Sabnzbd_GetImportItemAsync_ResolvesPath(string downloadId, string expectedPath)
        {
            var item = new DownloadClientItem
            {
                DownloadId = downloadId,
                OutputPath = string.Empty
            };

            var adapter = MockUtils.CreateSabnzbdAdapter(_provider);
            var resolved = await adapter.GetImportItemAsync(_sabnzbdClient, item);

            Assert.Equal(expectedPath, resolved.OutputPath);
        }

        [Theory]
        [Trait("Third-Party", "Nzbget")]
        [Trait("Method", "GetImportItemAsync")]
        [MemberData(nameof(NzbgetGetImportItemAsyncCases))]
        public async Task Nzbget_GetImportItemAsync_ResolvesPath(string downloadId, string expectedPath)
        {
            var item = new DownloadClientItem
            {
                DownloadId = downloadId,
                OutputPath = string.Empty
            };

            var adapter = MockUtils.CreateNzbgetAdapter(_provider);
            var resolved = await adapter.GetImportItemAsync(_nzbgetClient, item);

            Assert.Equal(expectedPath, resolved.OutputPath);
        }
    }
}
