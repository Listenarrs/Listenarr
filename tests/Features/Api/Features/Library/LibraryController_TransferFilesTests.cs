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
using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

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

        private static string[] Warnings(JsonElement payload) =>
            payload.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();

        /// <summary>
        /// Both records live under one authorized library root so file identity resolves
        /// from the persisted root semantics and organize may move files between them.
        /// </summary>
        private async Task<(Audiobook source, Audiobook target, string root)> CreateSourceAndTargetAsync(
            bool targetHasFolder = true)
        {
            var root = FileService.GetTempDirectory("transfer-root");
            await AddAuthorizedRootAsync(root);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(root)
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithFileNamingPattern("{Title}")
                .Build());

            var source = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Arcanum Unbounded")
                .WithAuthor("Brandon Sanderson")
                .WithBasePath(Path.Join(root, "Brandon Sanderson", "Arcanum Unbounded"))
                .Build());
            Directory.CreateDirectory(source.BasePath!);

            var targetBuilder = new AudiobookBuilder()
                .WithTitle("Warbreaker")
                .WithAuthor("Brandon Sanderson");
            if (targetHasFolder)
            {
                targetBuilder.WithBasePath(Path.Join(root, "Brandon Sanderson", "Warbreaker"));
            }
            var target = await _audiobookRepository.AddAsync(targetBuilder.Build());

            return (source, target, root);
        }

        /// <summary>
        /// Seeds a row through the ownership claim contract with the identity the
        /// production resolver computes, exactly like a scan or import would.
        /// </summary>
        private async Task<AudiobookFile> ClaimFileAsync(Audiobook owner, string fileName, bool onDisk = true)
        {
            var path = Path.Join(owner.BasePath, fileName);
            Directory.CreateDirectory(owner.BasePath!);
            if (onDisk)
            {
                await File.WriteAllTextAsync(path, "audio");
            }

            var identity = await _provider
                .GetRequiredService<IAudiobookFilePathIdentityResolver>()
                .ResolveAsync(owner, path);
            Assert.Equal(PathIdentityState.Valid, identity.State);
            var file = AudiobookFile.CreateUnresolved(path);
            file.AudiobookId = owner.Id;
            file.Format = "m4b";
            file.ApplyPathIdentity(path, identity);
            var claim = await _audiobookFileRepository.ClaimAsync(file);
            Assert.Equal(AudiobookFileClaimOutcome.Created, claim.Outcome);
            return claim.File!;
        }

        private async Task<LibraryController.TransferFilesRequest> AllFilesTo(Audiobook target) =>
            await Task.FromResult(new LibraryController.TransferFilesRequest { TargetAudiobookId = target.Id });

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "ReturnsBadRequest_WhenTargetIsSource")]
        public async Task TransferFiles_ReturnsBadRequest_WhenTargetIsSource()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, _, _) = await CreateSourceAndTargetAsync();

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
            var (source, _, _) = await CreateSourceAndTargetAsync();

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
            var (source, target, _) = await CreateSourceAndTargetAsync();
            var file = await ClaimFileAsync(source, "part1.m4b");

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
            var sourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id);
            Assert.Single(sourceFiles);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "ReturnsBadRequest_WhenSourceHasNoFiles")]
        public async Task TransferFiles_ReturnsBadRequest_WhenSourceHasNoFiles()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target, _) = await CreateSourceAndTargetAsync();

            var result = await controller.TransferFiles(
                source.Id,
                await AllFilesTo(target),
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
            var (source, target, _) = await CreateSourceAndTargetAsync();
            var file1 = await ClaimFileAsync(source, "part1.m4b");
            var file2 = await ClaimFileAsync(source, "part2.m4b");

            // Legacy single-file columns describe the audio the source is about to lose.
            var trackedSource = (await _audiobookRepository.GetByIdAsync(source.Id))!;
            trackedSource.FilePath = file1.Path;
            trackedSource.FileSize = 5;
            await _audiobookRepository.UpdateAsync(trackedSource);

            var result = await controller.TransferFiles(
                source.Id,
                await AllFilesTo(target),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(2, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(2, payload.GetProperty("physicallyMoved").GetInt32());
            Assert.Empty(Warnings(payload));

            // Rows re-homed (same ids), paths inside the destination folder, identity re-proven there.
            var sourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id);
            var targetFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id);
            Assert.Empty(sourceFiles);
            Assert.Equal(
                new[] { file1.Id, file2.Id }.Order(),
                targetFiles.Select(f => f.Id).Order());
            Assert.All(targetFiles, f =>
            {
                Assert.StartsWith(target.BasePath!, f.Path!);
                Assert.Equal(PathIdentityState.Valid, f.PathIdentityState);
                Assert.False(string.IsNullOrWhiteSpace(f.PathOwnershipKey));
            });

            // Physically relocated.
            Assert.True(File.Exists(Path.Join(target.BasePath, "part1.m4b")));
            Assert.True(File.Exists(Path.Join(target.BasePath, "part2.m4b")));
            Assert.False(File.Exists(Path.Join(source.BasePath, "part1.m4b")));
            Assert.False(File.Exists(Path.Join(source.BasePath, "part2.m4b")));

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
            var (source, target, _) = await CreateSourceAndTargetAsync();
            var file1 = await ClaimFileAsync(source, "part1.m4b");
            var file2 = await ClaimFileAsync(source, "part2.m4b");

            var trackedSource = (await _audiobookRepository.GetByIdAsync(source.Id))!;
            trackedSource.FilePath = file2.Path;
            trackedSource.FileSize = 5;
            await _audiobookRepository.UpdateAsync(trackedSource);

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
            Assert.Equal(1, payload.GetProperty("physicallyMoved").GetInt32());

            var sourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id);
            var targetFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id);
            Assert.Equal(file2.Id, Assert.Single(sourceFiles).Id);
            var moved = Assert.Single(targetFiles);
            Assert.Equal(file1.Id, moved.Id);
            Assert.Equal(Path.Join(target.BasePath, "part1.m4b"), moved.Path);
            Assert.True(File.Exists(file2.Path!));

            // Source still holds audio — legacy columns must survive.
            var reloadedSource = await _audiobookRepository.GetByIdAsync(source.Id);
            Assert.Equal(file2.Path, reloadedSource!.FilePath);
            Assert.Equal(5, reloadedSource.FileSize);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "TargetWithoutFolder_LandsInCanonicalFolder_NotTheLibraryRoot")]
        public async Task TransferFiles_TargetWithoutFolder_LandsInCanonicalFolder_NotTheLibraryRoot()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target, root) = await CreateSourceAndTargetAsync(targetHasFolder: false);
            var file = await ClaimFileAsync(source, "part1.m4b");

            var result = await controller.TransferFiles(
                source.Id,
                await AllFilesTo(target),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(1, payload.GetProperty("physicallyMoved").GetInt32());

            var expectedFolder = Path.Join(root, "Brandon Sanderson", "Warbreaker");
            var moved = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id));
            Assert.Equal(file.Id, moved.Id);
            Assert.Equal(Path.Join(expectedFolder, "part1.m4b"), moved.Path);
            Assert.True(File.Exists(moved.Path!));
            Assert.False(File.Exists(Path.Join(root, "part1.m4b")));

            var reloadedTarget = await _audiobookRepository.GetByIdAsync(target.Id);
            Assert.Equal(expectedFolder, reloadedTarget!.BasePath);
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "FileMissingOnDisk_TransfersOwnershipInPlace_WithWarning")]
        public async Task TransferFiles_FileMissingOnDisk_TransfersOwnershipInPlace_WithWarning()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target, _) = await CreateSourceAndTargetAsync();
            var ghost = await ClaimFileAsync(source, "ghost.m4b", onDisk: false);

            var result = await controller.TransferFiles(
                source.Id,
                await AllFilesTo(target),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(0, payload.GetProperty("physicallyMoved").GetInt32());
            Assert.Contains(Warnings(payload), w => w.Contains("missing on disk"));

            // Ownership re-homed with the identity intact; the path is unchanged.
            var moved = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id));
            Assert.Equal(ghost.Id, moved.Id);
            Assert.Equal(ghost.Path, moved.Path);
            Assert.Equal(ghost.PathOwnershipKey, moved.PathOwnershipKey);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id));
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "DestinationNameTaken_TransfersOwnership_LeavesFileInPlace")]
        public async Task TransferFiles_DestinationNameTaken_TransfersOwnership_LeavesFileInPlace()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target, _) = await CreateSourceAndTargetAsync();
            var sourceFile = await ClaimFileAsync(source, "part1.m4b");

            // A same-named file already sits in the destination folder: the row still
            // changes owner, but nothing on disk is overwritten.
            Directory.CreateDirectory(target.BasePath!);
            await File.WriteAllTextAsync(Path.Join(target.BasePath, "part1.m4b"), "other audio");

            var result = await controller.TransferFiles(
                source.Id,
                await AllFilesTo(target),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("transferred").GetInt32());
            Assert.Equal(0, payload.GetProperty("physicallyMoved").GetInt32());
            Assert.Contains(Warnings(payload), w => w.Contains("already exists"));

            var moved = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id));
            Assert.Equal(sourceFile.Path, moved.Path);
            Assert.True(File.Exists(sourceFile.Path!));
            Assert.Equal("other audio", await File.ReadAllTextAsync(Path.Join(target.BasePath, "part1.m4b")));
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "UnprovenIdentity_IsRefused")]
        public async Task TransferFiles_UnprovenIdentity_IsRefused()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target, _) = await CreateSourceAndTargetAsync();

            // A legacy row that identity reconciliation has not proven yet cannot be
            // carried into another owner.
            await using var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            var legacy = AudiobookFile.CreateUnresolved(Path.Join(source.BasePath, "legacy.m4b"));
            legacy.AudiobookId = source.Id;
            db.AudiobookFiles.Add(legacy);
            await db.SaveChangesAsync();

            var result = await controller.TransferFiles(
                source.Id,
                await AllFilesTo(target),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(0, payload.GetProperty("transferred").GetInt32());
            Assert.Contains(Warnings(payload), w => w.Contains("not been proven"));
            Assert.Equal(legacy.Id, Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id)).Id);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id));
            Assert.DoesNotContain(
                await _historyRepository.GetByAudiobookIdAsync(source.Id),
                h => h.EventType == "Files Transferred");
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "StaleIdentity_IsRefused")]
        public async Task TransferFiles_StaleIdentity_IsRefused()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target, root) = await CreateSourceAndTargetAsync();

            // A row whose persisted ownership key was computed under other semantics no
            // longer describes its path; reconciliation must repair it before a transfer.
            var path = Path.Join(source.BasePath, "stale.m4b");
            Directory.CreateDirectory(source.BasePath!);
            await File.WriteAllTextAsync(path, "audio");
            var current = await _provider
                .GetRequiredService<IAudiobookFilePathIdentityResolver>()
                .ResolveAsync(source, path);
            Assert.Equal(PathIdentityState.Valid, current.State);
            var flipped = current.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
            var stale = AudiobookFilePathIdentity.CreateValid(
                path,
                new FileSystemPathSemantics(current.Syntax, flipped),
                current.RequestedMode,
                root);
            Assert.NotEqual(current.OwnershipKey, stale.OwnershipKey);
            var file = AudiobookFile.CreateUnresolved(path);
            file.AudiobookId = source.Id;
            file.ApplyPathIdentity(path, stale);
            var claim = await _audiobookFileRepository.ClaimAsync(file);
            Assert.Equal(AudiobookFileClaimOutcome.Created, claim.Outcome);

            var result = await controller.TransferFiles(
                source.Id,
                await AllFilesTo(target),
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(0, payload.GetProperty("transferred").GetInt32());
            Assert.Contains(Warnings(payload), w => w.Contains("no longer matches"));
            var kept = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id));
            Assert.Equal(stale.OwnershipKey, kept.PathOwnershipKey);
            Assert.True(File.Exists(path));
        }

        [Fact]
        [Trait("Method", "TransferFiles")]
        [Trait("Scenario", "UnresolvedRowWithSameLookupKey_BlocksTransfer")]
        public async Task TransferFiles_UnresolvedRowWithSameLookupKey_BlocksTransfer()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, target, _) = await CreateSourceAndTargetAsync();
            var file = await ClaimFileAsync(source, "part1.m4b");

            // The same ownership gate a claim goes through: an unresolved legacy row for
            // the same path blocks the destination from taking ownership.
            await using var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            var legacy = AudiobookFile.CreateUnresolved(file.Path);
            legacy.AudiobookId = target.Id;
            legacy.MarkPathIdentityUnavailable(file.Path, "legacy row awaiting operator resolution");
            legacy.PreparePathIdentityReconciliation("legacy row awaiting operator resolution");
            db.AudiobookFiles.Add(legacy);
            await db.SaveChangesAsync();
            db.Entry(legacy)
                .Property(nameof(AudiobookFile.PathIdentityLookupKey))
                .CurrentValue = file.PathIdentityLookupKey;
            db.Entry(legacy)
                .Property(nameof(AudiobookFile.CanonicalPath))
                .CurrentValue = file.CanonicalPath;
            db.Entry(legacy)
                .Property(nameof(AudiobookFile.PathSyntax))
                .CurrentValue = file.PathSyntax;
            await db.SaveChangesAsync();

            var result = await controller.TransferFiles(
                source.Id,
                new LibraryController.TransferFilesRequest
                {
                    TargetAudiobookId = target.Id,
                    FileIds = [file.Id]
                },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(0, payload.GetProperty("transferred").GetInt32());
            Assert.Contains(Warnings(payload), w => w.Contains("was not transferred"));
            Assert.Equal(file.Id, Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id)).Id);
            Assert.True(File.Exists(file.Path!));
        }
    }
}
