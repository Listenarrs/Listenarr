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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using AppMoveQueueService = Listenarr.Application.Audiobooks.Jobs.MoveQueueService;
using MoveQueueService = Listenarr.Tests.Features.Application.Audiobooks.Jobs.MoveQueueServiceTestAdapter;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs
{
    public class MoveQueueServiceTests
    {
        private const string LeaseOwner = "test-worker";

        [Fact]
        public async Task EnsureFilesystemMutationAllowedAsync_ActiveMetadataRootRepairBlocksAudiobookMutation()
        {
            const int audiobookId = 4141;
            var persistence = new Mock<IMoveQueuePersistence>(MockBehavior.Strict);
            var relocation = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
            relocation.Setup(service => service.IsAudiobookPathStateProtectedAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocationService: relocation.Object);

            var blocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnsureFilesystemMutationAllowedAsync(audiobookId));

            Assert.Equal("root_folder_relocation_active", blocked.Code);
            persistence.VerifyNoOtherCalls();
            relocation.Verify(service => service.IsAudiobookPathStateProtectedAsync(
                audiobookId,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task EnsureFilesystemMutationAllowedAsync_ActiveDeletionIntentBlocksOtherMutationsButAllowsDeletionRecovery()
        {
            const int audiobookId = 4242;
            var persistence = new Mock<IMoveQueuePersistence>(MockBehavior.Strict);
            persistence.Setup(store => store.GetRecoveryCandidatesByAudiobookAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var deletionProbe = new Mock<IAudiobookDeletionIntentProbe>(MockBehavior.Strict);
            deletionProbe.Setup(probe => probe.HasActiveAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                deletionIntentProbe: deletionProbe.Object);

            var blocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnsureFilesystemMutationAllowedAsync(audiobookId));
            Assert.Equal("delete_recovery_pending", blocked.Code);
            persistence.Verify(store => store.GetRecoveryCandidatesByAudiobookAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.Never);

            await service.EnsureFilesystemMutationAllowedAsync(
                audiobookId,
                allowActiveDeletionIntent: true);
            persistence.Verify(store => store.GetRecoveryCandidatesByAudiobookAsync(
                audiobookId,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task EnqueueMoveAsync_ActiveDeletionIntentBlocksBeforeMovePersistence()
        {
            const int audiobookId = 4243;
            var persistence = new Mock<IMoveQueuePersistence>(MockBehavior.Strict);
            var deletionProbe = new Mock<IAudiobookDeletionIntentProbe>(MockBehavior.Strict);
            deletionProbe.Setup(probe => probe.HasActiveAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var service = new MoveQueueServiceTestAdapter(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                deletionIntentProbe: deletionProbe.Object);
            var root = Path.Join(
                Path.GetTempPath(),
                $"listenarr-move-enqueue-delete-intent-{Guid.NewGuid():N}");
            var source = Path.Join(root, "source");
            var target = Path.Join(root, "target");
            Directory.CreateDirectory(source);
            try
            {
                var blocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                    service.EnqueueMoveAsync(audiobookId, target, source));

                Assert.Equal("delete_recovery_pending", blocked.Code);
                persistence.VerifyNoOtherCalls();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task EnqueueMoveAsync_DeletionRecoveryAppearsBeforeDurablePublication_BlocksInsideMutationCoordinator()
        {
            const int audiobookId = 4244;
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var deletionProbe = new Mock<IAudiobookDeletionIntentProbe>(MockBehavior.Strict);
            deletionProbe.SetupSequence(probe => probe.HasActiveAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false)
                .ReturnsAsync(true);
            var service = new MoveQueueServiceTestAdapter(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                deletionIntentProbe: deletionProbe.Object);

            var blocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnqueueMoveAsync(
                    audiobookId,
                    "/library/Title",
                    "/downloads/Title"));

            Assert.Equal("delete_recovery_pending", blocked.Code);
            Assert.Empty(jobs);
            persistence.Verify(store => store.AddAsync(
                It.IsAny<MoveJob>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task EnqueueMoveAsync_DifferentActiveMoveForSameAudiobook_BlocksCompetingMove()
        {
            const int audiobookId = 4245;
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueServiceTestAdapter(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(
                audiobookId,
                "/library/FirstTitle",
                "/downloads/Title");
            var first = Assert.Single(jobs);
            first.Phase = MoveJobPhase.Copying;
            first.Entries.First(entry => entry.EntryType == MoveJobEntryType.File).CopyState =
                MoveJobEntryCopyState.Staged;

            var blocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnqueueMoveAsync(
                    audiobookId,
                    "/library/SecondTitle",
                    "/downloads/Title"));

            Assert.Equal("move_already_active", blocked.Code);
            Assert.Single(jobs);
            Assert.Equal(firstId, jobs[0].Id);
        }

        [Fact]
        public async Task EnqueueMoveAsync_UnresolvedMoveExecutionEvidence_BlocksCompetingMove()
        {
            const int audiobookId = 4245;
            var source = FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/Title");
            var unresolvedTarget = FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/OldTitle");
            var jobs = new List<MoveJob>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    AudiobookId = audiobookId,
                    SourcePath = source,
                    RequestedPath = unresolvedTarget,
                    Status = MoveJobStatus.NeedsAttention,
                    Phase = MoveJobPhase.Published,
                    FailureKind = MoveFailureKind.Verification,
                    ExecutionProtocolVersion = MoveExecutionProtocol.Current,
                    ActiveDeduplicationKey = null,
                    Entries =
                    [
                        new MoveJobEntry
                        {
                            RelativePath = "book.m4b",
                            EntryType = MoveJobEntryType.File,
                            Length = 1,
                            LastWriteTimeUtc = DateTime.UnixEpoch,
                            Sha256 = new string('A', 64),
                            CopyState = MoveJobEntryCopyState.Verified,
                            CleanupState = MoveJobEntryCleanupState.Pending
                        }
                    ]
                }
            };
            var persistence = CreateInMemoryPersistence(jobs);
            persistence.Setup(store => store.GetRecoveryCandidatesByAudiobookAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => jobs
                    .Where(job => job.AudiobookId == audiobookId)
                    .ToList());
            var service = new MoveQueueServiceTestAdapter(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var blocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnqueueMoveAsync(
                    audiobookId,
                    "/library/NewTitle",
                    "/downloads/Title"));

            Assert.Equal("move_repair_required", blocked.Code);
            Assert.Single(jobs);
        }

        [Fact]
        public async Task EnsureFilesystemMutationAllowedAsync_UnreconciledOwnerBoundRenameBlocksMutation()
        {
            const int audiobookId = 4343;
            var persistence = new Mock<IMoveQueuePersistence>(MockBehavior.Strict);
            var renameProbe = new Mock<IFileRenameRecoveryProbe>(MockBehavior.Strict);
            renameProbe.Setup(probe => probe.HasBlockingAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                fileRenameRecoveryProbe: renameProbe.Object);

            var blocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnsureFilesystemMutationAllowedAsync(audiobookId));

            Assert.Equal("rename_recovery_pending", blocked.Code);
            persistence.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task UpdateJobStatus_ExhaustedPersistenceRetries_PropagatesWithoutBroadcasting()
        {
            var job = new MoveJob { Id = Guid.NewGuid(), AudiobookId = 42, LeaseOwner = LeaseOwner, LeaseGeneration = 3 };
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            persistence.Setup(store => store.UpdateStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Completed,
                    It.IsAny<MoveJobPhase>(),
                    null,
                    MoveFailureKind.None,
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PersistenceException(
                    "Status write failed.",
                    new InvalidOperationException("Database unavailable.")));
            var broadcaster = new Mock<IHubBroadcaster>();
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver());

            await Assert.ThrowsAsync<PersistenceException>(() => service.UpdateJobStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Completed));

            persistence.Verify(store => store.UpdateStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Completed,
                It.IsAny<MoveJobPhase>(),
                null,
                MoveFailureKind.None,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
            broadcaster.Verify(service => service.BroadcastAsync(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task IncrementAttempt_StaleLease_ThrowsMoveLeaseLostException()
        {
            var jobId = Guid.NewGuid();
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.TryIncrementAttemptAsync(
                    jobId,
                    LeaseOwner,
                    3,
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            await Assert.ThrowsAsync<MoveLeaseLostException>(() => service.IncrementAttemptAsync(
                jobId,
                LeaseOwner,
                3));
        }

        [Fact]
        public async Task IncrementAttempt_RetriesTransientPersistenceFailures()
        {
            var jobId = Guid.NewGuid();
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.SetupSequence(store => store.TryIncrementAttemptAsync(
                    jobId,
                    LeaseOwner,
                    3,
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PersistenceException(
                    "Attempt write failed.",
                    new InvalidOperationException("Database unavailable.")))
                .ThrowsAsync(new PersistenceException(
                    "Attempt write failed.",
                    new InvalidOperationException("Database unavailable.")))
                .ReturnsAsync(true);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            await service.IncrementAttemptAsync(jobId, LeaseOwner, 3);

            persistence.Verify(store => store.TryIncrementAttemptAsync(
                jobId,
                LeaseOwner,
                3,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task UpdateJobStatus_PostCommitRelocationFailure_RemainsSuccessfulAndBroadcasts()
        {
            var job = new MoveJob { Id = Guid.NewGuid(), AudiobookId = 42, LeaseOwner = LeaseOwner, LeaseGeneration = 3 };
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            persistence.Setup(store => store.UpdateStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Completed,
                    It.IsAny<MoveJobPhase>(),
                    null,
                    MoveFailureKind.None,
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var relocation = new Mock<IRootFolderRelocationService>();
            relocation.Setup(service => service.OnMoveJobStateChangedAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PersistenceException(
                    "Relocation reconciliation failed.",
                    new InvalidOperationException("Database unavailable.")));
            var broadcaster = new Mock<IHubBroadcaster>();
            broadcaster.Setup(service => service.BroadcastAsync(
                    "MoveJobUpdate",
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            await service.UpdateJobStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Completed);

            broadcaster.Verify(service => service.BroadcastAsync(
                "MoveJobUpdate",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateJobStatus_NotificationCanceledAfterCommit_RemainsSuccessful()
        {
            using var cancellation = new CancellationTokenSource();
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 42,
                Status = MoveJobStatus.Running,
                LeaseOwner = LeaseOwner,
                LeaseGeneration = 3,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1)
            };
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetByIdAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            persistence.Setup(store => store.UpdateStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Completed,
                    It.IsAny<MoveJobPhase>(),
                    null,
                    MoveFailureKind.None,
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(true);
                });
            var relocation = new Mock<IRootFolderRelocationService>();
            relocation.Setup(service => service.OnMoveJobStateChangedAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException(
                    cancellation.Token));
            var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            await service.UpdateJobStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Completed,
                cancellationToken: cancellation.Token);

            persistence.Verify(store => store.UpdateStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Completed,
                It.IsAny<MoveJobPhase>(),
                null,
                MoveFailureKind.None,
                It.IsAny<DateTimeOffset>(),
                cancellation.Token), Times.Once);
            broadcaster.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task UpdateJobStatus_InternalNotificationCancellationAfterCommit_RemainsSuccessful()
        {
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 42,
                Status = MoveJobStatus.Running,
                LeaseOwner = LeaseOwner,
                LeaseGeneration = 3,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1)
            };
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetByIdAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            persistence.Setup(store => store.UpdateStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Completed,
                    It.IsAny<MoveJobPhase>(),
                    null,
                    MoveFailureKind.None,
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var relocation = new Mock<IRootFolderRelocationService>();
            relocation.Setup(service => service.OnMoveJobStateChangedAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TaskCanceledException(
                    "Injected internal post-commit notification cancellation."));
            var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            await service.UpdateJobStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Completed);

            persistence.Verify(store => store.UpdateStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Completed,
                It.IsAny<MoveJobPhase>(),
                null,
                MoveFailureKind.None,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
            broadcaster.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task NotifyPersistedJobState_InternalError_BroadcastsPublicProjection()
        {
            const string secret = "C:\\private\\library\\database-secret";
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 42,
                Status = MoveJobStatus.NeedsAttention,
                FailureKind = MoveFailureKind.Verification,
                Error = secret,
                RequestedPath = "C:\\library\\target",
                UpdatedAt = DateTime.UtcNow
            };
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetByIdAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            var relocation = new Mock<IRootFolderRelocationService>();
            relocation.Setup(service => service.OnMoveJobStateChangedAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            object? payload = null;
            var broadcaster = new Mock<IHubBroadcaster>();
            broadcaster.Setup(service => service.BroadcastAsync(
                    "MoveJobUpdate",
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, object, CancellationToken>((_, value, _) =>
                    payload = value)
                .Returns(Task.CompletedTask);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            await service.NotifyPersistedJobStateAsync(
                job.Id,
                job.Status,
                job.Error);

            var update = Assert.IsType<MoveJobPublicUpdate>(payload);
            Assert.Equal(job.Id.ToString(), update.JobId);
            Assert.Equal(
                "The moved files could not be verified.",
                update.Error);
            Assert.DoesNotContain(secret, update.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void PublicProjection_CompletedRetainedMove_ReportsSourceRetention()
        {
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 42,
                Status = MoveJobStatus.Completed,
                SourceDirectoryCleanupState = MoveJobEntryCleanupState.Retained,
                Entries =
                [
                    new MoveJobEntry
                    {
                        EntryType = MoveJobEntryType.File,
                        RelativePath = "book.m4b",
                        CleanupState = MoveJobEntryCleanupState.Retained
                    }
                ]
            };

            var update = MoveJobPublicProjection.CreateUpdate(
                job.Id,
                job.Status,
                fallbackError: null,
                fallbackUpdatedAt: DateTime.UtcNow,
                persistedJob: job);

            Assert.True(update.SourceRetained);
        }

        [Fact]
        public async Task NotifyPersistedJobState_CanceledWaiter_ReleasesPublicationGateEntry()
        {
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 42,
                Status = MoveJobStatus.Running
            };
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetByIdAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            var relocation = new Mock<IRootFolderRelocationService>();
            relocation.Setup(service => service.OnMoveJobStateChangedAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var firstBroadcastEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstBroadcast = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var broadcaster = new Mock<IHubBroadcaster>();
            broadcaster.SetupSequence(service => service.BroadcastAsync(
                    "MoveJobUpdate",
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    firstBroadcastEntered.TrySetResult();
                    await releaseFirstBroadcast.Task;
                })
                .Returns(Task.CompletedTask);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            var first = service.NotifyPersistedJobStateAsync(
                job.Id,
                MoveJobStatus.Running);
            await firstBroadcastEntered.Task;
            using var cancellation = new CancellationTokenSource();
            var second = service.NotifyPersistedJobStateAsync(
                job.Id,
                MoveJobStatus.Running,
                cancellationToken: cancellation.Token);
            for (var attempt = 0;
                attempt < 100 && service.GetPublicationGateReferenceCount(job.Id) != 2;
                attempt++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(2, service.GetPublicationGateReferenceCount(job.Id));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            releaseFirstBroadcast.TrySetResult();
            await first;

            Assert.Equal(0, service.PublicationGateCount);
            broadcaster.Verify(service => service.BroadcastAsync(
                "MoveJobUpdate",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateJobStatus_PersistsAndUpdatesInMemory()
        {
            var dbOpts = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase("test_db_movejob_" + Guid.NewGuid().ToString("N"))
                .Options;
            var db = new ListenArrDbContext(dbOpts);

            var logger = new NullLogger<MoveQueueService>();
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                    db.MoveJobs.SingleOrDefault(job => job.ActiveDeduplicationKey == key));
            persistence.Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => db.MoveJobs.Find(id));
            persistence.Setup(store => store.GetRecoveryCandidatesByAudiobookAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int audiobookId, CancellationToken _) => db.MoveJobs
                    .Where(job => job.AudiobookId == audiobookId
                        && (job.Status == MoveJobStatus.Queued
                            || job.Status == MoveJobStatus.Running
                            || job.Status == MoveJobStatus.RetryScheduled
                            || job.Status == MoveJobStatus.Failed
                            || job.Status == MoveJobStatus.NeedsAttention))
                    .ToList());
            persistence.Setup(store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()))
                .Returns(async (MoveJob job, CancellationToken ct) =>
                {
                    db.MoveJobs.Add(job);
                    await db.SaveChangesAsync(ct);
                });
            persistence.Setup(store => store.UpdateStatusAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<MoveJobStatus>(),
                    It.IsAny<MoveJobPhase>(),
                    It.IsAny<string?>(),
                    It.IsAny<MoveFailureKind>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, string _, int _, MoveJobStatus status, MoveJobPhase phase, string? error, MoveFailureKind failureKind, DateTimeOffset updatedAt, CancellationToken ct) =>
                {
                    var persisted = await db.MoveJobs.FindAsync([id], ct);
                    if (persisted == null) return false;
                    persisted.Status = status;
                    persisted.Phase = phase;
                    persisted.Error = error;
                    persisted.FailureKind = failureKind;
                    persisted.UpdatedAt = updatedAt.UtcDateTime;
                    await db.SaveChangesAsync(ct);
                    return true;
                });

            var svc = new MoveQueueService(
                logger,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            // Enqueue a job (creates DB entry)
            var jobId = await svc.EnqueueMoveAsync(1, "C:\\dest\\path", "C:\\src\\path");

            // Initially the job should be queued
            var job1 = await svc.GetJobAsync(jobId);
            Assert.NotNull(job1);
            Assert.Equal(MoveJobStatus.Queued, job1!.Status);

            // Update status to Processing
            await svc.UpdateJobStatusAsync(jobId, LeaseOwner, 0, MoveJobStatus.Running);
            var job2 = await svc.GetJobAsync(jobId);
            Assert.NotNull(job2);
            Assert.Equal(MoveJobStatus.Running, job2!.Status);

            // Verify persisted in DB
            var dbJob = await db.MoveJobs.FindAsync(jobId);
            Assert.NotNull(dbJob);
            Assert.Equal(MoveJobStatus.Running, dbJob!.Status);
        }

        [Fact]
        public async Task GetJobAsync_PersistenceFailure_Propagates()
        {
            var jobId = Guid.NewGuid();
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetByIdAsync(
                    jobId,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PersistenceException(
                    "database unavailable",
                    new InvalidOperationException("provider secret")));
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            await Assert.ThrowsAsync<PersistenceException>(() =>
                service.GetJobAsync(jobId));
        }

        [Fact]
        public async Task EnqueueMoveAsync_UnresolvedIdentity_FailsClosedBeforePersisting()
        {
            var persistence = new Mock<IMoveQueuePersistence>();
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildNoIdentityResolver());

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueMoveAsync(7, "/library/book"));
            persistence.Verify(
                store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task EnqueueMoveAsync_OmittedSourcePath_RejectsIdenticalCompatibilityEndpoint()
        {
            var persistence = new Mock<IMoveQueuePersistence>();
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.EnqueueMoveAsync(7, "/library/book"));

            Assert.Contains("distinct", exception.Message, StringComparison.OrdinalIgnoreCase);
            persistence.Verify(
                store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Theory]
        [InlineData("target-source")]
        [InlineData("target-target")]
        [InlineData("source-source")]
        public async Task EnqueueMoveAsync_RelocationBoundaryConflict_FailsBeforePersisting(string conflictKind)
        {
            var requestedPath = conflictKind == "target-source" ? "/books/new-title" : "/books-new/new-title";
            var sourcePath = conflictKind == "source-source" ? "/books/old-title" : "/downloads/old-title";
            var protectedPath = FileSystemPathIdentity.ResolveNativeAbsolutePath(
                conflictKind == "source-source" ? sourcePath : requestedPath);
            var persistence = new Mock<IMoveQueuePersistence>();
            var relocation = new Mock<IRootFolderRelocationService>();
            var checkedPaths = new List<string>();
            relocation.Setup(service => service.IsBoundaryProtectedAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemPathSemantics, CancellationToken>((path, _, _) =>
                {
                    checkedPaths.Add(path);
                    return Task.FromResult(FileSystemPathIdentity.AreEquivalent(
                        path,
                        protectedPath,
                        FileSystemPathSemantics.CurrentHostDefault));
                });
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            var captured = await Record.ExceptionAsync(() =>
                service.EnqueueMoveAsync(7, requestedPath, sourcePath));
            var exception = Assert.IsType<MoveRelocationConflictException>(captured);

            Assert.Contains(
                conflictKind.StartsWith("source", StringComparison.Ordinal) ? "source" : "target",
                exception.Message);
            Assert.Contains(checkedPaths, path => FileSystemPathIdentity.AreEquivalent(
                path,
                protectedPath,
                FileSystemPathSemantics.CurrentHostDefault));
            persistence.Verify(
                store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task EnqueueMoveAsync_ConcurrentDuplicates_ReturnSingleJob()
        {
            var jobs = new List<MoveJob>();
            var sync = new object();
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                {
                    lock (sync)
                    {
                        return jobs.SingleOrDefault(job => job.ActiveDeduplicationKey == key);
                    }
                });
            persistence.Setup(store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()))
                .Returns((MoveJob job, CancellationToken _) =>
                {
                    lock (sync)
                    {
                        jobs.Add(job);
                    }

                    return Task.CompletedTask;
                });
            persistence.Setup(store => store.GetRecoveryCandidatesByAudiobookAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int audiobookId, CancellationToken _) =>
                {
                    lock (sync)
                    {
                        return jobs
                            .Where(job => job.AudiobookId == audiobookId
                                && job.Status is MoveJobStatus.Queued
                                    or MoveJobStatus.Running
                                    or MoveJobStatus.RetryScheduled
                                    or MoveJobStatus.Failed
                                    or MoveJobStatus.NeedsAttention)
                            .ToList();
                    }
                });
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(FileSystemCaseSensitivity.Insensitive));

            var ids = await Task.WhenAll(
                Enumerable.Range(0, 16)
                    .Select(_ => service.EnqueueMoveAsync(
                        7,
                        @"C:\Library\Book\",
                        @"C:\Downloads\Book\")));

            Assert.Single(ids.Distinct());
            Assert.Single(jobs);
            persistence.Verify(
                store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [LinuxFact]
        public async Task EnqueueMoveAsync_CaseDistinctDestinations_OnCaseSensitiveHost_CreateSeparateJobs()
        {

            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(9, "/library/Title", "/downloads/Title");
            var secondId = await service.EnqueueMoveAsync(9, "/library/title", "/downloads/Title");

            Assert.NotEqual(firstId, secondId);
            Assert.Equal(2, jobs.Count);
        }

        [Fact]
        public async Task EnqueueMoveAsync_TrailingWhitespaceDestination_IsDistinctFromTrimmedPath()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(9, "/library/Title ", "/downloads/Title");
            var secondId = await service.EnqueueMoveAsync(9, "/library/Title", "/downloads/Title");

            Assert.NotEqual(firstId, secondId);
            Assert.Equal(2, jobs.Count);
        }

        [Fact]
        public async Task EnqueueMoveAsync_DeleteEmptySourceFalse_PersistsCleanupChoice()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title",
                deleteEmptySource: false);

            var job = Assert.Single(jobs, candidate => candidate.Id == jobId);
            Assert.False(job.DeleteEmptySource);
        }

        [Fact]
        public async Task EnqueueMoveAsync_ForcedCopyRetention_DisablesDirectoryRetirementBeforePersistence()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title",
                deleteEmptySource: true,
                forceCopyAndRetainSource: true);

            var job = Assert.Single(jobs, candidate => candidate.Id == jobId);
            Assert.True(job.ForceCopyAndRetainSource);
            Assert.Equal(MoveSourceCleanupMode.RetainSource, job.SourceCleanupMode);
            Assert.False(job.DeleteEmptySource);
        }

        [Fact]
        public async Task EnqueueMoveAsync_SameMoveIdentityWithDifferentCleanupPolicy_ConflictsInsteadOfDeduplicating()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title",
                deleteEmptySource: false);

            var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnqueueMoveAsync(
                    9,
                    "/library/Title",
                    "/downloads/Title",
                    deleteEmptySource: true));

            Assert.Equal("move_active_options_conflict", conflict.Code);
            var first = Assert.Single(jobs, candidate => candidate.Id == firstId);
            Assert.False(first.DeleteEmptySource);
        }

        [Fact]
        public async Task EnqueueMoveAsync_SameMoveIdentityWithDifferentSourceBoundary_ConflictsInsteadOfDeduplicating()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title",
                deleteEmptySource: true,
                sourceCleanupBoundary: "/downloads");

            var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnqueueMoveAsync(
                    9,
                    "/library/Title",
                    "/downloads/Title",
                    deleteEmptySource: true,
                    sourceCleanupBoundary: "/"));

            Assert.Equal("move_active_options_conflict", conflict.Code);
            var first = Assert.Single(jobs, candidate => candidate.Id == firstId);
            Assert.Equal(
                FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads"),
                first.SourceCleanupBoundary);
        }

        [Fact]
        public async Task EnqueueMoveAsync_SameMoveIdentityWithDifferentRequestedSemanticsMode_ConflictsInsteadOfDeduplicating()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            var first = Assert.Single(jobs, candidate => candidate.Id == firstId);
            Assert.True(first.TryGetSourceIdentity(out var sourceIdentity));
            var explicitMode = sourceIdentity.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
                ? FileSystemCaseSensitivityMode.Insensitive
                : FileSystemCaseSensitivityMode.Sensitive;
            first.SetSourceIdentity(sourceIdentity with { RequestedMode = explicitMode });

            var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnqueueMoveAsync(
                    9,
                    "/library/Title",
                    "/downloads/Title"));

            Assert.Equal("move_active_options_conflict", conflict.Code);
            Assert.Single(jobs);
        }

        [Fact]
        public async Task EnqueueMoveAsync_SameMoveIdentityWithDifferentIdentityBoundary_ConflictsInsteadOfDeduplicating()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            var first = Assert.Single(jobs, candidate => candidate.Id == firstId);
            Assert.True(first.TryGetSourceIdentity(out var sourceIdentity));
            var broaderBoundary = Path.GetDirectoryName(first.SourcePath!)
                ?? throw new InvalidOperationException("Test source parent is unavailable.");
            first.SetSourceIdentity(sourceIdentity with { BoundaryPath = broaderBoundary });

            var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.EnqueueMoveAsync(
                    9,
                    "/library/Title",
                    "/downloads/Title"));

            Assert.Equal("move_active_options_conflict", conflict.Code);
            Assert.Single(jobs);
        }

        [Fact]
        public async Task EnqueueMoveAsync_SourceCleanupBoundary_PersistsWithJob()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Author/Title",
                deleteEmptySource: true,
                sourceCleanupBoundary: "/downloads");

            var job = Assert.Single(jobs, candidate => candidate.Id == jobId);
            Assert.Equal(
                FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads"),
                job.SourceCleanupBoundary);
        }

        [Fact]
        public async Task EnqueueMoveAsync_PersistedActiveJob_SchedulesExistingJob()
        {
            var existingJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/Title"),
                RequestedPath = FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/Title"),
                ActiveDeduplicationKey = "9:/library/Title",
                Status = MoveJobStatus.Queued,
                DeleteEmptySource = true,
                SourceCleanupBoundary = FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads")
            };
            var semantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive);
            existingJob.SetSourceIdentity(PathIdentitySnapshot.FromResolution(
                semantics,
                FileSystemCaseSensitivityMode.Auto,
                existingJob.SourcePath!,
                existingJob.SourcePath!));
            existingJob.SetTargetIdentity(PathIdentitySnapshot.FromResolution(
                semantics,
                FileSystemCaseSensitivityMode.Auto,
                existingJob.RequestedPath!,
                existingJob.RequestedPath!));
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveByKeyAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingJob);
            persistence.Setup(store => store.GetRecoveryCandidatesByAudiobookAsync(
                    existingJob.AudiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([existingJob]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var jobId = await service.EnqueueMoveAsync(9, "/library/Title", "/downloads/Title");

            Assert.Equal(existingJob.Id, jobId);
            Assert.True(service.Reader.TryRead(out var scheduledJob));
            Assert.Equal(existingJob.Id, scheduledJob.Id);
        }

        [Fact]
        public async Task EnqueueMoveAsync_RequestCancelledAfterDurableWriteStillPublishesJob()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            using var cancellation = new CancellationTokenSource();
            persistence.Setup(store => store.AddAsync(
                    It.IsAny<MoveJob>(),
                    It.IsAny<CancellationToken>()))
                .Returns((MoveJob job, CancellationToken commitToken) =>
                {
                    jobs.Add(job);
                    cancellation.Cancel();
                    commitToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());
            var source = Path.GetFullPath(Path.Join(
                Path.GetTempPath(),
                "listenarr-post-commit-source",
                Guid.NewGuid().ToString("N")));
            var target = Path.GetFullPath(Path.Join(
                Path.GetTempPath(),
                "listenarr-post-commit-target",
                Guid.NewGuid().ToString("N")));
            var boundary = Path.GetPathRoot(source)
                ?? throw new InvalidOperationException("Test path root unavailable.");
            var semantics = FileSystemPathSemantics.CurrentHostDefault;

            var jobId = await service.EnqueueMoveAsync(
                new MoveEnqueueCommand(
                    9,
                    source,
                    PathIdentitySnapshot.FromResolution(
                        semantics,
                        FileSystemCaseSensitivityMode.Auto,
                        boundary,
                        source),
                    [new MoveSourceManifestEntry(
                        "book.m4b",
                        MoveJobEntryType.File,
                        1,
                        DateTime.UnixEpoch,
                        new string('A', 64))],
                    target,
                    PathIdentitySnapshot.FromResolution(
                        semantics,
                        FileSystemCaseSensitivityMode.Auto,
                        boundary,
                        target),
                    SourceBoundaryDirectoryObjectIdentityVersion: 2,
                    SourceBoundaryDirectoryObjectIdentity: "test-source-boundary-identity",
                    TargetBoundaryDirectoryObjectIdentityVersion: 2,
                    TargetBoundaryDirectoryObjectIdentity: "test-target-boundary-identity",
                    DeleteEmptySource: true,
                    SourceCleanupBoundary: boundary),
                cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(jobId, Assert.Single(jobs).Id);
            Assert.True(service.Reader.TryRead(out var scheduled));
            Assert.Equal(jobId, scheduled.Id);
            persistence.Verify(store => store.AddAsync(
                It.IsAny<MoveJob>(),
                It.Is<CancellationToken>(token => !token.CanBeCanceled)), Times.Once);
        }

        [Fact]
        public async Task EnqueueMoveAsync_InternalNotificationCancellationAfterCommit_ReturnsJobId()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var relocation = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
            relocation.Setup(service => service.IsAudiobookPathStateProtectedAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            relocation.Setup(service => service.IsBoundaryProtectedAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            relocation.Setup(service => service.OnMoveJobStateChangedAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
            broadcaster.Setup(service => service.BroadcastAsync(
                    "MoveJobUpdate",
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TaskCanceledException(
                    "Injected post-commit move notification cancellation."));
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");

            Assert.Equal(jobId, Assert.Single(jobs).Id);
            Assert.True(service.Reader.TryRead(out var scheduled));
            Assert.Equal(jobId, scheduled.Id);
            broadcaster.Verify(service => service.BroadcastAsync(
                "MoveJobUpdate",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RequeueMoveAsync_FailedJob_ReusesRecoveryIdentity()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());
            var jobId = await service.EnqueueMoveAsync(9, "/library/Title", "/downloads/Title");
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(jobId, LeaseOwner, 0, MoveJobStatus.Failed, "copy interrupted");

            var requeuedJobId = await service.RequeueMoveAsync(jobId);

            Assert.Equal(jobId, requeuedJobId);
            Assert.True(service.Reader.TryRead(out var scheduledJob));
            Assert.Equal(jobId, scheduledJob.Id);
        }

        [Fact]
        public async Task RequeueMoveAsync_ActiveDeletionRecovery_BlocksBeforeDurableRequeue()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var deletionProbe = new Mock<IAudiobookDeletionIntentProbe>(MockBehavior.Strict);
            deletionProbe.SetupSequence(probe => probe.HasActiveAsync(
                    9,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false)
                .ReturnsAsync(false)
                .ReturnsAsync(true);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                deletionIntentProbe: deletionProbe.Object);
            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(
                jobId,
                LeaseOwner,
                0,
                MoveJobStatus.Failed,
                "copy interrupted");

            var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.RequeueMoveAsync(jobId));

            Assert.Equal("delete_recovery_pending", conflict.Code);
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
            Assert.False(service.Reader.TryRead(out _));
        }

        [Fact]
        public async Task RequeueMoveAsync_ActiveRegistrationRecovery_BlocksBeforeDurableRequeue()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var registrationProbe = new Mock<IFileRegistrationRecoveryProbe>(MockBehavior.Strict);
            registrationProbe.SetupSequence(probe => probe.HasBlockingAsync(
                    9,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false)
                .ReturnsAsync(false)
                .ReturnsAsync(true);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                fileRegistrationRecoveryProbe: registrationProbe.Object);
            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(
                jobId,
                LeaseOwner,
                0,
                MoveJobStatus.Failed,
                "copy interrupted");

            var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.RequeueMoveAsync(jobId));

            Assert.Equal("registration_recovery_pending", conflict.Code);
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
            Assert.False(service.Reader.TryRead(out _));
        }

        [Fact]
        public async Task RequeueMoveAsync_ActiveRenameRecovery_BlocksBeforeDurableRequeue()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var renameProbe = new Mock<IFileRenameRecoveryProbe>(MockBehavior.Strict);
            renameProbe.SetupSequence(probe => probe.HasBlockingAsync(
                    9,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false)
                .ReturnsAsync(false)
                .ReturnsAsync(true);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                fileRenameRecoveryProbe: renameProbe.Object);
            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(
                jobId,
                LeaseOwner,
                0,
                MoveJobStatus.Failed,
                "copy interrupted");

            var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.RequeueMoveAsync(jobId));

            Assert.Equal("rename_recovery_pending", conflict.Code);
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
            Assert.False(service.Reader.TryRead(out _));
        }

        [Fact]
        public async Task RequeueMoveAsync_ConflictingActiveJobWithDifferentCleanupPolicy_DoesNotAdoptConflictingJob()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());
            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title",
                deleteEmptySource: true);
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(
                jobId,
                LeaseOwner,
                0,
                MoveJobStatus.Failed,
                "copy interrupted");
            var original = Assert.Single(jobs, candidate => candidate.Id == jobId);
            var conflicting = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = original.AudiobookId,
                SourcePath = original.SourcePath,
                RequestedPath = original.RequestedPath,
                Status = MoveJobStatus.Queued,
                DeleteEmptySource = false,
                SourceCleanupBoundary = original.SourceCleanupBoundary
            };
            persistence.Setup(store => store.RequeueAsync(
                    It.IsAny<RequeueMoveCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MoveRequeueResult(
                    MoveRequeueOutcome.ConflictingActiveJob,
                    conflicting));

            var requeuedJobId = await service.RequeueMoveAsync(jobId);

            Assert.Null(requeuedJobId);
            Assert.False(service.Reader.TryRead(out _));
            Assert.Equal(MoveJobStatus.Failed, original.Status);
        }

        [Fact]
        public async Task RequeueMoveAsync_RequestCancelledAfterDurableWriteStillPublishesJob()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());
            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(
                jobId,
                LeaseOwner,
                0,
                MoveJobStatus.Failed,
                "copy interrupted");
            using var cancellation = new CancellationTokenSource();
            persistence.Setup(store => store.RequeueAsync(
                    It.IsAny<RequeueMoveCommand>(),
                    It.IsAny<CancellationToken>()))
                .Returns((RequeueMoveCommand command, CancellationToken commitToken) =>
                {
                    var job = jobs.Single(candidate => candidate.Id == command.JobId);
                    job.Status = MoveJobStatus.Queued;
                    job.Error = null;
                    job.FailureKind = MoveFailureKind.None;
                    job.ActiveDeduplicationKey = command.DeduplicationKey;
                    job.SourcePath = command.SourcePath;
                    job.RequestedPath = command.TargetPath;
                    job.SetSourceIdentity(command.SourceIdentity);
                    job.SetTargetIdentity(command.TargetIdentity);
                    cancellation.Cancel();
                    commitToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new MoveRequeueResult(
                        MoveRequeueOutcome.Requeued,
                        job));
                });

            var requeued = await service.RequeueMoveAsync(
                jobId,
                cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(jobId, requeued);
            Assert.True(service.Reader.TryRead(out var scheduled));
            Assert.Equal(jobId, scheduled.Id);
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.Is<CancellationToken>(token => !token.CanBeCanceled)), Times.Once);
        }

        [Fact]
        public async Task RequeueMoveAsync_InternalNotificationCancellationAfterCommit_ReturnsJobId()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var relocation = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
            relocation.Setup(service => service.IsAudiobookPathStateProtectedAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            relocation.Setup(service => service.IsBoundaryProtectedAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            relocation.Setup(service => service.OnMoveJobStateChangedAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var cancelBroadcast = false;
            var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
            broadcaster.Setup(service => service.BroadcastAsync(
                    "MoveJobUpdate",
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() => cancelBroadcast
                    ? Task.FromException(new TaskCanceledException(
                        "Injected post-commit requeue notification cancellation."))
                    : Task.CompletedTask);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);
            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(
                jobId,
                LeaseOwner,
                0,
                MoveJobStatus.Failed,
                "copy interrupted");
            cancelBroadcast = true;

            var requeued = await service.RequeueMoveAsync(jobId);

            Assert.Equal(jobId, requeued);
            Assert.True(service.Reader.TryRead(out var scheduled));
            Assert.Equal(jobId, scheduled.Id);
            broadcaster.Verify(service => service.BroadcastAsync(
                "MoveJobUpdate",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task RequeueMoveAsync_CancelledWhileWaitingForMutationCoordinatorDoesNotRequeue()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var coordinator = new FilesystemMutationCoordinator();
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                mutationCoordinator: coordinator);
            var jobId = await service.EnqueueMoveAsync(
                9,
                "/library/Title",
                "/downloads/Title");
            Assert.True(service.Reader.TryRead(out _));
            await service.UpdateJobStatusAsync(
                jobId,
                LeaseOwner,
                0,
                MoveJobStatus.Failed,
                "copy interrupted");
            var lockEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;
            using var cancellation = new CancellationTokenSource();

            var requeueTask = service.RequeueMoveAsync(jobId, cancellation.Token);
            await Task.Delay(50);
            Assert.False(requeueTask.IsCompleted);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requeueTask);
            releaseLock.SetResult();
            await lockTask;
            Assert.Equal(MoveJobStatus.Failed, jobs.Single().Status);
            Assert.False(service.Reader.TryRead(out _));
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RequeueMoveAsync_FailedJob_ResetsRetryStateAndPreservesRecoveryPhase()
        {
            var future = DateTimeOffset.UtcNow.AddHours(1);
            var sourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-requeue-source", "Title"));
            var targetPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-requeue-target", "Title"));
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Failed,
                Phase = MoveJobPhase.CleaningSource,
                SourceCleanupBoundary = Path.GetDirectoryName(sourcePath),
                Error = "verification failed",
                FailureKind = MoveFailureKind.Verification,
                AttemptCount = MoveTimingPolicy.MaxTransientAttempts,
                NextAttemptAt = future.UtcDateTime,
                LeaseOwner = "worker",
                LeaseExpiresAt = future.UtcDateTime,
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "book.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 1,
                        LastWriteTimeUtc = DateTime.UnixEpoch,
                        Sha256 = new string('A', 64)
                    },
                    MoveManifestIdentity.CreateSourceBoundaryAuthorization(
                        2,
                        "test-source-generation"),
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        2,
                        "test-target-generation")
                ]
            };
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Equal(job.Id, requeuedJobId);
            Assert.Equal(MoveJobStatus.Queued, job.Status);
            Assert.Equal(MoveJobPhase.CleaningSource, job.Phase);
            Assert.Null(job.Error);
            Assert.Equal(MoveFailureKind.None, job.FailureKind);
            Assert.Equal(0, job.AttemptCount);
            Assert.Null(job.NextAttemptAt);
            Assert.Null(job.LeaseOwner);
            Assert.Null(job.LeaseExpiresAt);
            Assert.NotNull(job.ActiveDeduplicationKey);
            persistence.Verify(store => store.RequeueAsync(
                It.Is<RequeueMoveCommand>(command =>
                    command.JobId == job.Id
                    && command.ExpectedStatus == MoveJobStatus.Failed
                    && command.SourcePath == sourcePath
                    && command.TargetPath == targetPath
                    && !string.IsNullOrWhiteSpace(command.DeduplicationKey)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RequeueMoveAsync_SourceMutationBoundaryOutsideSourceParent_RequiresAttention()
        {
            var tempRoot = Path.GetFullPath(Path.Join(
                Path.GetTempPath(),
                $"listenarr-requeue-boundary-{Guid.NewGuid():N}"));
            var sourcePath = Path.Join(tempRoot, "source", "Title");
            var targetPath = Path.Join(tempRoot, "target", "Title");
            var invalidBoundary = Path.Join(tempRoot, "unrelated");
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var sourceIdentity = new PathIdentitySnapshot(
                semantics.Syntax,
                semantics.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                tempRoot);
            var targetIdentity = sourceIdentity;
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                SourceCleanupBoundary = invalidBoundary,
                DeleteEmptySource = true,
                Status = MoveJobStatus.Failed,
                Phase = MoveJobPhase.Copying,
                FailureKind = MoveFailureKind.Transient,
                Error = "Simulated transient failure.",
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "book.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 1,
                        LastWriteTimeUtc = DateTime.UnixEpoch,
                        Sha256 = new string('A', 64)
                    },
                    MoveManifestIdentity.CreateSourceBoundaryAuthorization(
                        2,
                        "test-source-generation"),
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        2,
                        "test-target-generation")
                ]
            };
            job.SetSourceIdentity(sourceIdentity);
            job.SetTargetIdentity(targetIdentity);
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Equal(MoveFailureKind.Verification, job.FailureKind);
            Assert.Null(job.ActiveDeduplicationKey);
            Assert.Contains(
                "source mutation boundary",
                job.Error ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData(MoveJobEntryCleanupState.Deleted)]
        [InlineData(MoveJobEntryCleanupState.Retained)]
        public async Task RequeueMoveAsync_MarkerlessNeedsAttentionUnknownWithCompletedRecoveryEvidence_Requeues(
            MoveJobEntryCleanupState completedCleanupState)
        {
            var sourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-recoverable-source", "Title"));
            var targetPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-recoverable-target", "Title"));
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var identity = new PathIdentitySnapshot(
                semantics.Syntax,
                semantics.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                Path.GetFullPath(Path.GetTempPath()));
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.NeedsAttention,
                Phase = MoveJobPhase.Published,
                ExecutionProtocolVersion = MoveExecutionProtocol.MarkerlessDatabaseState,
                SourceDirectoryCleanupState = completedCleanupState,
                TargetDirectoryObjectIdentity = "target-generation",
                FailureKind = MoveFailureKind.Unknown,
                Error = "A prior build could not reconcile stale target ownership.",
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "book.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 1,
                        LastWriteTimeUtc = DateTime.UnixEpoch,
                        Sha256 = new string('A', 64),
                        CopyState = MoveJobEntryCopyState.Verified,
                        CleanupState = completedCleanupState
                    },
                    MoveManifestIdentity.CreateSourceBoundaryAuthorization(
                        2,
                        "test-source-generation"),
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        2,
                        "test-target-generation")
                ],
                CreatedDirectories =
                [
                    new MoveJobCreatedDirectory
                    {
                        Path = targetPath,
                        State = MoveCreatedDirectoryState.Created,
                        DirectoryObjectIdentity = "target-generation"
                    }
                ]
            };
            job.SetSourceIdentity(identity);
            job.SetTargetIdentity(identity);
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Equal(job.Id, requeuedJobId);
            Assert.Equal(MoveJobStatus.Queued, job.Status);
            Assert.Equal(MoveJobPhase.Published, job.Phase);
            Assert.Equal(MoveFailureKind.None, job.FailureKind);
            Assert.Null(job.Error);
            persistence.Verify(store => store.RequeueAsync(
                It.Is<RequeueMoveCommand>(command =>
                    command.JobId == job.Id
                    && command.ExpectedStatus == MoveJobStatus.NeedsAttention),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RequeueMoveAsync_NeedsAttentionVerificationWithValidEvidence_RemainsOperatorRepairOnly()
        {
            var sourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-repair-source", "Title"));
            var targetPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-repair-target", "Title"));
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.NeedsAttention,
                Phase = MoveJobPhase.CleaningSource,
                FailureKind = MoveFailureKind.Verification,
                Error = "The persisted filesystem generation changed.",
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "book.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 1,
                        LastWriteTimeUtc = DateTime.UnixEpoch,
                        Sha256 = new string('A', 64),
                        CopyState = MoveJobEntryCopyState.Verified,
                        CleanupState = MoveJobEntryCleanupState.DeleteAuthorized
                    },
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        2,
                        "test-target-generation")
                ]
            };
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Equal(MoveFailureKind.Verification, job.FailureKind);
            Assert.Equal("The persisted filesystem generation changed.", job.Error);
            Assert.False(service.Reader.TryRead(out _));
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData(nameof(MoveJobStatus.Failed))]
        [InlineData(nameof(MoveJobStatus.NeedsAttention))]
        [InlineData(nameof(MoveJobStatus.Queued))]
        public async Task RequeueMoveAsync_LegacyJobWithoutManifest_RequiresAttention(
            string statusName)
        {
            var status = Enum.Parse<MoveJobStatus>(statusName);
            var sourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-legacy-source", "Legacy Title"));
            var targetPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-legacy-target", "Legacy Title"));
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = status,
                ActiveDeduplicationKey = status.IsActive() ? "legacy-active" : null
            };
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains(
                "no persisted tracked-file source manifest",
                job.Error ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(service.Reader.TryRead(out _));
        }

        [Fact]
        public async Task RequeueMoveAsync_ForeignLegacyPaths_ArePreservedAndRequireAttention()
        {
            var sourcePath = OperatingSystem.IsWindows()
                ? "/downloads/Foreign Title"
                : @"C:\Downloads\Foreign Title";
            var targetPath = OperatingSystem.IsWindows()
                ? "/library/Foreign Title"
                : @"C:\Library\Foreign Title";
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Failed,
                ActiveDeduplicationKey = "legacy-foreign"
            };
            var persistence = CreateInMemoryPersistence([job]);
            var broadcaster = new Mock<IHubBroadcaster>();
            broadcaster
                .Setup(service => service.BroadcastAsync(
                    "MoveJobUpdate",
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                broadcaster.Object,
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(sourcePath, job.SourcePath);
            Assert.Equal(targetPath, job.RequestedPath);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Equal(MoveFailureKind.Verification, job.FailureKind);
            Assert.Contains("filesystem syntax", job.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
            Assert.False(job.TryGetSourceIdentity(out _));
            Assert.False(job.TryGetTargetIdentity(out _));
            Assert.False(service.Reader.TryRead(out _));
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
            broadcaster.Verify(service => service.BroadcastAsync(
                "MoveJobUpdate",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RequeueMoveAsync_ForeignPersistedIdentity_IsPreservedAndRequiresAttention()
        {
            var syntax = OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Unix
                : FileSystemPathSyntax.Windows;
            var sourcePath = syntax == FileSystemPathSyntax.Windows
                ? @"C:\Downloads\Foreign Identity Title"
                : "/downloads/Foreign Identity Title";
            var targetPath = syntax == FileSystemPathSyntax.Windows
                ? @"C:\Library\Foreign Identity Title"
                : "/library/Foreign Identity Title";
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Failed,
                ActiveDeduplicationKey = "foreign-identity"
            };
            job.SetSourceIdentity(new PathIdentitySnapshot(
                syntax,
                FileSystemCaseSensitivity.Insensitive,
                FileSystemCaseSensitivityMode.Insensitive,
                syntax == FileSystemPathSyntax.Windows ? @"C:\Downloads" : "/downloads"));
            job.SetTargetIdentity(new PathIdentitySnapshot(
                syntax,
                FileSystemCaseSensitivity.Insensitive,
                FileSystemCaseSensitivityMode.Insensitive,
                syntax == FileSystemPathSyntax.Windows ? @"C:\Library" : "/library"));
            job.IdentityKeyVersion = MoveManifestIdentity.Version;
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(sourcePath, job.SourcePath);
            Assert.Equal(targetPath, job.RequestedPath);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains("persisted identity", job.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
            Assert.False(service.Reader.TryRead(out _));
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RequeueMoveAsync_RelativeLegacyPath_IsPreservedAndRequiresAttention()
        {
            const string sourcePath = "downloads/Relative Title";
            var targetPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-relative-target", "Title"));
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Failed,
                ActiveDeduplicationKey = "legacy-relative"
            };
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(sourcePath, job.SourcePath);
            Assert.Equal(targetPath, job.RequestedPath);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains("not absolute", job.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
            Assert.False(service.Reader.TryRead(out _));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RequeueMoveAsync_LegacyNavigationSegment_IsPreservedAndRequiresAttention(
            bool invalidSource)
        {
            var validSource = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-navigation-source", "Title"));
            var validTarget = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-navigation-target", "Title"));
            var invalidPath = OperatingSystem.IsWindows()
                ? @"C:\Listenarr\Source\..\Title"
                : "/listenarr/source/../Title";
            var sourcePath = invalidSource ? invalidPath : validSource;
            var targetPath = invalidSource ? validTarget : invalidPath;
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Failed,
                ActiveDeduplicationKey = "legacy-navigation"
            };
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(sourcePath, job.SourcePath);
            Assert.Equal(targetPath, job.RequestedPath);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains(invalidSource ? "source" : "target", job.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("navigation segment", job.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
            Assert.False(service.Reader.TryRead(out _));
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RequeueMoveAsync_IdentityBearingNavigationSegment_IsPreservedAndRequiresAttention()
        {
            var boundary = OperatingSystem.IsWindows() ? @"C:\Listenarr" : "/listenarr";
            var sourcePath = OperatingSystem.IsWindows()
                ? @"C:\Listenarr\Source\..\Title"
                : "/listenarr/source/../Title";
            var targetPath = OperatingSystem.IsWindows()
                ? @"C:\Listenarr\Target\Title"
                : "/listenarr/target/Title";
            var syntax = OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Windows
                : FileSystemPathSyntax.Unix;
            var sensitivity = OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Failed,
                ActiveDeduplicationKey = "identity-navigation"
            };
            job.SetSourceIdentity(new PathIdentitySnapshot(
                syntax,
                sensitivity,
                FileSystemCaseSensitivityMode.Auto,
                boundary));
            job.SetTargetIdentity(new PathIdentitySnapshot(
                syntax,
                sensitivity,
                FileSystemCaseSensitivityMode.Auto,
                boundary));
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(sourcePath, job.SourcePath);
            Assert.Equal(targetPath, job.RequestedPath);
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains("navigation segment", job.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
            Assert.False(service.Reader.TryRead(out _));
        }

        [Fact]
        public async Task RequeueMoveAsync_LegacyIdenticalEndpoint_RejectsWithoutMutationOrScheduling()
        {
            var path = FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/Title");
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = path,
                RequestedPath = path,
                Status = MoveJobStatus.Failed,
                ActiveDeduplicationKey = "legacy-identical"
            };
            var persistence = CreateInMemoryPersistence([job]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var requeuedJobId = await service.RequeueMoveAsync(job.Id);

            Assert.Null(requeuedJobId);
            Assert.Equal(MoveJobStatus.Failed, job.Status);
            Assert.Null(job.Error);
            Assert.Equal("legacy-identical", job.ActiveDeduplicationKey);
            Assert.False(service.Reader.TryRead(out _));
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RequeuedRecoveryJob_MarkedRunning_PreservesRecoveryPhase()
        {
            var item = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = "/downloads/Title",
                RequestedPath = "/library/Title",
                Status = MoveJobStatus.Failed,
                Phase = MoveJobPhase.CleaningSource,
                FailureKind = MoveFailureKind.Verification
            };
            var persistence = CreateInMemoryPersistence([item]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            await service.RequeueMoveAsync(item.Id);
            await service.UpdateJobStatusAsync(item.Id, LeaseOwner, 0, MoveJobStatus.Running);

            Assert.Equal(MoveJobStatus.Running, item.Status);
            Assert.Equal(MoveJobPhase.CleaningSource, item.Phase);
        }

        [Fact]
        public async Task RequeueMoveAsync_RelocationBoundaryConflict_DoesNotRequeue()
        {
            var job = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                SourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-protected-source", "Title")),
                RequestedPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-protected-target", "Title")),
                Status = MoveJobStatus.Failed
            };
            var persistence = CreateInMemoryPersistence([job]);
            var relocation = new Mock<IRootFolderRelocationService>();
            var protectedTarget = FileSystemPathIdentity.ResolveNativeAbsolutePath(job.RequestedPath);
            relocation.Setup(service => service.IsBoundaryProtectedAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemPathSemantics, CancellationToken>((path, _, _) =>
                    Task.FromResult(FileSystemPathIdentity.AreEquivalent(
                        path,
                        protectedTarget,
                        FileSystemPathSemantics.CurrentHostDefault)));
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver(),
                relocation.Object);

            await Assert.ThrowsAsync<MoveRelocationConflictException>(() => service.RequeueMoveAsync(job.Id));

            Assert.Equal(MoveJobStatus.Failed, job.Status);
            Assert.False(service.Reader.TryRead(out _));
            persistence.Verify(store => store.RequeueAsync(
                It.IsAny<RequeueMoveCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RecoverActiveJobsAsync_SchedulesPersistedProcessingJob()
        {
            var persistedJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = 9,
                RequestedPath = "/library/Title",
                ActiveDeduplicationKey = "9:/library/Title",
                Status = MoveJobStatus.Running
            };
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([persistedJob]);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            await service.RecoverActiveJobsAsync();

            Assert.True(service.Reader.TryRead(out var scheduledJob));
            Assert.Equal(persistedJob.Id, scheduledJob.Id);
        }

        [Fact]
        public async Task TerminalStatus_ReleasesDeduplicationKey_ForLaterMove()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System,
                BuildSemanticsResolver());

            var firstId = await service.EnqueueMoveAsync(9, "/library/book", "/downloads/book");
            await service.UpdateJobStatusAsync(firstId, LeaseOwner, 0, MoveJobStatus.Completed);
            var secondId = await service.EnqueueMoveAsync(9, "/library/book/", "/downloads/book");

            Assert.NotEqual(firstId, secondId);
            Assert.Equal(2, jobs.Count);
            Assert.Null(jobs.Single(job => job.Id == firstId).ActiveDeduplicationKey);
            Assert.NotNull(jobs.Single(job => job.Id == secondId).ActiveDeduplicationKey);
        }

        private static IFileSystemSemanticsResolver BuildSemanticsResolver(FileSystemCaseSensitivity caseSensitivity = FileSystemCaseSensitivity.Sensitive)
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, caseSensitivity),
                        PathIdentityState.Valid,
                        path)));
            return resolver.Object;
        }

        private static IFileSystemSemanticsResolver BuildNoIdentityResolver()
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, FileSystemCaseSensitivity.Sensitive),
                        PathIdentityState.Unavailable,
                        path,
                        "identity probe failed")));
            return resolver.Object;
        }

        private static Mock<IMoveQueuePersistence> CreateInMemoryPersistence(List<MoveJob> jobs)
        {
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                    jobs.SingleOrDefault(job => job.ActiveDeduplicationKey == key));
            persistence.Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => jobs.SingleOrDefault(job => job.Id == id));
            persistence.Setup(store => store.GetRecoveryCandidatesByAudiobookAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int audiobookId, CancellationToken _) => jobs
                    .Where(job => job.AudiobookId == audiobookId
                        && job.Status is MoveJobStatus.Queued
                            or MoveJobStatus.Running
                            or MoveJobStatus.RetryScheduled
                            or MoveJobStatus.Failed
                            or MoveJobStatus.NeedsAttention)
                    .ToList());
            persistence.Setup(store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()))
                .Returns((MoveJob job, CancellationToken _) =>
                {
                    jobs.Add(job);
                    return Task.CompletedTask;
                });
            persistence.Setup(store => store.MarkNeedsAttentionAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<MoveJobStatus>(),
                    It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, MoveJobStatus expectedStatus, string error, DateTimeOffset updatedAt, CancellationToken _) =>
                {
                    var job = jobs.Single(candidate => candidate.Id == id);
                    if (job.Status != expectedStatus)
                    {
                        return Task.FromResult(false);
                    }

                    job.Status = MoveJobStatus.NeedsAttention;
                    job.Error = error;
                    job.FailureKind = MoveFailureKind.Verification;
                    job.ActiveDeduplicationKey = null;
                    job.LeaseOwner = null;
                    job.LeaseExpiresAt = null;
                    job.UpdatedAt = updatedAt.UtcDateTime;
                    return Task.FromResult(true);
                });
            persistence.Setup(store => store.RequeueAsync(
                    It.IsAny<RequeueMoveCommand>(),
                    It.IsAny<CancellationToken>()))
                .Returns((RequeueMoveCommand command, CancellationToken _) =>
                {
                    var job = jobs.Single(candidate => candidate.Id == command.JobId);
                    if (job.Status != command.ExpectedStatus)
                    {
                        return Task.FromResult(new MoveRequeueResult(
                            MoveRequeueOutcome.StaleState,
                            job));
                    }

                    var conflicting = jobs.SingleOrDefault(candidate =>
                        candidate.Id != command.JobId
                        && candidate.ActiveDeduplicationKey == command.DeduplicationKey);
                    if (conflicting != null)
                    {
                        return Task.FromResult(new MoveRequeueResult(
                            MoveRequeueOutcome.ConflictingActiveJob,
                            conflicting));
                    }

                    job.SourcePath = command.SourcePath;
                    job.RequestedPath = command.TargetPath;
                    job.SetSourceIdentity(command.SourceIdentity);
                    job.SetTargetIdentity(command.TargetIdentity);
                    job.IdentityKeyVersion = MoveManifestIdentity.Version;
                    job.Status = MoveJobStatus.Queued;
                    job.Error = null;
                    job.FailureKind = MoveFailureKind.None;
                    job.AttemptCount = 0;
                    job.NextAttemptAt = null;
                    job.LeaseOwner = null;
                    job.LeaseExpiresAt = null;
                    job.UpdatedAt = command.UpdatedAt.UtcDateTime;
                    job.ActiveDeduplicationKey = command.DeduplicationKey;
                    return Task.FromResult(new MoveRequeueResult(
                        MoveRequeueOutcome.Requeued,
                        job));
                });
            persistence.Setup(store => store.UpdateStatusAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<MoveJobStatus>(),
                    It.IsAny<MoveJobPhase>(),
                    It.IsAny<string?>(),
                    It.IsAny<MoveFailureKind>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, string _, int _, MoveJobStatus status, MoveJobPhase phase, string? error, MoveFailureKind failureKind, DateTimeOffset updatedAt, CancellationToken _) =>
                {
                    var job = jobs.Single(candidate => candidate.Id == id);
                    job.Status = status;
                    job.Phase = phase;
                    job.Error = error;
                    job.FailureKind = failureKind;
                    job.UpdatedAt = updatedAt.UtcDateTime;
                    if (!status.IsActive())
                    {
                        job.ActiveDeduplicationKey = null;
                    }

                    return Task.FromResult(true);
                });
            return persistence;
        }
    }

    internal sealed class MoveQueueServiceTestAdapter : AppMoveQueueService
    {
        private readonly IFileSystemSemanticsResolver _semanticsResolver;

        public MoveQueueServiceTestAdapter(
            ILogger<MoveQueueService> logger,
            IMoveQueuePersistence persistence,
            IHubBroadcaster hubBroadcaster,
            TimeProvider timeProvider,
            IFileSystemSemanticsResolver semanticsResolver,
            IRootFolderRelocationService? relocationService = null,
            IFilesystemMutationCoordinator? mutationCoordinator = null,
            IAudiobookDeletionIntentProbe? deletionIntentProbe = null,
            IFileRegistrationRecoveryProbe? fileRegistrationRecoveryProbe = null,
            IFileRenameRecoveryProbe? fileRenameRecoveryProbe = null)
            : base(
                NullLogger<AppMoveQueueService>.Instance,
                persistence,
                hubBroadcaster,
                timeProvider,
                semanticsResolver,
                relocationService ?? Mock.Of<IRootFolderRelocationService>(),
                mutationCoordinator ?? new FilesystemMutationCoordinator(),
                deletionIntentProbe,
                fileRegistrationRecoveryProbe,
                fileRenameRecoveryProbe)
        {
            _semanticsResolver = semanticsResolver;
        }

        public async Task<Guid> EnqueueMoveAsync(
            int audiobookId,
            string requestedPath,
            string? sourcePath = null,
            bool deleteEmptySource = true,
            string? sourceCleanupBoundary = null,
            MoveSourceCleanupMode sourceCleanupMode = MoveSourceCleanupMode.RetainSource,
            bool forceCopyAndRetainSource = false)
        {
            var target = FileSystemPathIdentity.ResolveNativeAbsolutePath(requestedPath);
            var source = FileSystemPathIdentity.ResolveNativeAbsolutePath(
                sourcePath ?? requestedPath);
            var sourceResolution = await _semanticsResolver.ResolveAsync(
                source,
                FileSystemCaseSensitivityMode.Auto,
                CancellationToken.None);
            var targetResolution = await _semanticsResolver.ResolveAsync(
                target,
                FileSystemCaseSensitivityMode.Auto,
                CancellationToken.None);
            if (sourceResolution.State != PathIdentityState.Valid
                || targetResolution.State != PathIdentityState.Valid)
            {
                throw new InvalidOperationException(
                    sourceResolution.Reason
                        ?? targetResolution.Reason
                        ?? "Filesystem identity is unavailable.");
            }

            var sourceIdentity = PathIdentitySnapshot.FromResolution(
                sourceResolution.Semantics,
                FileSystemCaseSensitivityMode.Auto,
                sourceResolution.BoundaryPath,
                source);
            var effectiveSourceCleanupBoundary = sourceCleanupBoundary;
            if (deleteEmptySource && string.IsNullOrWhiteSpace(effectiveSourceCleanupBoundary))
            {
                effectiveSourceCleanupBoundary = Path.GetDirectoryName(
                    Path.TrimEndingDirectorySeparator(source))
                    ?? sourceIdentity.BoundaryPath;
            }
            else if (!string.IsNullOrWhiteSpace(effectiveSourceCleanupBoundary))
            {
                effectiveSourceCleanupBoundary =
                    FileSystemPathIdentity.ResolveNativeAbsolutePath(
                        effectiveSourceCleanupBoundary);
            }
            var targetIdentity = PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                FileSystemCaseSensitivityMode.Auto,
                targetResolution.BoundaryPath,
                target);
            return await base.EnqueueMoveAsync(new MoveEnqueueCommand(
                audiobookId,
                source,
                sourceIdentity,
                [new MoveSourceManifestEntry(
                    "book.m4b",
                    MoveJobEntryType.File,
                    1,
                    DateTime.UnixEpoch,
                    new string('A', 64))],
                target,
                targetIdentity,
                SourceBoundaryDirectoryObjectIdentityVersion: 2,
                SourceBoundaryDirectoryObjectIdentity: "test-source-boundary-identity",
                TargetBoundaryDirectoryObjectIdentityVersion: 2,
                TargetBoundaryDirectoryObjectIdentity: "test-target-boundary-identity",
                DeleteEmptySource: deleteEmptySource,
                SourceCleanupBoundary: effectiveSourceCleanupBoundary,
                SourceCleanupMode: sourceCleanupMode,
                ForceCopyAndRetainSource: forceCopyAndRetainSource));
        }
    }
}
