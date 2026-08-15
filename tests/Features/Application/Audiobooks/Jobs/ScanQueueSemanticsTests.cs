/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Text.Json;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs
{
    [Trait("Area", "Jobs")]
    [Trait("Name", "ScanQueueSemanticsTests")]
    [Trait("Category", "Application")]
    public sealed class ScanQueueSemanticsTests : BaseTests
    {
        private static readonly ScanPathPhysicalIdentity PhysicalIdentity = new(
            "scan-queue-test-boundary",
            "scan-queue-test-root");

        [Fact]
        public void ScanJob_Serialization_DoesNotExposePhysicalIdentity()
        {
            var job = new ScanJob
            {
                AudiobookId = 42,
                PhysicalIdentity = PhysicalIdentity
            };

            var json = JsonSerializer.Serialize(job);

            Assert.DoesNotContain(
                nameof(ScanJob.PhysicalIdentity),
                json,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                nameof(ScanJob.AuthorizationMode),
                json,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PhysicalIdentity, job.PhysicalIdentity);
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivity.Sensitive, false)]
        [InlineData(FileSystemCaseSensitivity.Insensitive, true)]
        public async Task ScanQueue_DedupeUsesResolvedSemantics(
            FileSystemCaseSensitivity caseSensitivity,
            bool shouldDedupe)
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1001)
                .WithTitle("Case Book")
                .Build();
            var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-scan-queue"));
            var first = Path.Join(root, "CaseBook");
            var second = Path.Join(root, "casebook");

            var firstJob = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                first,
                CreateHostIdentity(first, root, caseSensitivity),
                PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));
            var secondJob = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                second,
                CreateHostIdentity(second, root, caseSensitivity),
                PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));

            Assert.Equal(shouldDedupe, firstJob == secondJob);
        }

        [Fact]
        public async Task ScanQueue_DifferentReconciliationAuthorityDoesNotDedupe()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1013)
                .WithTitle("Authority Bound Scan")
                .Build();
            const string path = "/library/book/cd1";
            var identity = CreateUnixIdentity(
                path,
                FileSystemCaseSensitivity.Sensitive);

            var focused = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                path,
                identity,
                PhysicalIdentity,
                IsAuthoritativeScope: false,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));
            var authoritative = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                path,
                identity,
                PhysicalIdentity,
                IsAuthoritativeScope: true,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));

            Assert.NotEqual(focused, authoritative);
        }

        [Fact]
        public async Task ScanQueue_RequeuePreservesReconciliationAuthority()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1014)
                .WithTitle("Focused Requeue")
                .Build();
            const string path = "/library/book/cd1";
            var jobId = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                path,
                CreateUnixIdentity(path, FileSystemCaseSensitivity.Sensitive),
                PhysicalIdentity,
                IsAuthoritativeScope: false,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));
            Assert.True(queue.Reader.TryRead(out _));
            queue.UpdateJobStatus(jobId, "Failed", "retry");

            var replacementId = await queue.RequeueScanAsync(jobId);

            Assert.NotNull(replacementId);
            Assert.True(queue.Reader.TryRead(out var replacement));
            Assert.Equal(replacementId, replacement.Id);
            Assert.False(replacement.IsAuthoritativeScope);
        }

        [Theory]
        [InlineData(
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivity.Sensitive,
            true)]
        [InlineData(
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivity.Insensitive,
            true)]
        [InlineData(
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivity.Sensitive,
            false)]
        public async Task ScanQueue_DedupeUsesBothPersistedEndpointIdentities(
            FileSystemCaseSensitivity firstSensitivity,
            FileSystemCaseSensitivity secondSensitivity,
            bool shouldDedupe)
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1010)
                .WithTitle("Persisted Identity Scan")
                .Build();
            const string firstPath = "/library/CaseBook";
            const string secondPath = "/library/casebook";

            var firstJob = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                firstPath,
                CreateUnixIdentity(firstPath, firstSensitivity),
                PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));
            var secondJob = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                secondPath,
                CreateUnixIdentity(secondPath, secondSensitivity),
                PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));

            Assert.Equal(shouldDedupe, firstJob == secondJob);
        }

        [Fact]
        public async Task ScanQueue_DifferentPersistedSyntaxesNeverDedupe()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1011)
                .WithTitle("Cross Syntax Scan")
                .Build();
            const string windowsPath = @"C:\Library\Book";
            const string unixPath = "/Library/Book";
            var windowsIdentity = PathIdentitySnapshot.FromResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Insensitive),
                FileSystemCaseSensitivityMode.Insensitive,
                @"C:\Library",
                windowsPath);

            var firstJob = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                windowsPath,
                windowsIdentity,
                PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));
            var secondJob = await queue.EnqueueScanAsync(new ScanEnqueueCommand(
                audiobook,
                unixPath,
                CreateUnixIdentity(unixPath, FileSystemCaseSensitivity.Insensitive),
                PhysicalIdentity,
                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath));

            Assert.NotEqual(firstJob, secondJob);
        }

        [Fact]
        public async Task ScanQueue_CompletedCorrelationCreatesReplacementHandoff()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1002)
                .WithTitle("Move Completion Book")
                .Build();
            const string correlationId = "move:abc123";

            var earlierExplicitScan = await queue.EnqueueScanAsync(audiobook);
            queue.UpdateJobStatus(earlierExplicitScan, "Completed");
            var correlatedScan = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            queue.UpdateJobStatus(correlatedScan, "Completed");
            var replayedJob = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            var explicitRescan = await queue.EnqueueScanAsync(audiobook);

            Assert.NotEqual(correlatedScan, replayedJob);
            Assert.NotEqual(earlierExplicitScan, correlatedScan);
            Assert.Equal(replayedJob, explicitRescan);
        }

        [Fact]
        public async Task ScanQueue_ConcurrentCorrelationDispatchesOnlyOneJob()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1003)
                .WithTitle("Concurrent Move Scan")
                .Build();
            const string correlationId = "move:concurrent-scan";

            var jobs = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
                queue.EnqueueScanAsync(audiobook, correlationId: correlationId)));

            Assert.Single(jobs.Distinct());
            Assert.True(queue.Reader.TryRead(out var queued));
            Assert.Equal(jobs[0], queued.Id);
            Assert.False(queue.Reader.TryRead(out _));
        }

        [Fact]
        public async Task ScanQueue_ManualMoveRetryDefersAndReleasesHandoffWhenAnotherScanIsActive()
        {
            var handoffId = Guid.NewGuid();
            var moveJobId = Guid.NewGuid();
            var store = new Mock<IMoveScanHandoffStore>();
            store.Setup(candidate => candidate.RequeueAsync(
                    handoffId,
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            store.Setup(candidate => candidate.ReleaseClaimAsync(
                    handoffId,
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            store.Setup(candidate => candidate.MarkDispatchedAsync(
                    handoffId,
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<Guid>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            store.Setup(candidate => candidate.TryClaimAsync(
                    handoffId,
                    It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MoveScanHandoffClaim(
                    handoffId,
                    moveJobId,
                    1004,
                    "/library/book",
                    CreateUnixIdentity(),
                    [],
                    2,
                    "manual-worker",
                    2));
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                store.Object,
                TimeProvider.System);
            var audiobook = new AudiobookBuilder()
                .WithId(1004)
                .WithTitle("Deferred Move Retry")
                .Build();
            var original = await queue.EnqueueMoveHandoffScanAsync(
                audiobook,
                new MoveScanHandoffClaim(
                    handoffId,
                    moveJobId,
                    audiobook.Id,
                    "/library/book",
                    CreateUnixIdentity(),
                    [],
                    1,
                    "initial-worker",
                    1),
                PhysicalIdentity);
            var originalId = Assert.IsType<Guid>(original);
            Assert.True(queue.Reader.TryRead(out var originalJob));
            Assert.Equal("/library/book", originalJob.Path);
            queue.UpdateJobStatus(originalId, "Failed", "first attempt failed");
            var ordinary = await queue.EnqueueScanAsync(audiobook);
            Assert.NotEqual(originalId, ordinary);

            var retried = await queue.RequeueScanAsync(originalId);

            Assert.Null(retried);
            store.Verify(candidate => candidate.RequeueAsync(
                handoffId,
                originalId,
                1,
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
            store.Verify(candidate => candidate.ReleaseClaimAsync(
                handoffId,
                "manual-worker",
                2,
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
            store.Verify(candidate => candidate.MarkDispatchedAsync(
                handoffId,
                It.IsAny<string>(),
                1,
                originalId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ScanQueue_CorrelatedMoveHandoffDoesNotReuseDifferentTargetIdentity()
        {
            var handoffId = Guid.NewGuid();
            var moveJobId = Guid.NewGuid();
            var store = new Mock<IMoveScanHandoffStore>();
            store.Setup(candidate => candidate.MarkDispatchedAsync(
                    handoffId,
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<Guid>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                store.Object,
                TimeProvider.System);
            var audiobook = new AudiobookBuilder()
                .WithId(1012)
                .WithTitle("Identity-Bound Handoff")
                .Build();
            var firstClaim = new MoveScanHandoffClaim(
                handoffId,
                moveJobId,
                audiobook.Id,
                "/library/book",
                CreateUnixIdentity(),
                [],
                1,
                "worker-a",
                1);
            var conflictingClaim = firstClaim with
            {
                TargetPath = "/library/other",
                TargetIdentity = CreateUnixIdentity(
                    "/library/other",
                    FileSystemCaseSensitivity.Sensitive)
            };

            var firstJob = await queue.EnqueueMoveHandoffScanAsync(
                audiobook,
                firstClaim,
                PhysicalIdentity);
            var conflictingJob = await queue.EnqueueMoveHandoffScanAsync(
                audiobook,
                conflictingClaim,
                PhysicalIdentity);

            Assert.NotNull(firstJob);
            Assert.Null(conflictingJob);
            store.Verify(candidate => candidate.MarkDispatchedAsync(
                handoffId,
                It.IsAny<string>(),
                1,
                firstJob!.Value,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ScanQueue_TerminalPersistenceDoesNotHoldQueueGate()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1005)
                .WithTitle("Terminal Persistence")
                .Build();
            var original = await queue.EnqueueScanAsync(audiobook);
            Assert.True(queue.Reader.TryRead(out _));
            queue.UpdateJobStatus(original, "Processing");
            var persistenceEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePersistence = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = queue.CommitTerminalJobStatusAsync(
                original,
                async () =>
                {
                    persistenceEntered.TrySetResult();
                    await releasePersistence.Task;
                    return ("Completed", (string?)null);
                });
            await persistenceEntered.Task;

            var otherAudiobook = new AudiobookBuilder()
                .WithId(1007)
                .WithTitle("Independent Scan")
                .Build();
            var otherEnqueue = queue.EnqueueScanAsync(otherAudiobook);
            var otherJobId = await otherEnqueue.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEqual(original, otherJobId);

            releasePersistence.TrySetResult();
            await completion;
            Assert.True(queue.TryGetJob(original, out var completed));
            Assert.Equal("Completed", completed!.Status);
        }

        [Fact]
        public async Task ScanQueue_RequestCancelledAfterTerminalPersistenceStillUpdatesQueue()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1008)
                .WithTitle("Post Commit Cancellation")
                .Build();
            var jobId = await queue.EnqueueScanAsync(audiobook);
            Assert.True(queue.Reader.TryRead(out _));
            queue.UpdateJobStatus(jobId, "Processing");
            using var cancellation = new CancellationTokenSource();

            await queue.CommitTerminalJobStatusAsync(
                jobId,
                () =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(("Completed", (string?)null));
                },
                cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(queue.TryGetJob(jobId, out var completed));
            Assert.Equal("Completed", completed!.Status);
        }

        [Fact]
        public async Task ScanQueue_TerminalCommitUsesAuthoritativePersistedOutcome()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1006)
                .WithTitle("Authoritative Terminal State")
                .Build();
            var jobId = await queue.EnqueueScanAsync(audiobook);
            Assert.True(queue.Reader.TryRead(out _));
            queue.UpdateJobStatus(jobId, "Processing");

            await queue.CommitTerminalJobStatusAsync(
                jobId,
                () => Task.FromResult(("Failed", (string?)"durable failure")));

            Assert.True(queue.TryGetJob(jobId, out var terminal));
            Assert.Equal("Failed", terminal!.Status);
            Assert.Equal("durable failure", terminal.Error);
        }

        [Fact]
        public async Task ScanQueue_FailedCorrelationCreatesReplacementHandoff()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance);
            var audiobook = new AudiobookBuilder()
                .WithId(1005)
                .WithTitle("Failed Move Scan")
                .Build();
            const string correlationId = "move:failed-scan";

            var failedJob = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            queue.UpdateJobStatus(failedJob, "Failed", "Transient failure");
            var replacement = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);

            Assert.NotEqual(failedJob, replacement);
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivity.Sensitive, false)]
        [InlineData(FileSystemCaseSensitivity.Insensitive, true)]
        public async Task UnmatchedScanQueue_DedupeUsesResolvedSemantics(
            FileSystemCaseSensitivity caseSensitivity,
            bool shouldDedupe)
        {
            var queue = new UnmatchedScanQueueService(
                NullLogger<UnmatchedScanQueueService>.Instance,
                BuildResolver(caseSensitivity));
            var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-unmatched-queue"));
            var first = Path.Join(root, "CaseRoot");
            var second = Path.Join(root, "caseroot");

            var firstJob = await queue.EnqueueAsync(first);
            var secondJob = await queue.EnqueueAsync(second);

            Assert.Equal(shouldDedupe, firstJob == secondJob);
        }

        private static PathIdentitySnapshot CreateHostIdentity(
            string path,
            string boundary,
            FileSystemCaseSensitivity sensitivity) =>
            PathIdentitySnapshot.FromResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    sensitivity),
                sensitivity == FileSystemCaseSensitivity.Insensitive
                    ? FileSystemCaseSensitivityMode.Insensitive
                    : FileSystemCaseSensitivityMode.Sensitive,
                boundary,
                path);

        private static PathIdentitySnapshot CreateUnixIdentity(
            string path = "/library/book",
            FileSystemCaseSensitivity sensitivity = FileSystemCaseSensitivity.Sensitive) =>
            PathIdentitySnapshot.FromResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Unix,
                    sensitivity),
                sensitivity == FileSystemCaseSensitivity.Insensitive
                    ? FileSystemCaseSensitivityMode.Insensitive
                    : FileSystemCaseSensitivityMode.Sensitive,
                "/library",
                path);

        private static IFileSystemSemanticsResolver BuildResolver(FileSystemCaseSensitivity caseSensitivity)
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(r => r.ResolveAsync(
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
    }
}
