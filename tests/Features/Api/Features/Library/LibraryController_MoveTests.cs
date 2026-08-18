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
using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_MoveTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_MoveTests : BaseTests
    {
        private static Mock<IMoveQueueService> CreateMoveQueueMock(
            MockBehavior behavior = MockBehavior.Loose)
        {
            var moveQueue = new Mock<IMoveQueueService>(behavior);
            moveQueue.Setup(service => service.GetActiveJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MoveJob>());
            moveQueue.Setup(service => service.GetRecoveryStateForAudiobookAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(MoveRecoveryState.None);
            return moveQueue;
        }

        private static Mock<IMoveQueueService> CreateStrictMoveQueueMock() =>
            CreateMoveQueueMock(MockBehavior.Strict);

        [Fact]
        public async Task GetMoveJobStatus_ReturnsPublicContractWithoutWorkerInternals()
        {
            var jobId = Guid.NewGuid();
            var job = new MoveJob
            {
                Id = jobId,
                AudiobookId = 42,
                RequestedPath = "/library/Author/Title",
                SourcePath = "/incoming/Author/Title",
                Status = MoveJobStatus.Failed,
                Phase = MoveJobPhase.CleaningSource,
                Error = "C:\\machine\\private\\recovery failed for worker secret",
                FailureKind = MoveFailureKind.Verification,
                AttemptCount = 3,
                EnqueuedAt = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 7, 28, 12, 5, 0, DateTimeKind.Utc),
                NextAttemptAt = new DateTime(2026, 7, 28, 12, 10, 0, DateTimeKind.Utc),
                ActiveDeduplicationKey = "dedupe-secret",
                IdentityKeyVersion = MoveManifestIdentity.Version,
                LeaseOwner = "machine:123:secret",
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                LeaseGeneration = 9,
                SourceIdentityBoundary = "/incoming",
                TargetIdentityBoundary = "/library",
                SourceCleanupBoundary = "/incoming/Author",
                RelocationId = Guid.NewGuid(),
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "private-file.m4b",
                        Sha256 = "private-hash"
                    }
                ],
                CreatedDirectories =
                [
                    new MoveJobCreatedDirectory
                    {
                        Path = "/library/private-recovery"
                    }
                ]
            };
            var moveQueue = CreateStrictMoveQueueMock();
            moveQueue.Setup(service => service.GetJobAsync(
                    jobId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            Init(services => services.WithSingleton(moveQueue.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .GetMoveJobStatus(jobId.ToString("D"), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.IsNotType<MoveJob>(ok.Value);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains(nameof(MoveJob.Id), json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(MoveJob.AudiobookId), json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(MoveJob.RequestedPath), json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(MoveJob.Status), json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(MoveJob.Phase), json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("The moved files could not be verified", json, StringComparison.Ordinal);
            foreach (var forbidden in new[]
            {
                nameof(MoveJob.ActiveDeduplicationKey),
                nameof(MoveJob.IdentityKeyVersion),
                nameof(MoveJob.LeaseOwner),
                nameof(MoveJob.LeaseExpiresAt),
                nameof(MoveJob.LeaseGeneration),
                nameof(MoveJob.SourcePath),
                nameof(MoveJob.SourceIdentityBoundary),
                nameof(MoveJob.TargetIdentityBoundary),
                nameof(MoveJob.SourceCleanupBoundary),
                nameof(MoveJob.RelocationId),
                nameof(MoveJob.Relocation),
                nameof(MoveJob.Entries),
                nameof(MoveJob.CreatedDirectories),
                nameof(MoveJob.ScanHandoff),
                "worker secret",
                "private-hash",
                "private-recovery"
            })
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task GetActiveMoveJobs_ReturnsByteWeightedProgressWithoutWorkerInternals()
        {
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 42,
                RequestedPath = "/library/Author/Title",
                SourcePath = "/incoming/Author/Title",
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                LeaseOwner = "worker-secret",
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "part-1.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 100,
                        CopyState = MoveJobEntryCopyState.Verified
                    },
                    new MoveJobEntry
                    {
                        RelativePath = "part-2.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 300,
                        CopyState = MoveJobEntryCopyState.Pending
                    }
                ]
            };
            var moveQueue = CreateStrictMoveQueueMock();
            moveQueue.Setup(service => service.GetActiveJobsAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([job]);
            Init(services => services.WithSingleton(moveQueue.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .GetActiveMoveJobs(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var projected = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal((int)MoveJobStatus.Running, projected.GetProperty("Status").GetInt32());
            Assert.Equal((int)MoveJobPhase.Copying, projected.GetProperty("Phase").GetInt32());
            Assert.Equal(21.25, projected.GetProperty("Progress").GetDouble());
            Assert.DoesNotContain(nameof(MoveJob.Entries), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("worker-secret", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetMoveJobStatus_NeedsAttentionVerification_ReportsOperatorRepairNotRetryable()
        {
            var jobId = Guid.NewGuid();
            var job = new MoveJob
            {
                Id = jobId,
                AudiobookId = 42,
                RequestedPath = "/library/Author/Title",
                SourcePath = "/incoming/Author/Title",
                Status = MoveJobStatus.NeedsAttention,
                Phase = MoveJobPhase.CleaningSource,
                Error = "Persisted generation changed.",
                FailureKind = MoveFailureKind.Verification,
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "book.m4b",
                        EntryType = MoveJobEntryType.File,
                        CopyState = MoveJobEntryCopyState.Verified,
                        CleanupState = MoveJobEntryCleanupState.DeleteAuthorized
                    }
                ]
            };
            var moveQueue = CreateStrictMoveQueueMock();
            moveQueue.Setup(service => service.GetJobAsync(
                    jobId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            Init(services => services.WithSingleton(moveQueue.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .GetMoveJobStatus(jobId.ToString("D"), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains(
                "\"RecoveryDisposition\":\"OperatorRepairRequired\"",
                json,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"CanRetry\":false", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ReturnsConflict_WhenTrackedSourceDoesNotExist")]
        public async Task MoveAudiobook_ReturnsConflict_WhenTrackedSourceDoesNotExist()
        {
            // Given
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var missingSource = Path.Join(FileService.GetTempPath(), "nonexistent");
            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(missingSource)
                .Build());
            await AddTrackedFileAsync(
                ab,
                missingSource,
                createFile: false);

            var request = new LibraryController.MoveRequest { DestinationPath = Path.Join(outputPath, "target") };

            // When
            var result = await controller.EnqueueMove(ab.Id, request);

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Contains("move_source_unverified", conflict.Value?.ToString() ?? string.Empty);
            Assert.Contains("missing from disk", conflict.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        public async Task MoveAudiobook_UnresolvedPublishedMove_BlocksBeforeFreshManifestValidation()
        {
            var manifestService = new Mock<IMoveSourceManifestService>(MockBehavior.Strict);
            Init(services => services.WithSingleton(manifestService.Object));
            var missingSource = Path.Join(
                FileService.GetTempPath(),
                $"listenarr-interrupted-move-source-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Interrupted Physical Move")
                .WithBasePath(missingSource)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                missingSource,
                createFile: false);
            var jobId = Guid.NewGuid();
            var target = Path.Join(
                FileService.GetTempPath(),
                $"listenarr-interrupted-move-target-{Guid.NewGuid():N}");
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = audiobook.Id,
                    SourcePath = missingSource,
                    RequestedPath = target,
                    Status = MoveJobStatus.Failed,
                    Phase = MoveJobPhase.Published,
                    FailureKind = MoveFailureKind.Unknown,
                    Error = "Interrupted after published source cleanup",
                    Entries =
                    [
                        new MoveJobEntry
                        {
                            RelativePath = "book.m4b",
                            EntryType = MoveJobEntryType.File,
                            Length = 5,
                            LastWriteTimeUtc = DateTime.UtcNow,
                            Sha256 = new string('A', 64),
                            CopyState = MoveJobEntryCopyState.Verified,
                            CleanupState = MoveJobEntryCleanupState.Deleted
                        }
                    ]
                });
                await db.SaveChangesAsync();
            }

            var result = await _provider.GetRequiredService<LibraryController>()
                .EnqueueMove(
                    audiobook.Id,
                    new LibraryController.MoveRequest
                    {
                        DestinationPath = target,
                        SourcePath = missingSource,
                        MoveFiles = true,
                        DeleteEmptySource = true
                    });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            var payload = JsonSerializer.Serialize(conflict.Value);
            Assert.Contains("move_recovery_required", payload, StringComparison.Ordinal);
            Assert.Contains(jobId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("move_source_unverified", payload, StringComparison.Ordinal);
            manifestService.Verify(service => service.BuildAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<CancellationToken>()), Times.Never);
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Single(await verification.MoveJobs
                .Where(job => job.AudiobookId == audiobook.Id)
                .ToListAsync());
        }

        [WindowsFact]
        public async Task MoveAudiobook_PersistedUnixOutputRoot_DoesNotAuthorizeCurrentWindowsDrive()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var controller = _provider.GetRequiredService<LibraryController>();

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath("/")
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-foreign-root-source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Foreign Configured Root")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);
            var targetPath = Path.Join(
                Path.GetPathRoot(Environment.CurrentDirectory)!,
                "listenarr-foreign-configured-root",
                Guid.NewGuid().ToString("N"));

            var result = await controller.EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    DeleteEmptySource = false
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(
                "configured root folder or output path",
                badRequest.Value?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "EnqueuesJob_WhenSourceExists")]
        public async Task MoveAudiobook_EnqueuesJob_WhenSourceExists()
        {
            // Given
            var mockMoveQueue = CreateMoveQueueMock();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedId);

            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(ab, sourcePath);

            var target = Path.Join(outputPath, "listenarr-move-dst");
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = target,
                DeleteEmptySource = false
            };

            // When
            var result = await controller.EnqueueMove(ab.Id, request);

            // Then: expect 202 Accepted
            var acceptedObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, acceptedObj.StatusCode);
            Assert.NotNull(acceptedObj.Value);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == ab.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(target)
                    && command.SourcePath == ab.BasePath
                    && !command.DeleteEmptySource
                    && command.SourceCleanupBoundary == null),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MoveAudiobook_BroadBasePath_QueuesOnlyTrackedBookManifest()
        {
            MoveEnqueueCommand? captured = null;
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<MoveEnqueueCommand, CancellationToken>((command, _) =>
                    captured = command)
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(moveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(outputPath)
                    .Build());
            var authorPath = FileService.GetTempDirectory("listenarr-move-author");
            var requestedBook = Path.Join(authorPath, "Book One");
            var siblingBook = Path.Join(authorPath, "Book Two");
            Directory.CreateDirectory(requestedBook);
            Directory.CreateDirectory(siblingBook);
            _ = await FileService.GetFileAsync(
                siblingBook,
                "Book Two.m4b",
                "foreign");
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Book One")
                    .WithBasePath(authorPath)
                    .Build());
            await AddTrackedFileAsync(
                audiobook,
                requestedBook,
                "Book One.m4b");

            var result = await _provider.GetRequiredService<LibraryController>()
                .EnqueueMove(
                    audiobook.Id,
                    new LibraryController.MoveRequest
                    {
                        SourcePath = authorPath,
                        DestinationPath = Path.Join(outputPath, "Book One"),
                        MoveFiles = true
                    });

            Assert.IsType<AcceptedResult>(result);
            Assert.NotNull(captured);
            Assert.Equal(requestedBook, captured!.SourcePath);
            var file = Assert.Single(
                captured.SourceEntries,
                entry => entry.EntryType == MoveJobEntryType.File);
            Assert.Equal("Book One.m4b", file.RelativePath);
            Assert.DoesNotContain(captured.SourceEntries, entry =>
                entry.RelativePath.Contains("Book Two", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MoveAudiobook_SharedFlatFolder_QueuesOnlyTrackedFile()
        {
            MoveEnqueueCommand? captured = null;
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<MoveEnqueueCommand, CancellationToken>((command, _) =>
                    captured = command)
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(moveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(outputPath)
                    .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-shared-flat");
            _ = await FileService.GetFileAsync(
                sourcePath,
                "Book Two.m4b",
                "foreign");
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Book One")
                    .WithBasePath(sourcePath)
                    .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                "Book One.m4b");

            var result = await _provider.GetRequiredService<LibraryController>()
                .EnqueueMove(
                    audiobook.Id,
                    new LibraryController.MoveRequest
                    {
                        DestinationPath = Path.Join(outputPath, "Book One"),
                        MoveFiles = true
                    });

            Assert.IsType<AcceptedResult>(result);
            Assert.NotNull(captured);
            Assert.Equal(sourcePath, captured!.SourcePath);
            var file = Assert.Single(captured.SourceEntries);
            Assert.Equal("Book One.m4b", file.RelativePath);
        }

        [Fact]
        public async Task MoveAudiobook_NoTrackedFiles_RequiresRepair()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(outputPath)
                    .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-untracked");
            _ = await FileService.GetFileAsync(sourcePath, "Untracked.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder()
                    .WithTitle("Untracked")
                    .WithBasePath(sourcePath)
                    .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .EnqueueMove(
                    audiobook.Id,
                    new LibraryController.MoveRequest
                    {
                        DestinationPath = Path.Join(outputPath, "Untracked"),
                        MoveFiles = true
                    });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("move_source_unverified", conflict.Value?.ToString() ?? string.Empty);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MoveAudiobook_InvalidSourcePath_IsBoundToSourceField()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var currentSource = FileService.GetTempDirectory("listenarr-current-source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Invalid Source Path")
                .WithBasePath(currentSource)
                .Build());
            await AddTrackedFileAsync(audiobook, currentSource);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(outputPath, "target"),
                    SourcePath = "relative/source",
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            Assert.Equal("source_path_invalid", root.GetProperty("code").GetString());
            Assert.Equal("sourcePath", root.GetProperty("field").GetString());
            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                root.GetProperty("resolvedDestination").ValueKind);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsStalePhysicalSourcePath")]
        public async Task MoveAudiobook_PhysicalMoveRejectsStaleExistingSourcePath()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var currentSource = FileService.GetTempDirectory("listenarr-current-source");
            var staleSource = FileService.GetTempDirectory("listenarr-stale-source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Stale Physical Source")
                .WithBasePath(currentSource)
                .Build());
            await AddTrackedFileAsync(audiobook, currentSource);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(outputPath, "target"),
                    SourcePath = staleSource,
                    MoveFiles = true
                });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Contains("source_path_changed", conflict.Value?.ToString() ?? string.Empty);
            moveQueue.Verify(
                service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task MoveAudiobook_SourceChangesAfterPreflight_RejectsBeforeQueuePersistence()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            var updatedSource = FileService.GetTempDirectory("listenarr-updated-source");
            using var coordinator = new BeforeExecuteAudiobookCoordinator(async () =>
            {
                using var scope = _provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var audiobook = await db.Audiobooks.SingleAsync();
                audiobook.BasePath = updatedSource;
                await db.SaveChangesAsync();
            });
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IAudiobookOperationCoordinator>(coordinator));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var originalSource = FileService.GetTempDirectory("listenarr-original-source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Enqueue Fence")
                .WithBasePath(originalSource)
                .Build());
            await AddTrackedFileAsync(audiobook, originalSource);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(outputPath, "target"),
                    SourcePath = originalSource,
                    MoveFiles = true
                });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("source_path_changed", conflict.Value?.ToString() ?? string.Empty);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
            using var verificationScope = _provider.CreateScope();
            var verification = verificationScope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            Assert.Equal(
                FileUtils.NormalizeStoredPath(updatedSource),
                (await verification.Audiobooks.SingleAsync()).BasePath);
        }

        [Fact]
        public async Task MoveAudiobook_UnavailableTargetAncestor_ReturnsStructuredDestinationError()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            var validationCalls = 0;
            fileSystem.Setup(system => system.TryValidateMutationTarget(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string?>>(),
                    out It.Ref<string>.IsAny,
                    out It.Ref<string>.IsAny))
                .Returns((
                    string targetPath,
                    IEnumerable<string?> _,
                    out string normalizedPath,
                    out string reason) =>
                {
                    normalizedPath = targetPath;
                    var accepted = Interlocked.Increment(ref validationCalls) == 1;
                    reason = accepted ? string.Empty : "simulated unavailable target ancestor";
                    return accepted;
                });
            fileSystem.Setup(system => system.DirectoryExists(It.IsAny<string>())).Returns(true);
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFileSystem>(fileSystem.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-ancestor-target-source");
            var targetPath = Path.Join(outputPath, "Author", "Title");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Ancestor Target")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            Assert.Equal("destination_parent_unavailable", root.GetProperty("code").GetString());
            Assert.Equal("destinationPath", root.GetProperty("field").GetString());
            Assert.Equal(targetPath, root.GetProperty("resolvedDestination").GetString());
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsCustomPhysicalDestinationOutsideConfiguredRoots")]
        public async Task MoveAudiobook_MoveFilesTrue_RejectsCustomDestinationOutsideConfiguredRoots()
        {
            var mockMoveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var configuredOutputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredOutputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Blocked Custom Physical Move")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);
            var customRoot = FileService.GetTempDirectory("listenarr-custom-destination");
            var target = Path.Join(customRoot, "Author", "Title", "test");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = target,
                    SourcePath = sourcePath,
                    MoveFiles = true,
                    DeleteEmptySource = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            using var payloadDocument = System.Text.Json.JsonDocument.Parse(payload);
            var payloadRoot = payloadDocument.RootElement;
            Assert.Equal(
                "destination_path_outside_roots",
                payloadRoot.GetProperty("code").GetString());
            Assert.Equal("destinationPath", payloadRoot.GetProperty("field").GetString());
            Assert.Equal(
                FileUtils.NormalizeStoredPath(target),
                payloadRoot.GetProperty("resolvedDestination").GetString());
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MoveAudiobook_CustomSiblingMove_PersistsCommonSeriesCleanupBoundary()
        {
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(moveQueue.Object));
            var configuredOutputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredOutputPath)
                .Build());

            var series = Path.Join(
                configuredOutputPath,
                "Matt Dinniman",
                "Dungeon Crawler Carl");
            var source = Path.Join(series, "A Parade of Horribles (20262)", "test");
            Directory.CreateDirectory(source);
            var target = Path.Join(series, "A Parade of Horribles (2026)", "test");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Parade of Horribles")
                .WithBasePath(source)
                .Build());
            await AddTrackedFileAsync(audiobook, source);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    SourcePath = source,
                    DestinationPath = target,
                    MoveFiles = true,
                    DeleteEmptySource = true
                });

            Assert.IsType<AcceptedResult>(result);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == audiobook.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(target)
                    && command.SourcePath == source
                    && command.DeleteEmptySource
                    && command.SourceCleanupBoundary == series),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MoveAudiobook_RelocationConflictReturnsConflict()
        {
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MoveRelocationConflictException(
                    "Move target overlaps an active root folder relocation boundary."));
            Init(services => services.WithSingleton(moveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(outputPath, "listenarr-move-dst")
                });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Contains("active root folder relocation", conflict.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "UpdatesBasePath_WhenMoveFilesFalse")]
        public async Task MoveAudiobook_UpdatesBasePath_WhenMoveFilesFalse()
        {
            // Given
            var mockMoveQueue = new Mock<IMoveQueueService>();

            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(Path.Join(FileService.GetTempPath(), "listenarr-move-src"))
                .Build());

            var target = Path.Join(outputPath, "listenarr-move-dst");
            var request = new LibraryController.MoveRequest { DestinationPath = target, MoveFiles = false };

            // When
            var result = await controller.EnqueueMove(ab.Id, request);

            // Then: expect 200 OK
            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);
            Assert.NotNull(okObj.Value);

            // Ensure DB was updated
            var updated = await _audiobookRepository.GetByIdAsync(ab.Id);
            Assert.Equal(FileUtils.NormalizeStoredPath(target), updated!.BasePath);

            // Ensure move queue was NOT enqueued
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("source")]
        [InlineData("target")]
        public async Task MoveAudiobook_MetadataOnlyRejectsActiveRelocationBoundary(string protectedEndpoint)
        {
            var moveQueue = new Mock<IMoveQueueService>();
            var relocation = new Mock<IRootFolderRelocationService>();
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton(relocation.Object));

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(outputPath, "listenarr-move-dst");
            var protectedPath = protectedEndpoint == "source"
                ? sourcePath
                : FileUtils.NormalizeStoredPath(targetPath);
            relocation.Setup(service => service.IsBoundaryProtectedAsync(
                    protectedPath,
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath, MoveFiles = false });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("active root folder relocation", conflict.Value?.ToString() ?? string.Empty);
            Assert.Equal(sourcePath, (await _audiobookRepository.GetByIdAsync(audiobook.Id))!.BasePath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsStaleMetadataOnlySourcePath")]
        public async Task MoveAudiobook_MetadataOnlyRejectsStaleSourcePath()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services.WithSingleton(moveQueue.Object));

            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutOutputPath()
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var sourcePath = Path.Join(rootPath, "Author", "Title");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Stale Source")
                .WithBasePath(sourcePath)
                .Build());
            var staleSourcePath = Path.Join(rootPath, "Author", "OldTitle");
            var targetPath = Path.Join(rootPath, "Author", "NewTitle");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    MoveFiles = false,
                    SourcePath = staleSourcePath
                });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("source path changed", conflict.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourcePath, (await _audiobookRepository.GetByIdAsync(audiobook.Id))!.BasePath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ResolvesDestinationAfterAcquiringMutationCoordinator")]
        public async Task MoveAudiobook_MetadataOnlyResolvesDestinationInsideMutationCoordinator()
        {
            var coordinator = new FilesystemMutationCoordinator();
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFilesystemMutationCoordinator>(coordinator));

            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutOutputPath()
                .Build());
            var root = new RootFolderBuilder()
                .WithName("Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build();
            await _rootFolderRepository.AddAsync(root);

            var sourcePath = Path.Join(rootPath, "Author", "Title");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Coordinator")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(rootPath, "Author", "UpdatedTitle");

            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var moveTask = _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    MoveFiles = false
                });
            await Task.Delay(50);
            Assert.False(moveTask.IsCompleted);

            await _rootFolderRepository.RemoveAsync(root.Id);

            releaseLock.SetResult();
            await lockTask;
            var result = await moveTask;
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("configured root folder or output path", badRequest.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourcePath, (await _audiobookRepository.GetByIdAsync(audiobook.Id))!.BasePath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MoveAudiobook_PhysicalPreflightWaitsForFilesystemMutationCoordinator()
        {
            var coordinator = new FilesystemMutationCoordinator();
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFilesystemMutationCoordinator>(coordinator));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            await FileService.GetFileAsync(sourcePath, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Physical Gate")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);
            var targetPath = Path.Join(outputPath, "physical-target");
            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var moveTask = _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    SourcePath = sourcePath,
                    MoveFiles = true
                });
            await Task.Delay(50);
            Assert.False(moveTask.IsCompleted);

            releaseLock.SetResult();
            await lockTask;
            Assert.IsType<AcceptedResult>(await moveTask);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MoveAudiobook_ActiveMoveRejectsBeforeWaitingForFilesystemMutationCoordinator()
        {
            var coordinator = new FilesystemMutationCoordinator();
            var activeJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 42,
                SourcePath = "C:\\source",
                RequestedPath = "C:\\target",
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                EnqueuedAt = DateTime.UtcNow
            };
            var moveQueue = CreateMoveQueueMock(MockBehavior.Strict);
            moveQueue.Setup(service => service.GetRecoveryStateForAudiobookAsync(
                    activeJob.AudiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MoveRecoveryState(
                    MoveRecoveryDisposition.InProgress,
                    activeJob.Id,
                    activeJob.Status,
                    activeJob.Phase,
                    activeJob.RequestedPath,
                    activeJob.Error,
                    [activeJob.Id]));
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFilesystemMutationCoordinator>(coordinator));
            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var moveTask = _provider.GetRequiredService<LibraryController>().EnqueueMove(
                activeJob.AudiobookId,
                new LibraryController.MoveRequest
                {
                    DestinationPath = "C:\\target",
                    SourcePath = "C:\\source",
                    MoveFiles = true
                });

            var completed = await Task.WhenAny(moveTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(moveTask, completed);
            var conflict = Assert.IsType<ConflictObjectResult>(await moveTask);
            var payload = JsonSerializer.Serialize(conflict.Value);
            Assert.Contains("move_already_active", payload, StringComparison.Ordinal);
            Assert.Contains(activeJob.Id.ToString(), payload, StringComparison.OrdinalIgnoreCase);

            releaseLock.SetResult();
            await lockTask;
            moveQueue.Verify(service => service.GetRecoveryStateForAudiobookAsync(
                activeJob.AudiobookId,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MoveAudiobook_CancelledWhileWaitingForFilesystemMutationDoesNotEnqueue()
        {
            var coordinator = new FilesystemMutationCoordinator();
            var moveQueue = CreateMoveQueueMock();
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFilesystemMutationCoordinator>(coordinator));
            var outputPath = FileService.GetTempDirectory("listenarr-move-cancel-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-cancel-src");
            await FileService.GetFileAsync(sourcePath, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Cancelled Physical Gate")
                .WithBasePath(sourcePath)
                .Build());
            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;
            using var cancellation = new CancellationTokenSource();

            var moveTask = _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(outputPath, "cancelled-target"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                },
                cancellation.Token);
            await Task.Delay(50);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveTask);
            releaseLock.SetResult();
            await lockTask;
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MoveAudiobook_MetadataOnlyWaitsForFilesystemMutationCoordinator()
        {
            var coordinator = new FilesystemMutationCoordinator();
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFilesystemMutationCoordinator>(coordinator));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(outputPath, "metadata-target");
            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var moveTask = _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath, MoveFiles = false });
            await Task.Delay(50);
            Assert.False(moveTask.IsCompleted);

            releaseLock.SetResult();
            await lockTask;
            Assert.IsType<OkObjectResult>(await moveTask);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "PreservesDestinationPathWhitespace_WhenMoveFilesFalse")]
        public async Task MoveAudiobook_PreservesDestinationPathWhitespace_WhenMoveFilesFalse()
        {
            // Given
            var outputPath = FileService.GetTempPath();
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            var relativeTarget = "  listenarr-move-dst-" + Guid.NewGuid().ToString("N");
            var request = new LibraryController.MoveRequest { DestinationPath = relativeTarget, MoveFiles = false };

            // When
            var result = await controller.EnqueueMove(audiobook.Id, request);

            // Then
            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(Path.Join(outputPath, relativeTarget)), updated.BasePath);
            Assert.StartsWith("  listenarr-move-dst-", Path.GetFileName(updated.BasePath), StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsInvalidDestinationPath")]
        public async Task MoveAudiobook_RejectsInvalidDestinationPath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = Path.Join(FileService.GetTempPath(), "bad\0target"),
                MoveFiles = false
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("DestinationPath", badObj.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AuthorizedRootReturnedAfterTransientFailure")]
        public async Task MoveAudiobook_AuthorizedRootReturnedAfterTransientFailure_UsesLiveGeneration()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-move-returned-root");
            var identity = await new DirectoryObjectIdentityResolver().ResolveAsync(rootPath);
            Assert.True(identity.IsAvailable, identity.UnavailableReason);
            var root = new RootFolderBuilder()
                .WithName("Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build();
            root.ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.DirectoryObjectIdentityVersion = identity.Version;
            root.DirectoryObjectIdentity = identity.Value;
            root.DirectoryObjectIdentityUnavailableReason =
                "The directory was unavailable during startup.";
            await _rootFolderRepository.AddAsync(root);

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-returned-src"))
                .Build());
            var target = Path.Join(rootPath, "Author", "Title");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = target,
                    MoveFiles = false
                });

            var ok = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            Assert.Equal(
                FileUtils.NormalizeStoredPath(target),
                (await _audiobookRepository.GetByIdAsync(audiobook.Id))!.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ConfiguredRootsDoNotRequireLegacySettingsRead")]
        public async Task MoveAudiobook_ConfiguredRootsExist_DoesNotRequireLegacySettingsRead()
        {
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            var configuration = new Mock<IConfigurationService>(MockBehavior.Strict);
            configuration.Setup(service => service.GetApplicationSettingsAsync())
                .ThrowsAsync(new InvalidOperationException("Injected legacy settings outage."));
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton(configuration.Object));

            var rootPath = FileService.GetTempDirectory(
                "listenarr-move-settings-independent-root");
            var root = await AddAuthorizedRootAsync(rootPath, "Managed Root");
            root.IsDefault = true;
            await _rootFolderRepository.UpdateAsync(root);
            var sourcePath = Path.Join(rootPath, "Author", "Source");
            var targetPath = Path.Join(rootPath, "Author", "Target");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Settings-independent move")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sourcePath);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            Assert.IsType<AcceptedResult>(result);
            configuration.Verify(
                service => service.GetApplicationSettingsAsync(),
                Times.Never);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ManagedRootChangedFilesystemSemanticsBlocksPhysicalMove")]
        public async Task MoveAudiobook_ManagedRootChangedFilesystemSemantics_BlocksPhysicalMove()
        {
            var moveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-move-semantics-changed-root");
            var actualSemantics = FileSystemPathSemantics.CurrentHostDefault;
            var persistedSensitivity = actualSemantics.CaseSensitivity
                == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivity.Insensitive
                    : FileSystemCaseSensitivity.Sensitive;
            var persistedSemantics = new FileSystemPathSemantics(
                actualSemantics.Syntax,
                persistedSensitivity);
            var identity = await new DirectoryObjectIdentityResolver().ResolveAsync(rootPath);
            Assert.True(identity.IsAvailable, identity.UnavailableReason);
            var root = new RootFolderBuilder()
                .WithName("Changed Semantics Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build();
            root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            root.ResolvedCaseSensitivity = persistedSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                rootPath,
                persistedSemantics);
            root.DirectoryObjectIdentityVersion = identity.Version;
            root.DirectoryObjectIdentity = identity.Value;
            await _rootFolderRepository.AddAsync(root);

            var sourcePath = FileService.GetTempDirectory("listenarr-move-semantics-changed-source");
            await FileService.GetFileAsync(sourcePath, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(rootPath, "Author", "Title"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            Assert.IsType<BadRequestObjectResult>(result);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "UnconfirmedManagedRootBlocksPhysicalMove")]
        public async Task MoveAudiobook_UnconfirmedManagedRoot_BlocksPhysicalMove()
        {
            var moveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-move-unconfirmed-root");
            var root = new RootFolderBuilder()
                .WithName("Unconfirmed Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build();
            root.ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.DirectoryObjectIdentityUnavailableReason =
                "The root folder physical directory has not been confirmed.";
            await _rootFolderRepository.AddAsync(root);

            var sourcePath = FileService.GetTempDirectory("listenarr-move-unconfirmed-source");
            await FileService.GetFileAsync(sourcePath, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(rootPath, "Author", "Title"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(
                "physical identity is unavailable",
                badRequest.Value?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ReadOnlyDestinationRootRejectsBeforeEnqueue")]
        public async Task MoveAudiobook_ReadOnlyDestinationRoot_RejectsBeforeEnqueue()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            var storageHealth = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton(storageHealth.Object));
            var sourceRootPath = FileService.GetTempDirectory(
                "listenarr-move-writable-source-root");
            var targetRootPath = FileService.GetTempDirectory(
                "listenarr-move-readonly-target-root");
            var sourceRoot = await AddAuthorizedRootAsync(
                sourceRootPath,
                "Writable Source Root");
            var targetRoot = await AddAuthorizedRootAsync(
                targetRootPath,
                "Read-only Target Root");
            storageHealth.Setup(service => service.ResolveAsync(
                    It.Is<RootFolder>(root => root.Id == targetRoot.Id),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderStorageObservation(
                    RootFolderStorageState.Limited,
                    RootFolderStorageReason.ReadOnlyFilesystem,
                    "This storage is mounted read-only.",
                    CanConfirmCurrentFolder: false,
                    CanChangePath: true,
                    CanMutateFilesystem: false,
                    ConfirmationToken: null));

            var sourcePath = Path.Join(sourceRootPath, "Author", "Source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Read-only destination")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sourceRoot.Path);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(targetRootPath, "Author", "Target"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains(
                "destination_filesystem_mutation_unavailable",
                payload,
                StringComparison.Ordinal);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
            storageHealth.VerifyAll();
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ReadOnlySourceRootRejectsBeforeEnqueue")]
        public async Task MoveAudiobook_ReadOnlySourceRoot_RejectsBeforeEnqueue()
        {
            var moveQueue = CreateStrictMoveQueueMock();
            var storageHealth = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton(storageHealth.Object));
            var sourceRootPath = FileService.GetTempDirectory(
                "listenarr-move-readonly-source-root");
            var targetRootPath = FileService.GetTempDirectory(
                "listenarr-move-writable-target-root");
            var sourceRoot = await AddAuthorizedRootAsync(
                sourceRootPath,
                "Read-only Source Root");
            var targetRoot = await AddAuthorizedRootAsync(
                targetRootPath,
                "Writable Target Root");
            storageHealth.Setup(service => service.ResolveAsync(
                    It.Is<RootFolder>(root => root.Id == targetRoot.Id),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderStorageObservation(
                    RootFolderStorageState.Healthy,
                    RootFolderStorageReason.None,
                    null,
                    CanConfirmCurrentFolder: false,
                    CanChangePath: true,
                    CanMutateFilesystem: true,
                    ConfirmationToken: null));
            storageHealth.Setup(service => service.ResolveAsync(
                    It.Is<RootFolder>(root => root.Id == sourceRoot.Id),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderStorageObservation(
                    RootFolderStorageState.Limited,
                    RootFolderStorageReason.ReadOnlyFilesystem,
                    "This storage is mounted read-only.",
                    CanConfirmCurrentFolder: false,
                    CanChangePath: true,
                    CanMutateFilesystem: false,
                    ConfirmationToken: null));

            var sourcePath = Path.Join(sourceRootPath, "Author", "Source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Read-only source")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sourceRoot.Path);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(targetRootPath, "Author", "Target"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains(
                "source_filesystem_mutation_unavailable",
                payload,
                StringComparison.Ordinal);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
            storageHealth.VerifyAll();
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "NarrowTrackedIdentityBoundaryUsesManagedRootMutationAuthority")]
        public async Task MoveAudiobook_NarrowTrackedIdentityBoundary_UsesManagedRootMutationAuthority()
        {
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(moveQueue.Object));
            var rootPath = FileService.GetTempDirectory(
                "listenarr-move-managed-source-root");
            await AddAuthorizedRootAsync(rootPath, "Managed Source Root");

            var sourcePath = Path.Join(rootPath, "Author", "BookMoved");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Moved source")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sourcePath);
            var targetPath = Path.Join(rootPath, "Author", "BookReturned");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    SourcePath = sourcePath,
                    MoveFiles = true,
                    DeleteEmptySource = true
                });

            Assert.IsType<AcceptedResult>(result);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.SourcePath == sourcePath
                    && command.SourceIdentity.BoundaryPath == sourcePath
                    && command.SourceCleanupBoundary == rootPath
                    && command.SourceBoundaryDirectoryObjectIdentityVersion > 0
                    && !string.IsNullOrWhiteSpace(
                        command.SourceBoundaryDirectoryObjectIdentity)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "NarrowTrackedIdentityBoundaryWithoutCleanupKeepsManagedRootAuthority")]
        public async Task MoveAudiobook_NarrowTrackedIdentityBoundary_DeleteEmptySourceFalse_PersistsManagedRootAuthorizationBoundary()
        {
            var moveQueue = CreateMoveQueueMock();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(moveQueue.Object));
            var rootPath = FileService.GetTempDirectory(
                "listenarr-move-managed-source-root-no-cleanup");
            await AddAuthorizedRootAsync(rootPath, "Managed Source Root");

            var sourcePath = Path.Join(rootPath, "Author", "BookMoved");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Moved source without cleanup")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sourcePath);
            var targetPath = Path.Join(rootPath, "Author", "BookReturned");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    SourcePath = sourcePath,
                    MoveFiles = true,
                    DeleteEmptySource = false
                });

            Assert.IsType<AcceptedResult>(result);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.SourcePath == sourcePath
                    && command.SourceIdentity.BoundaryPath == sourcePath
                    && !command.DeleteEmptySource
                    && command.SourceCleanupBoundary == rootPath
                    && command.SourceBoundaryDirectoryObjectIdentityVersion > 0
                    && !string.IsNullOrWhiteSpace(
                        command.SourceBoundaryDirectoryObjectIdentity)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "TrackedIdentityBoundaryOutsideManagedRootIsRejected")]
        public async Task MoveAudiobook_TrackedIdentityBoundaryOutsideManagedRoot_IsRejected()
        {
            var moveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var authorityParent = FileService.GetTempDirectory(
                "listenarr-move-outside-source-authority");
            var rootPath = Path.Join(authorityParent, "ManagedRoot");
            await AddAuthorizedRootAsync(rootPath, "Managed Source Root");

            var sourcePath = Path.Join(rootPath, "Author", "Book");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Overbroad source authority")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: authorityParent);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(rootPath, "Author", "Target"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(
                "source_physical_identity_unavailable",
                badRequest.Value?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [WindowsFact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ChangedSemanticsManagedSourceRootBlocksCaseAliasFallback")]
        public async Task MoveAudiobook_ChangedSemanticsManagedSourceRoot_BlocksCaseAliasFallback()
        {
            var moveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var sourceRootPath = FileService.GetTempDirectory(
                "listenarr-move-changed-source-root");
            var targetRootPath = FileService.GetTempDirectory(
                "listenarr-move-changed-source-target");
            var liveResolution = await _provider
                .GetRequiredService<IFileSystemSemanticsResolver>()
                .ResolveAsync(sourceRootPath);
            Assert.Equal(PathIdentityState.Valid, liveResolution.State);
            var persistedSensitivity = liveResolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivity.Insensitive
                    : FileSystemCaseSensitivity.Sensitive;
            var persistedSemantics = new FileSystemPathSemantics(
                liveResolution.Semantics.Syntax,
                persistedSensitivity);
            var sourceDirectoryIdentity = await new DirectoryObjectIdentityResolver()
                .ResolveAsync(sourceRootPath);
            Assert.True(
                sourceDirectoryIdentity.IsAvailable,
                sourceDirectoryIdentity.UnavailableReason);
            var sourceRoot = new RootFolderBuilder()
                .WithName("Changed Source Root")
                .WithPath(sourceRootPath)
                .Build();
            sourceRoot.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            sourceRoot.ResolvedCaseSensitivity = persistedSensitivity;
            sourceRoot.PathIdentityState = PathIdentityState.Valid;
            sourceRoot.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                sourceRootPath,
                persistedSemantics);
            sourceRoot.DirectoryObjectIdentityVersion = sourceDirectoryIdentity.Version;
            sourceRoot.DirectoryObjectIdentity = sourceDirectoryIdentity.Value;
            await _rootFolderRepository.AddAsync(sourceRoot);

            var targetDirectoryIdentity = await new DirectoryObjectIdentityResolver()
                .ResolveAsync(targetRootPath);
            Assert.True(
                targetDirectoryIdentity.IsAvailable,
                targetDirectoryIdentity.UnavailableReason);
            var targetRoot = new RootFolderBuilder()
                .WithName("Healthy Target Root")
                .WithPath(targetRootPath)
                .WithIsDefault()
                .Build();
            targetRoot.ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity;
            targetRoot.PathIdentityState = PathIdentityState.Valid;
            targetRoot.DirectoryObjectIdentityVersion = targetDirectoryIdentity.Version;
            targetRoot.DirectoryObjectIdentity = targetDirectoryIdentity.Value;
            await _rootFolderRepository.AddAsync(targetRoot);

            var sourceAliasRoot = sourceRootPath.ToUpperInvariant();
            if (string.Equals(sourceAliasRoot, sourceRootPath, StringComparison.Ordinal))
            {
                sourceAliasRoot = sourceRootPath.ToLowerInvariant();
            }
            Assert.NotEqual(sourceRootPath, sourceAliasRoot);
            var sourcePath = Path.Join(sourceAliasRoot, "Author", "Title");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Changed source semantics")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sourceAliasRoot);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(targetRootPath, "Author", "Title"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(
                "source_physical_identity_unavailable",
                badRequest.Value?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "UnconfirmedManagedSourceRootBlocksPhysicalMove")]
        public async Task MoveAudiobook_UnconfirmedManagedSourceRoot_BlocksMoveToHealthyRoot()
        {
            var moveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var sourceRootPath = FileService.GetTempDirectory(
                "listenarr-move-unconfirmed-source-root");
            var targetRootPath = FileService.GetTempDirectory(
                "listenarr-move-healthy-target-root");
            var sourceRoot = new RootFolderBuilder()
                .WithName("Unconfirmed Source Root")
                .WithPath(sourceRootPath)
                .Build();
            sourceRoot.ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity;
            sourceRoot.PathIdentityState = PathIdentityState.Valid;
            sourceRoot.DirectoryObjectIdentityUnavailableReason =
                "The source root physical directory has not been confirmed.";
            await _rootFolderRepository.AddAsync(sourceRoot);

            var targetDirectoryIdentity = await new DirectoryObjectIdentityResolver()
                .ResolveAsync(targetRootPath);
            Assert.True(
                targetDirectoryIdentity.IsAvailable,
                targetDirectoryIdentity.UnavailableReason);
            var targetRoot = new RootFolderBuilder()
                .WithName("Healthy Target Root")
                .WithPath(targetRootPath)
                .WithIsDefault()
                .Build();
            targetRoot.ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity;
            targetRoot.PathIdentityState = PathIdentityState.Valid;
            targetRoot.DirectoryObjectIdentityVersion =
                targetDirectoryIdentity.Version;
            targetRoot.DirectoryObjectIdentity = targetDirectoryIdentity.Value;
            await _rootFolderRepository.AddAsync(targetRoot);

            var sourcePath = Path.Join(sourceRootPath, "Author", "Source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Unconfirmed source authority")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sourceRootPath);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(targetRootPath, "Author", "Target"),
                    SourcePath = sourcePath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(
                "source_physical_identity_unavailable",
                badRequest.Value?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsAbsoluteDestinationInsideConfiguredRootFolder")]
        public async Task MoveAudiobook_AllowsAbsoluteDestinationInsideConfiguredRootFolder()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var target = Path.Join(rootPath, "Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = target, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(target), updated.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RewritesStoredPathsWithoutMovingFiles")]
        public async Task MoveAudiobook_PathOnlyUpdate_RewritesStoredAbsoluteReferences()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services.WithSingleton(moveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-path-only-root");
            var sourcePath = FileService.GetTempDirectory("listenarr-path-only-source");
            var targetPath = Path.Join(rootPath, "Author", "Title");
            var unrelatedPath = Path.Join(FileService.GetTempPath(), "outside", "bonus.mp3");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Path Only Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Path Only",
                BasePath = sourcePath,
                FilePath = Path.Join(sourcePath, "book.m4b"),
                ImageUrl = Path.Join(sourcePath, "cover.jpg"),
                Files =
                [
                    new AudiobookFile { Path = Path.Join(sourcePath, "book.m4b") },
                    new AudiobookFile { Path = Path.Join("disc-1", "chapter.mp3") },
                    new AudiobookFile { Path = unrelatedPath }
                ]
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.EnqueueMove(audiobook.Id, new LibraryController.MoveRequest
            {
                DestinationPath = targetPath,
                MoveFiles = false
            });

            var ok = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(targetPath, updated.BasePath);
            Assert.Equal(Path.Join(targetPath, "book.m4b"), updated.FilePath);
            Assert.Equal(Path.Join(targetPath, "cover.jpg"), updated.ImageUrl);
            Assert.Contains(updated.Files!, file => file.Path == Path.Join(targetPath, "book.m4b"));
            Assert.Contains(updated.Files!, file => file.Path == Path.Join("disc-1", "chapter.mp3"));
            Assert.Contains(updated.Files!, file => file.Path == unrelatedPath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "SetsBasePathWhenNoPriorBaseExists")]
        public async Task MoveAudiobook_PathOnlyUpdate_WithoutSourceBase_PreservesUnrelatedReferences()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-path-only-empty-root");
            var targetPath = Path.Join(rootPath, "Author", "Title");
            var legacyPath = Path.Join(FileService.GetTempPath(), "legacy", "book.m4b");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Path Only Empty Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "No Source Base",
                FilePath = legacyPath,
                Files = [new AudiobookFile { Path = legacyPath }]
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.EnqueueMove(audiobook.Id, new LibraryController.MoveRequest
            {
                DestinationPath = targetPath,
                MoveFiles = false
            });

            var ok = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(targetPath, updated.BasePath);
            Assert.Equal(legacyPath, updated.FilePath);
            Assert.Equal(legacyPath, Assert.Single(updated.Files!).Path);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsAbsoluteDestinationInsideConfiguredOutputPath")]
        public async Task MoveAudiobook_AllowsAbsoluteDestinationInsideConfiguredOutputPath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var target = Path.Join(outputPath, "Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = target, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(target), updated.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "LegacyOutputPathDoesNotAuthorizeOutsideConfiguredRoot")]
        public async Task MoveAudiobook_LegacyOutputPathDoesNotAuthorizeOutsideConfiguredRoot()
        {
            var legacyOutputPath = FileService.GetTempDirectory("listenarr-legacy-output");
            var rootPath = FileService.GetTempDirectory("listenarr-managed-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(legacyOutputPath)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Managed Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());
            var sourcePath = Path.Join(rootPath, "Author", "Source");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Legacy output authority")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(legacyOutputPath, "Author", "Target");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    MoveFiles = false
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("destination_path_outside_roots", badRequest.Value?.ToString() ?? string.Empty);
            var unchanged = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.Equal(sourcePath, unchanged!.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "LegacyOutputPathDoesNotAuthorizePhysicalMoveOutsideConfiguredRoot")]
        public async Task MoveAudiobook_LegacyOutputPathDoesNotAuthorizePhysicalMoveOutsideConfiguredRoot()
        {
            var moveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(moveQueue.Object));
            var legacyOutputPath = FileService.GetTempDirectory("listenarr-legacy-physical-output");
            var rootPath = FileService.GetTempDirectory("listenarr-managed-physical-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(legacyOutputPath)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Managed Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());
            var sourcePath = Path.Join(rootPath, "Author", "Source");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Legacy physical output authority")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath, identityBoundary: rootPath);
            var targetPath = Path.Join(legacyOutputPath, "Author", "Target");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    MoveFiles = true
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("destination_path_outside_roots", badRequest.Value?.ToString() ?? string.Empty);
            moveQueue.Verify(queue => queue.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsAbsoluteDestinationOutsideConfiguredRoots")]
        public async Task MoveAudiobook_RejectsAbsoluteDestinationOutsideConfiguredRoots()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var originalBasePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(originalBasePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var outsidePath = Path.Join(FileService.GetTempDirectory("listenarr-move-outside"), "Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = outsidePath, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("configured root folder or output path", badObj.Value?.ToString() ?? string.Empty);

            var unchanged = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(unchanged);
            Assert.Equal(originalBasePath, unchanged.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "UsesDefaultRootFolderForRelativeDestination_WhenOutputPathEmpty")]
        public async Task MoveAudiobook_UsesDefaultRootFolderForRelativeDestination_WhenOutputPathEmpty()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(string.Empty)
                .Build());
            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Default Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var relativeTarget = Path.Join("Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = relativeTarget, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(Path.Join(rootPath, relativeTarget)), updated.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsDestinationPathWithLeadingWhitespaceBeforeAbsolutePath")]
        public async Task MoveAudiobook_RejectsDestinationPathWithLeadingWhitespaceBeforeAbsolutePath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = " " + Path.Join(outputPath, "target"),
                MoveFiles = false
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("leading whitespace", badObj.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [LinuxFact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsCaseOnlyDestinationDifference_OnCaseSensitiveHosts")]
        public async Task MoveAudiobook_AllowsCaseOnlyDestinationDifference_OnCaseSensitiveHosts()
        {

            var mockMoveQueue = CreateMoveQueueMock();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedId);

            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = Path.Join(outputPath, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(audiobook, sourcePath);

            var targetPath = Path.Join(outputPath, "caseonlybook");
            var request = new LibraryController.MoveRequest { DestinationPath = targetPath };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var acceptedObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, acceptedObj.StatusCode);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == audiobook.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(targetPath)
                    && command.SourcePath == sourcePath
                    && command.DeleteEmptySource
                    && command.SourceCleanupBoundary == outputPath),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "TreatsCaseOnlyDestinationAsIdentical_OnCaseInsensitiveRoot")]
        public async Task MoveAudiobook_TreatsCaseOnlyDestinationAsIdentical_OnCaseInsensitiveRoot()
        {
            var mockMoveQueue = CreateMoveQueueMock();
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-move-insensitive-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(rootPath)
                .Build());
            var insensitiveRoot = await AddAuthorizedRootAsync(
                rootPath,
                "Insensitive Move Root",
                FileSystemCaseSensitivityMode.Insensitive);
            insensitiveRoot.IsDefault = true;
            await _rootFolderRepository.UpdateAsync(insensitiveRoot);

            var sourcePath = Path.Join(rootPath, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: rootPath,
                caseSensitivityMode: FileSystemCaseSensitivityMode.Insensitive);
            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = Path.Join(rootPath, "caseonlybook")
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<BadRequestObjectResult>(result);
            Assert.Contains("identical", badObj.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsCaseOnlyDestination_OnExplicitlyCaseSensitiveRoot")]
        public async Task MoveAudiobook_AllowsCaseOnlyDestination_OnExplicitlyCaseSensitiveRoot()
        {
            var mockMoveQueue = CreateMoveQueueMock();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedId);
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-move-sensitive-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(rootPath)
                .Build());
            var sensitiveRoot = await AddAuthorizedRootAsync(
                rootPath,
                "Sensitive Move Root",
                FileSystemCaseSensitivityMode.Sensitive);
            sensitiveRoot.IsDefault = true;
            await _rootFolderRepository.UpdateAsync(sensitiveRoot);

            var sourcePath = Path.Join(rootPath, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: rootPath,
                caseSensitivityMode: FileSystemCaseSensitivityMode.Sensitive);
            var targetPath = Path.Join(rootPath, "caseonlybook");
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.EnqueueMove(audiobook.Id, new LibraryController.MoveRequest
            {
                DestinationPath = targetPath
            });

            Assert.IsType<AcceptedResult>(result);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == audiobook.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(targetPath)
                    && command.SourcePath == sourcePath
                    && command.DeleteEmptySource
                    && command.SourceCleanupBoundary == rootPath
                    && command.SourceIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                    && command.TargetIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [LinuxFact]
        public async Task MoveAudiobook_AmbiguousNestedManagedSourceRoot_DoesNotFallBackToBroaderRootAuthority()
        {
            var mockMoveQueue = CreateMoveQueueMock();
            mockMoveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var outerRoot = FileService.GetTempDirectory("listenarr-move-ambiguous-source-outer");
            var innerRoot = Path.Join(outerRoot, "Managed Inner");
            Directory.CreateDirectory(innerRoot);
            await AddAuthorizedRootAsync(
                outerRoot,
                "Outer Managed Root",
                FileSystemCaseSensitivityMode.Sensitive);
            var ambiguousInnerRoot = "/" + innerRoot;
            Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                ambiguousInnerRoot,
                out _));
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Ambiguous Nested Source Root")
                .WithPath(ambiguousInnerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());

            var sourcePath = Path.Join(innerRoot, "Author", "Source Book");
            var targetPath = Path.Join(outerRoot, "Target Book");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested source authority")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: innerRoot,
                caseSensitivityMode: FileSystemCaseSensitivityMode.Sensitive);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains(
                "source_physical_identity_unavailable",
                payload,
                StringComparison.Ordinal);
            mockMoveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [LinuxFact]
        public async Task MoveAudiobook_AmbiguousNestedManagedRoot_DoesNotFallBackToBroaderRootAuthority()
        {
            var mockMoveQueue = CreateMoveQueueMock();
            mockMoveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var outerRoot = FileService.GetTempDirectory("listenarr-move-ambiguous-outer");
            var innerRoot = Path.Join(outerRoot, "Managed Inner");
            Directory.CreateDirectory(innerRoot);
            await AddAuthorizedRootAsync(
                outerRoot,
                "Outer Managed Root",
                FileSystemCaseSensitivityMode.Sensitive);
            var ambiguousInnerRoot = "/" + innerRoot;
            Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                ambiguousInnerRoot,
                out _));
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Ambiguous Nested Root")
                .WithPath(ambiguousInnerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());

            var sourcePath = Path.Join(outerRoot, "Source Book");
            var targetPath = Path.Join(innerRoot, "Author", "Target Book");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested root authority")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: outerRoot,
                caseSensitivityMode: FileSystemCaseSensitivityMode.Sensitive);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payload = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains(
                "destination_filesystem_identity_unavailable",
                payload,
                StringComparison.Ordinal);
            mockMoveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MoveAudiobook_NestedExplicitRoot_OverridesBroaderOutputPathSemantics()
        {
            var mockMoveQueue = CreateMoveQueueMock();
            mockMoveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-nested-output");
            var sensitiveRoot = Path.Join(outputPath, "Sensitive Library");
            Directory.CreateDirectory(sensitiveRoot);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            await AddAuthorizedRootAsync(
                sensitiveRoot,
                "Nested Sensitive Root",
                FileSystemCaseSensitivityMode.Sensitive);

            var sourcePath = Path.Join(sensitiveRoot, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var targetPath = Path.Join(sensitiveRoot, "caseonlybook");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested sensitive move")
                .WithBasePath(sourcePath)
                .Build());
            await AddTrackedFileAsync(
                audiobook,
                sourcePath,
                identityBoundary: sensitiveRoot,
                caseSensitivityMode: FileSystemCaseSensitivityMode.Sensitive);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath });

            Assert.IsType<AcceptedResult>(result);
            mockMoveQueue.Verify(service => service.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.SourceIdentity.BoundaryPath == sensitiveRoot
                    && command.TargetIdentity.BoundaryPath == sensitiveRoot
                    && command.SourceIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                    && command.TargetIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private async Task<string> AddTrackedFileAsync(
            Audiobook audiobook,
            string sourcePath,
            string fileName = "book.m4b",
            bool createFile = true,
            string? identityBoundary = null,
            FileSystemCaseSensitivityMode caseSensitivityMode = FileSystemCaseSensitivityMode.Auto)
        {
            Directory.CreateDirectory(sourcePath);
            var filePath = Path.Join(sourcePath, fileName);
            if (createFile)
            {
                await File.WriteAllTextAsync(filePath, "audio");
            }

            var resolution = await _provider
                .GetRequiredService<IFileSystemSemanticsResolver>()
                .ResolveAsync(filePath, caseSensitivityMode);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            var identity = AudiobookFilePathIdentity.CreateValid(
                filePath,
                resolution.Semantics,
                caseSensitivityMode,
                identityBoundary ?? resolution.BoundaryPath);
            var tracked = new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(filePath)
                .Build();
            tracked.ApplyPathIdentity(filePath, identity);
            if (File.Exists(filePath))
            {
                using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    Path.GetDirectoryName(filePath)!,
                    createMissing: false);
                using var file = parent.OpenExistingFileForStableRead(Path.GetFileName(filePath));
                tracked.ApplyPhysicalObjectIdentity(
                    file.GetObjectIdentity(),
                    DateTime.UtcNow);
            }
            await _audiobookFileRepository.AddAsync(tracked);
            return filePath;
        }

        private sealed class BeforeExecuteAudiobookCoordinator(Func<Task> beforeExecute)
            : IAudiobookOperationCoordinator, IDisposable
        {
            private readonly AudiobookOperationCoordinator _inner = new();
            private int _invoked;

            public Task ExecuteExclusiveAsync(
                int audiobookId,
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookId,
                    token => ExecuteAfterCallbackAsync(operation, token),
                    cancellationToken);

            public Task<T> ExecuteExclusiveAsync<T>(
                int audiobookId,
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookId,
                    token => ExecuteAfterCallbackAsync(operation, token),
                    cancellationToken);

            public Task ExecuteExclusiveAsync(
                IEnumerable<int> audiobookIds,
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookIds,
                    token => ExecuteAfterCallbackAsync(operation, token),
                    cancellationToken);

            public Task<T> ExecuteExclusiveAsync<T>(
                IEnumerable<int> audiobookIds,
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default) =>
                _inner.ExecuteExclusiveAsync(
                    audiobookIds,
                    token => ExecuteAfterCallbackAsync(operation, token),
                    cancellationToken);

            private async Task ExecuteAfterCallbackAsync(
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken)
            {
                await InvokeBeforeExecuteOnceAsync();
                await operation(cancellationToken);
            }

            private async Task<T> ExecuteAfterCallbackAsync<T>(
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken)
            {
                await InvokeBeforeExecuteOnceAsync();
                return await operation(cancellationToken);
            }

            private async Task InvokeBeforeExecuteOnceAsync()
            {
                if (Interlocked.Exchange(ref _invoked, 1) == 0)
                {
                    await beforeExecute();
                }
            }

            public void Dispose() => _inner.Dispose();
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsRelativeDestinationOutsideOutputPath")]
        public async Task MoveAudiobook_RejectsRelativeDestinationOutsideOutputPath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = Path.Join("..", "escape"),
                MoveFiles = false
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("DestinationPath", badObj.Value?.ToString() ?? string.Empty);
        }
    }
}
