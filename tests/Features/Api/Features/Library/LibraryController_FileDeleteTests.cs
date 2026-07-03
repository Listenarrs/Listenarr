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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_FileDeleteTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_FileDeleteTests : BaseTests
    {
        private static JsonElement ToJson(object? value)
        {
            return JsonSerializer.SerializeToElement(value);
        }

        private async Task<Audiobook> CreateAudiobookWithFolderAsync(string folderName = "file-delete")
        {
            return await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test Book")
                .WithBasePath(FileService.GetTempDirectory(folderName))
                .Build());
        }

        private async Task<AudiobookFile> AddFileOnDiskAsync(Audiobook owner, string fileName)
        {
            var path = Path.Join(owner.BasePath, fileName);
            Directory.CreateDirectory(owner.BasePath!);
            await File.WriteAllTextAsync(path, "audio");
            return await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(owner)
                .WithPath(path)
                .Build());
        }

        [Fact]
        [Trait("Method", "DeleteAudiobookFile")]
        [Trait("Scenario", "ReturnsNotFound_WhenAudiobookMissing")]
        public async Task DeleteAudiobookFile_ReturnsNotFound_WhenAudiobookMissing()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.DeleteAudiobookFile(999_101, 1, deleteFromDisk: false, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        [Trait("Method", "DeleteAudiobookFile")]
        [Trait("Scenario", "ReturnsNotFound_WhenFileMissing")]
        public async Task DeleteAudiobookFile_ReturnsNotFound_WhenFileMissing()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var ab = await CreateAudiobookWithFolderAsync();

            var result = await controller.DeleteAudiobookFile(ab.Id, 999_102, deleteFromDisk: false, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        [Trait("Method", "DeleteAudiobookFile")]
        [Trait("Scenario", "ReturnsBadRequest_WhenFileBelongsToAnotherAudiobook")]
        public async Task DeleteAudiobookFile_ReturnsBadRequest_WhenFileBelongsToAnotherAudiobook()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var owner = await CreateAudiobookWithFolderAsync("file-delete-owner");
            var other = await CreateAudiobookWithFolderAsync("file-delete-other");
            var file = await AddFileOnDiskAsync(owner, "part1.m4b");

            var result = await controller.DeleteAudiobookFile(other.Id, file.Id, deleteFromDisk: false, CancellationToken.None);

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("does not belong", bad.Value?.ToString() ?? string.Empty);

            // File untouched.
            Assert.NotNull(await _audiobookFileRepository.GetByIdAsync(file.Id));
            Assert.True(File.Exists(file.Path));
        }

        [Fact]
        [Trait("Method", "DeleteAudiobookFile")]
        [Trait("Scenario", "DbOnlyDelete_RemovesRow_KeepsFileOnDisk")]
        public async Task DeleteAudiobookFile_DbOnlyDelete_RemovesRow_KeepsFileOnDisk()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var ab = await CreateAudiobookWithFolderAsync();
            var file = await AddFileOnDiskAsync(ab, "part1.m4b");

            var result = await controller.DeleteAudiobookFile(ab.Id, file.Id, deleteFromDisk: false, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.False(payload.GetProperty("deletedFromDisk").GetBoolean());

            Assert.Null(await _audiobookFileRepository.GetByIdAsync(file.Id));
            Assert.True(File.Exists(file.Path));

            var history = await _historyRepository.GetByAudiobookIdAsync(ab.Id);
            Assert.Contains(history, h => h.EventType == "File Removed");
        }

        [Fact]
        [Trait("Method", "DeleteAudiobookFile")]
        [Trait("Scenario", "DiskDelete_RemovesRowAndFile")]
        public async Task DeleteAudiobookFile_DiskDelete_RemovesRowAndFile()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var ab = await CreateAudiobookWithFolderAsync();
            var file = await AddFileOnDiskAsync(ab, "part1.m4b");

            var result = await controller.DeleteAudiobookFile(ab.Id, file.Id, deleteFromDisk: true, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.True(payload.GetProperty("deletedFromDisk").GetBoolean());
            Assert.Empty(payload.GetProperty("warnings").EnumerateArray());

            Assert.Null(await _audiobookFileRepository.GetByIdAsync(file.Id));
            Assert.False(File.Exists(file.Path));
        }

        [Fact]
        [Trait("Method", "DeleteAudiobookFile")]
        [Trait("Scenario", "DiskDelete_FileAlreadyGone_StillRemovesRow")]
        public async Task DeleteAudiobookFile_DiskDelete_FileAlreadyGone_StillRemovesRow()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var ab = await CreateAudiobookWithFolderAsync();

            // Row whose physical file does not exist.
            var ghostPath = Path.Join(ab.BasePath, "ghost.m4b");
            var ghost = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(ab)
                .WithPath(ghostPath)
                .Build());

            var result = await controller.DeleteAudiobookFile(ab.Id, ghost.Id, deleteFromDisk: true, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.False(payload.GetProperty("deletedFromDisk").GetBoolean());

            Assert.Null(await _audiobookFileRepository.GetByIdAsync(ghost.Id));
        }
    }
}
