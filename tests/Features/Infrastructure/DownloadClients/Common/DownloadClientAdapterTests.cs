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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Common
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
        [Fact]
        [Trait("Third-Party", "Transmission")]
        [Trait("Method", "GetImportItemAsync")]
        public async Task Transmission_LegacyGetImportItemAsync_PreservesWhitespaceBearingFolderPaths()
        {
            var item = new QueueItem
            {
                Id = TransmissionApiMock.WHITESPACE_FOLDER_TORRENT.ToString(),
                ContentPath = string.Empty
            };

            var download = new DownloadBuilder().Build();
            await _downloadRepository.AddAsync(download);

            var adapter = MockUtils.CreateTransmissionAdapter(_provider);
            var resolved = await adapter.GetImportItemAsync(_transmissionClient, download, item);

            Assert.Equal(FileUtils.GetAbsolutePath("downloads", " Book Folder "), resolved.ContentPath);
            Assert.Equal(
                new[]
                {
                    FileUtils.GetAbsolutePath("downloads", " Book Folder ", "chapter1.m4b"),
                    FileUtils.GetAbsolutePath("downloads", " Book Folder ", "book.txt")
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

        [Fact]
        [Trait("Third-Party", "Nzbget")]
        [Trait("Method", "GetImportItemAsync")]
        public async Task Nzbget_GetImportItemAsync_DownloadClientItemHistoryCompatibility_PreservesMethodParametersAndResponse()
        {
            var apiMock = _provider.GetRequiredService<NzbgetApiMock>();
            apiMock.ResetXmlRpcCapture();
            var historyResponse = NzbgetApiMock.CreateHistoryResponse(
                """
                <value><struct>
                  <member><name>ID</name><value><string>case-id</string></value></member>
                  <member><name>DestDir</name><value><string>{{PATH}}</string></value></member>
                </struct></value>
                """.Replace("{{PATH}}", FileUtils.GetAbsolutePath("nzbget", "completed", "Case Book")));
            apiMock.QueueXmlRpcResponse("history", historyResponse);
            apiMock.QueueXmlRpcResponse("history", historyResponse);
            var original = new DownloadClientItem
            {
                DownloadId = "CASE-ID",
                OutputPath = string.Empty,
                Title = "Original"
            };
            var unmatchedOriginal = new DownloadClientItem
            {
                DownloadId = "missing",
                OutputPath = string.Empty,
                Title = "Unmatched"
            };
            var adapter = MockUtils.CreateNzbgetAdapter(_provider);

            var resolved = await adapter.GetImportItemAsync(_nzbgetClient, original);
            var unmatched = await adapter.GetImportItemAsync(_nzbgetClient, unmatchedOriginal);

            Assert.NotSame(original, resolved);
            Assert.Equal(FileUtils.GetAbsolutePath("nzbget", "completed", "Case Book"), resolved.OutputPath);
            Assert.Equal(string.Empty, original.OutputPath);
            Assert.NotSame(unmatchedOriginal, unmatched);
            Assert.Equal("Unmatched", unmatched.Title);
            Assert.Equal(string.Empty, unmatched.OutputPath);
            Assert.All(apiMock.XmlRpcCalls, AssertHistoryFalseCall);
            Assert.Equal(2, apiMock.XmlRpcCalls.Count);
        }

        [Fact]
        [Trait("Third-Party", "Nzbget")]
        [Trait("Method", "GetImportItemAsync")]
        public async Task Nzbget_GetImportItemAsync_DownloadClientItemWithStaleOutputPath_ResolvesFromHistory()
        {
            var apiMock = _provider.GetRequiredService<NzbgetApiMock>();
            apiMock.ResetXmlRpcCapture();
            var completedPath = FileUtils.GetAbsolutePath("nzbget", "completed", "Resolved Client Item");
            var historyResponse = NzbgetApiMock.CreateHistoryResponse(
                """
                <value><struct>
                  <member><name>ID</name><value><string>case-id</string></value></member>
                  <member><name>FinalDir</name><value><string>{{COMPLETED_PATH}}</string></value></member>
                  <member><name>DestDir</name><value><string>{{IGNORED_PATH}}</string></value></member>
                </struct></value>
                """
                .Replace("{{COMPLETED_PATH}}", completedPath)
                .Replace("{{IGNORED_PATH}}", FileUtils.GetAbsolutePath("nzbget", "ignored", "Intermediate Client Item")));
            apiMock.QueueXmlRpcResponse("history", historyResponse);
            var missingPath = Path.Combine(
                Path.GetTempPath(),
                $"listenarr-nzbget-output-missing-{Guid.NewGuid():N}");
            var original = new DownloadClientItem
            {
                DownloadId = "CASE-ID",
                OutputPath = missingPath,
                Title = "Stale"
            };
            var adapter = MockUtils.CreateNzbgetAdapter(_provider);

            var resolved = await adapter.GetImportItemAsync(_nzbgetClient, original);

            Assert.False(File.Exists(missingPath));
            Assert.False(Directory.Exists(missingPath));
            Assert.NotSame(original, resolved);
            Assert.Equal(completedPath, resolved.OutputPath);
            Assert.Equal(missingPath, original.OutputPath);
            AssertHistoryFalseCall(Assert.Single(apiMock.XmlRpcCalls));
        }

        [Fact]
        [Trait("Third-Party", "Nzbget")]
        [Trait("Method", "GetImportItemAsync")]
        public async Task Nzbget_GetImportItemAsync_QueueItemHistoryCompatibility_PreservesMethodParametersAndResponse()
        {
            var apiMock = _provider.GetRequiredService<NzbgetApiMock>();
            apiMock.ResetXmlRpcCapture();
            var historyResponse = NzbgetApiMock.CreateHistoryResponse(
                """
                <value><struct>
                  <member><name>NZBID</name><value><string>501</string></value></member>
                  <member><name>FinalDir</name><value><string>{{FINAL_PATH}}</string></value></member>
                  <member><name>DestDir</name><value><string>{{IGNORED_PATH}}</string></value></member>
                </struct></value>
                <value><struct>
                  <member><name>NZBID</name><value><string>502</string></value></member>
                  <member><name>FinalDir</name><value><string></string></value></member>
                  <member><name>DestDir</name><value><string>{{DEST_PATH}}</string></value></member>
                </struct></value>
                <value><struct>
                  <member><name>NZBID</name><value><string>case-sensitive</string></value></member>
                  <member><name>FinalDir</name><value><string>{{CASE_PATH}}</string></value></member>
                </struct></value>
                """
                .Replace("{{FINAL_PATH}}", FileUtils.GetAbsolutePath("nzbget", "final"))
                .Replace("{{IGNORED_PATH}}", FileUtils.GetAbsolutePath("nzbget", "ignored"))
                .Replace("{{DEST_PATH}}", FileUtils.GetAbsolutePath("nzbget", "destination"))
                .Replace("{{CASE_PATH}}", FileUtils.GetAbsolutePath("nzbget", "case")));
            apiMock.QueueXmlRpcResponse("history", historyResponse);
            apiMock.QueueXmlRpcResponse("history", historyResponse);
            apiMock.QueueXmlRpcResponse("history", historyResponse);
            var adapter = MockUtils.CreateNzbgetAdapter(_provider);
            var download = new DownloadBuilder().Build();

            var finalDirResult = await adapter.GetImportItemAsync(
                _nzbgetClient,
                download,
                new QueueItem { Id = "501", ContentPath = string.Empty });
            var destDirResult = await adapter.GetImportItemAsync(
                _nzbgetClient,
                download,
                new QueueItem { Id = "502", ContentPath = string.Empty });
            var unmatchedOriginal = new QueueItem
            {
                Id = "CASE-SENSITIVE",
                ContentPath = string.Empty,
                Title = "Unmatched"
            };
            var unmatched = await adapter.GetImportItemAsync(
                _nzbgetClient,
                download,
                unmatchedOriginal);

            Assert.Equal(FileUtils.GetAbsolutePath("nzbget", "final"), finalDirResult.ContentPath);
            Assert.Equal(FileUtils.GetAbsolutePath("nzbget", "destination"), destDirResult.ContentPath);
            Assert.NotSame(unmatchedOriginal, unmatched);
            Assert.Equal("Unmatched", unmatched.Title);
            Assert.Equal(string.Empty, unmatched.ContentPath);
            Assert.All(apiMock.XmlRpcCalls, AssertHistoryFalseCall);
            Assert.Equal(3, apiMock.XmlRpcCalls.Count);
        }

        [Fact]
        [Trait("Third-Party", "Nzbget")]
        [Trait("Method", "GetImportItemAsync")]
        public async Task Nzbget_GetImportItemAsync_QueueItemWithMissingContentPath_ResolvesFromHistory()
        {
            var apiMock = _provider.GetRequiredService<NzbgetApiMock>();
            apiMock.ResetXmlRpcCapture();
            var completedPath = FileUtils.GetAbsolutePath("nzbget", "completed", "Resolved Book");
            var ignoredPath = FileUtils.GetAbsolutePath("nzbget", "ignored", "Intermediate Book");
            var historyResponse = NzbgetApiMock.CreateHistoryResponse(
                """
                <value><struct>
                  <member><name>NZBID</name><value><string>501</string></value></member>
                  <member><name>FinalDir</name><value><string>{{COMPLETED_PATH}}</string></value></member>
                  <member><name>DestDir</name><value><string>{{IGNORED_PATH}}</string></value></member>
                </struct></value>
                """
                .Replace("{{COMPLETED_PATH}}", completedPath)
                .Replace("{{IGNORED_PATH}}", ignoredPath));
            apiMock.QueueXmlRpcResponse("history", historyResponse);
            var missingPath = Path.Combine(
                Path.GetTempPath(),
                $"listenarr-nzbget-missing-{Guid.NewGuid():N}");
            var adapter = MockUtils.CreateNzbgetAdapter(_provider);
            var download = new DownloadBuilder().Build();
            var original = new QueueItem
            {
                Id = "501",
                ContentPath = missingPath,
                Title = "Stale"
            };

            var resolved = await adapter.GetImportItemAsync(_nzbgetClient, download, original);

            Assert.False(File.Exists(missingPath));
            Assert.False(Directory.Exists(missingPath));
            Assert.NotSame(original, resolved);
            Assert.Equal(completedPath, resolved.ContentPath);
            Assert.Equal(missingPath, original.ContentPath);
            AssertHistoryFalseCall(Assert.Single(apiMock.XmlRpcCalls));
        }

        [Theory]
        [Trait("Third-Party", "Nzbget")]
        [Trait("Method", "GetImportItemAsync")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Nzbget_GetImportItemAsync_QueueItemWithExistingContentPath_DoesNotQueryHistory(bool existingDirectory)
        {
            var apiMock = _provider.GetRequiredService<NzbgetApiMock>();
            apiMock.ResetXmlRpcCapture();
            var existingPath = Path.Combine(
                Path.GetTempPath(),
                $"listenarr-nzbget-existing-{Guid.NewGuid():N}");

            if (existingDirectory)
            {
                Directory.CreateDirectory(existingPath);
            }
            else
            {
                File.WriteAllText(existingPath, "existing NZBGet content");
            }

            try
            {
                var adapter = MockUtils.CreateNzbgetAdapter(_provider);
                var download = new DownloadBuilder().Build();
                var original = new QueueItem
                {
                    Id = "501",
                    ContentPath = existingPath,
                    Title = "Existing"
                };

                var resolved = await adapter.GetImportItemAsync(_nzbgetClient, download, original);

                Assert.NotSame(original, resolved);
                Assert.Equal(existingPath, resolved.ContentPath);
                Assert.Empty(apiMock.XmlRpcCalls);
            }
            finally
            {
                if (File.Exists(existingPath))
                {
                    File.Delete(existingPath);
                }

                if (Directory.Exists(existingPath))
                {
                    Directory.Delete(existingPath, recursive: true);
                }
            }
        }

        private static void AssertHistoryFalseCall(NzbgetApiMock.XmlRpcCall call)
        {
            Assert.Equal("history", call.MethodName);
            var parameter = Assert.Single(call.Parameters);
            Assert.Equal("0", parameter.Element("boolean")?.Value);
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
