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
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Services
{
    public class downloadImportServiceHardlinkTests : BaseTests
    {
        private string _outputRoot = "";
        private string _sourceDir = "";

        private ApplicationSettings _settings = new ApplicationSettingsBuilder()
            .WithMoveFileOnCompleted()
            .WithoutMetadataProcessing()
            .Build();

        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder()
            .Build();

        private Audiobook _audiobook = new AudiobookBuilder()
            .Build();

        private Download _download = new DownloadBuilder()
            .Build();

        public override async Task InitializeAsync()
        {
            _outputRoot = FileService.GetTempDirectory("import-hardlink-out");
            _sourceDir = FileService.GetTempDirectory("import-hardlink-src");

            _audiobook.BasePath = _outputRoot;

            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            _settings.OutputPath = _outputRoot;
            await _applicationSettingsRepository.SaveAsync(_settings);

            await _downloadClientConfigurationRepository.SaveAsync(_client);

            await _audiobookRepository.AddAsync(_audiobook);

            _download.DownloadClientId = _client.Id;
            _download.AudiobookId = _audiobook.Id;
            await _downloadRepository.AddAsync(_download);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_UsesHardlink_WhenModeIsHardlinkCopy()
        {
            // Arrange
            var file1 = Path.Join(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            _settings.CompletedFileAction = FileAction.Copy;
            await _applicationSettingsRepository.SaveAsync(_settings);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            // Act
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [file1]);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed");

            var successResult = results.First(r => r.Success);
            Assert.True(File.Exists(successResult.FinalPath), "Destination file should exist");
            Assert.True(File.Exists(file1), "Source file should still exist (hardlink/copy preserves source)");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_UsesMove_WhenModeIsMove()
        {
            // Arrange
            var file1 = Path.Join(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            // Act
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [file1]);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed");

            var successResult = results.First(r => r.Success);
            Assert.True(File.Exists(successResult.FinalPath), "Destination file should exist");
            Assert.False(File.Exists(file1), "Source file should be removed (moved)");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_UsesCopy_WhenModeIsCopy()
        {
            // Arrange
            var file1 = Path.Join(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            _settings.CompletedFileAction = FileAction.Copy;
            await _applicationSettingsRepository.SaveAsync(_settings);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            // Act
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [file1]);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed");

            var successResult = results.First(r => r.Success);
            Assert.True(File.Exists(successResult.FinalPath), "Destination file should exist");
            Assert.True(File.Exists(file1), "Source file should still exist (copy preserves source)");
        }

        [Fact]
        public async Task ImportSingleFileAsync_UsesHardlink_WhenModeIsHardlinkCopy()
        {
            // Arrange
            var sourceFile = Path.Join(_sourceDir, "audiobook.m4b");
            await File.WriteAllTextAsync(sourceFile, "single file audio");

            _settings.CompletedFileAction = FileAction.HardlinkCopy;
            await _applicationSettingsRepository.SaveAsync(_settings);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            // Act
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [sourceFile]);
            Assert.Single(results);
            var result = results.First();

            // Assert
            Assert.True(result.Success, "Single file import should succeed");
            Assert.True(File.Exists(result.FinalPath), "Destination file should exist");
            Assert.True(File.Exists(sourceFile), "Source file should still exist");
        }

        [Fact]
        public async Task ImportSingleFileAsync_UsesMove_WhenModeIsMove()
        {
            // Arrange
            var sourceFile = Path.Join(_sourceDir, "audiobook.m4b");
            await File.WriteAllTextAsync(sourceFile, "single file audio");

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            // Act
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [sourceFile]);
            Assert.Single(results);
            var result = results.First();

            // Assert
            Assert.True(result.Success, "Single file import should succeed");
            Assert.True(File.Exists(result.FinalPath), "Destination file should exist");
            Assert.False(File.Exists(sourceFile), "Source file should be removed");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_HandlesMultipleFiles_WithHardlinkCopy()
        {
            // Arrange
            var file1 = Path.Join(_sourceDir, "track1.m4b");
            var file2 = Path.Join(_sourceDir, "track2.m4b");
            var file3 = Path.Join(_sourceDir, "track3.m4b");
            await File.WriteAllTextAsync(file1, "audio1");
            await File.WriteAllTextAsync(file2, "audio2");
            await File.WriteAllTextAsync(file3, "audio3");

            _settings.CompletedFileAction = FileAction.HardlinkCopy;
            await _applicationSettingsRepository.SaveAsync(_settings);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            // Act
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [file1, file2, file3]);

            // Assert
            var successResults = results.Where(r => r.Success).ToList();
            Assert.True(successResults.Count >= 2, "At least 2 files should import successfully");

            foreach (var result in successResults)
            {
                Assert.True(File.Exists(result.FinalPath), $"Destination {result.FinalPath} should exist");
            }

            // All source files should still exist
            Assert.True(File.Exists(file1), "Source file 1 should still exist");
            Assert.True(File.Exists(file2), "Source file 2 should still exist");
            Assert.True(File.Exists(file3), "Source file 3 should still exist");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_FallbacksToCopy_WhenHardlinkFails()
        {
            // Arrange - hardlink might fail on some systems or across volumes
            var file1 = Path.Join(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            _settings.CompletedFileAction = FileAction.HardlinkCopy;
            await _applicationSettingsRepository.SaveAsync(_settings);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            // Act - even if hardlink fails, should fallback to copy
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [file1]);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed via copy fallback");
            Assert.True(File.Exists(file1), "Source should still exist after hardlink/copy");
        }
    }
}
