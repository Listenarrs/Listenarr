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
    [Trait("Name", "LibraryController_TransferFilesTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_TransferFilesTests : BaseTests
    {
        private static JsonElement ToJson(object? value)
        {
            return JsonSerializer.SerializeToElement(value);
        }

        private async Task<(Audiobook source, Audiobook target)> CreateSourceAndTargetAsync(
            string sourceFolderName = "transfer-src",
            string targetFolderName = "transfer-dst")
        {
            var sourceFolder = FileService.GetTempDirectory(sourceFolderName);
            var targetFolder = Path.Join(FileService.GetTempPath(), targetFolderName);

            var source = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Arcanum Unbounded")
                .WithBasePath(sourceFolder)
                .Build());

            var target = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Warbreaker")
                .WithBasePath(targetFolder)
                .Build());

            return (source, target);
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
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "ReturnsBadRequest_WhenTargetIsSource")]
        public async Task TransferFiles_ReturnsBadRequest_WhenTargetIsSource()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, _) = await CreateSourceAndTargetAsync();

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = source.Id },
                CancellationToken.None);

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("different", bad.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "ReturnsNotFound_WhenSourceOrTargetMissing")]
        public async Task TransferFiles_ReturnsNotFound_WhenSourceOrTargetMissing()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, _) = await CreateSourceAndTargetAsync();

            var missingSource = await controller.TransferFiles(
                999_001,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = source.Id },
                CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(missingSource);

            var missingTarget = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = 999_002 },
                CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(missingTarget);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "ReturnsBadRequest_WhenFileIdsDoNotBelongToSource")]
        public async Task TransferFiles_ReturnsBadRequest_WhenFileIdsDoNotBelongToSource()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target) = await CreateSourceAndTargetAsync();
            var file = await AddFileOnDiskAsync(source, "part1.m4b");

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest
                {
                    TargetAudiobookId = target.Id,
                    FileIds = [file.Id, 999_003]
                },
                CancellationToken.None);

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("do not belong", bad.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "ReturnsBadRequest_WhenSourceHasNoFiles")]
        public async Task TransferFiles_ReturnsBadRequest_WhenSourceHasNoFiles()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target) = await CreateSourceAndTargetAsync();

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = target.Id },
                CancellationToken.None);

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("No matching files", bad.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "MovesAllFiles_ReassignsOwnership_AndClearsLegacyColumns")]
        public async Task TransferFiles_MovesAllFiles_ReassignsOwnership_AndClearsLegacyColumns()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target) = await CreateSourceAndTargetAsync();
            var file1 = await AddFileOnDiskAsync(source, "part1.m4b");
            var file2 = await AddFileOnDiskAsync(source, "part2.m4b");

            // Legacy single-file columns describe the audio the source is about to lose.
            source.FilePath = file1.Path;
            source.FileSize = 5;
            await _audiobookRepository.UpdateAsync(source);

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = target.Id },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(2, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(2, payload.GetProperty("physicallyMoved").GetInt32());
            Assert.Empty(payload.GetProperty("warnings").EnumerateArray());

            // DB ownership moved to the target, paths point into its folder.
            var sourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id);
            var targetFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id);
            Assert.Empty(sourceFiles);
            Assert.Equal(2, targetFiles.Count);
            Assert.All(targetFiles, f => Assert.StartsWith(target.BasePath!, f.Path!));

            // Physically relocated.
            Assert.True(File.Exists(Path.Join(target.BasePath, "part1.m4b")));
            Assert.True(File.Exists(Path.Join(target.BasePath, "part2.m4b")));
            Assert.False(File.Exists(Path.Join(source.BasePath, "part1.m4b")));

            // Legacy columns cleared: the source no longer owns any audio.
            var reloadedSource = await _audiobookRepository.GetByIdAsync(source.Id);
            Assert.Null(reloadedSource!.FilePath);
            Assert.Null(reloadedSource.FileSize);

            // History recorded on both records.
            var sourceHistory = await _historyRepository.GetByAudiobookIdAsync(source.Id);
            var targetHistory = await _historyRepository.GetByAudiobookIdAsync(target.Id);
            Assert.Contains(sourceHistory, h => h.EventType == "Files Transferred");
            Assert.Contains(targetHistory, h => h.EventType == "Files Received");
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "SubsetTransfer_KeepsSourceLegacyColumns")]
        public async Task TransferFiles_SubsetTransfer_KeepsSourceLegacyColumns()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target) = await CreateSourceAndTargetAsync();
            var file1 = await AddFileOnDiskAsync(source, "part1.m4b");
            var file2 = await AddFileOnDiskAsync(source, "part2.m4b");

            source.FilePath = file2.Path;
            source.FileSize = 5;
            await _audiobookRepository.UpdateAsync(source);

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest
                {
                    TargetAudiobookId = target.Id,
                    FileIds = [file1.Id]
                },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("transferred").GetInt32());

            var sourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id);
            var targetFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id);
            Assert.Single(sourceFiles);
            Assert.Equal(file2.Id, sourceFiles[0].Id);
            Assert.Single(targetFiles);

            // Source still holds audio — legacy columns must survive.
            var reloadedSource = await _audiobookRepository.GetByIdAsync(source.Id);
            Assert.Equal(file2.Path, reloadedSource!.FilePath);
            Assert.Equal(5, reloadedSource.FileSize);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "FileMissingOnDisk_ReassignsInPlace_WithWarning")]
        public async Task TransferFiles_FileMissingOnDisk_ReassignsInPlace_WithWarning()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target) = await CreateSourceAndTargetAsync();

            // DB row whose physical file does not exist.
            var ghostPath = Path.Join(source.BasePath, "ghost.m4b");
            var ghost = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(source)
                .WithPath(ghostPath)
                .Build());

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = target.Id },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(0, payload.GetProperty("physicallyMoved").GetInt32());
            Assert.Contains(
                payload.GetProperty("warnings").EnumerateArray(),
                w => w.GetString()!.Contains("missing on disk"));

            // Ownership reassigned; path unchanged (nothing to move).
            var targetFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id);
            var moved = Assert.Single(targetFiles);
            Assert.Equal(ghost.Id, moved.Id);
            Assert.Equal(ghostPath, moved.Path);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "TargetAlreadyOwnsRowAtPath_SkipsFile_NoDiskMove")]
        public async Task TransferFiles_TargetAlreadyOwnsRowAtPath_SkipsFile_NoDiskMove()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target) = await CreateSourceAndTargetAsync();
            var sourceFile = await AddFileOnDiskAsync(source, "part1.m4b");

            // The target already owns a row at the exact path the file would occupy —
            // a duplicate from a prior partial transfer. Reassigning would violate the
            // (AudiobookId, Path) unique key; moving first would orphan the source row.
            Directory.CreateDirectory(target.BasePath!);
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(target)
                .WithPath(Path.Join(target.BasePath, "part1.m4b"))
                .Build());

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = target.Id },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(0, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(0, payload.GetProperty("physicallyMoved").GetInt32());
            Assert.Contains(
                payload.GetProperty("warnings").EnumerateArray(),
                w => w.GetString()!.Contains("already exists under the target"));

            // File stayed with the source, on disk and in the DB.
            Assert.True(File.Exists(sourceFile.Path));
            var sourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id);
            Assert.Single(sourceFiles);

            // A skipped file means the source still holds audio — no history spam either.
            var sourceHistory = await _historyRepository.GetByAudiobookIdAsync(source.Id);
            Assert.Contains(sourceHistory, h => h.EventType == "Files Transferred" && h.Message!.Contains("0 file(s)"));
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "DestinationFileOnDiskWithoutRow_LeavesInPlace_ButReassigns")]
        public async Task TransferFiles_DestinationFileOnDiskWithoutRow_LeavesInPlace_ButReassigns()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target) = await CreateSourceAndTargetAsync();
            var sourceFile = await AddFileOnDiskAsync(source, "part1.m4b");

            // A same-named file already sits in the target folder, but the target has
            // no DB row for it — ownership can still transfer, the disk move cannot.
            Directory.CreateDirectory(target.BasePath!);
            await File.WriteAllTextAsync(Path.Join(target.BasePath, "part1.m4b"), "other audio");

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest { TargetAudiobookId = target.Id },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(0, payload.GetProperty("physicallyMoved").GetInt32());
            Assert.Contains(
                payload.GetProperty("warnings").EnumerateArray(),
                w => w.GetString()!.Contains("file left in place"));

            // Ownership moved; the file's path still points at the source folder.
            var targetFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id);
            var moved = Assert.Single(targetFiles);
            Assert.Equal(sourceFile.Path, moved.Path);
            Assert.True(File.Exists(sourceFile.Path));
        }
    }
}
