using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "EfMoveScanHandoffStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfMoveScanHandoffStoreTests : BaseTests
{
    [Fact]
    public async Task CommitMoveCompletionAsync_AtomicallyCompletesMoveAndCreatesOneHandoff()
    {
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Atomic Completion",
            BasePath = FileService.GetTempDirectory("handoff-atomic-completion")
        });
        var job = await InsertRunningMoveAsync(audiobook.Id, audiobook.BasePath!);
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();

        var result = await store.CommitMoveCompletionAsync(
            new MoveCompletionCommit(
                job.Id,
                job.LeaseOwner!,
                job.LeaseGeneration,
                audiobook.Id,
                audiobook.Title,
                job.SourcePath!,
                job.RequestedPath!,
                DateTimeOffset.UtcNow));

        Assert.True(result.MoveHistoryCreated);
        Assert.True(result.HandoffCreated);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persistedJob = await db.MoveJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(MoveJobStatus.Completed, persistedJob.Status);
        Assert.Null(persistedJob.LeaseOwner);
        Assert.Null(persistedJob.ActiveDeduplicationKey);
        var handoff = await db.MoveScanHandoffs.AsNoTracking().SingleAsync(candidate => candidate.MoveJobId == job.Id);
        Assert.Equal(MoveScanHandoffStatus.Pending, handoff.Status);
        Assert.Single(await db.History.AsNoTracking()
            .Where(history => history.IdempotencyKey == $"move:{job.Id:N}:moved")
            .ToListAsync());
    }

    [Fact]
    public async Task CommitMoveCompletionAsync_RetryAfterCommittedCompletion_ReusesExistingRecords()
    {
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Idempotent Completion",
            BasePath = FileService.GetTempDirectory("handoff-idempotent-completion")
        });
        var job = await InsertRunningMoveAsync(audiobook.Id, audiobook.BasePath!);
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var command = new MoveCompletionCommit(
            job.Id,
            job.LeaseOwner!,
            job.LeaseGeneration,
            audiobook.Id,
            audiobook.Title,
            job.SourcePath!,
            job.RequestedPath!,
            DateTimeOffset.UtcNow);

        var first = await store.CommitMoveCompletionAsync(command);
        var retry = await store.CommitMoveCompletionAsync(command);

        Assert.True(first.MoveHistoryCreated);
        Assert.True(first.HandoffCreated);
        Assert.False(retry.MoveHistoryCreated);
        Assert.False(retry.HandoffCreated);
        Assert.Equal(first.MoveHistory.Id, retry.MoveHistory.Id);
        Assert.Equal(first.Handoff.Id, retry.Handoff.Id);
        await using var db = await GetFactory().CreateDbContextAsync();
        Assert.Single(await db.History.AsNoTracking()
            .Where(history => history.IdempotencyKey == $"move:{job.Id:N}:moved")
            .ToListAsync());
        Assert.Single(await db.MoveScanHandoffs.AsNoTracking()
            .Where(handoff => handoff.MoveJobId == job.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task FailedAttempt_CanBeManuallyRequeuedAndLaterSucceed()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var first = await store.TryClaimAsync(
            handoff.Id,
            "worker-one",
            now,
            now.AddMinutes(5));
        Assert.NotNull(first);
        var firstScanJobId = Guid.NewGuid();
        await store.MarkDispatchedAsync(
            first!.HandoffId,
            first.LeaseOwner,
            first.LeaseGeneration,
            firstScanJobId,
            now);
        var failed = await store.CompleteAttemptAsync(
            first.HandoffId,
            first.AttemptGeneration,
            firstScanJobId,
            MoveScanTerminalOutcome.Failed,
            "temporary scan failure",
            found: 0,
            created: 0,
            scanPath: handoff.TargetPath,
            now);
        Assert.Equal(MoveScanAttemptOutcome.Failed, failed.Outcome);

        Assert.True(await store.RequeueAsync(
            handoff.Id,
            firstScanJobId,
            first.AttemptGeneration,
            null,
            now.AddSeconds(1)));
        var second = await store.TryClaimAsync(
            handoff.Id,
            "worker-two",
            now.AddSeconds(1),
            now.AddMinutes(6));
        Assert.NotNull(second);
        Assert.Equal(first.AttemptGeneration + 1, second!.AttemptGeneration);
        var secondScanJobId = Guid.NewGuid();
        Assert.True(await store.MarkDispatchedAsync(
            second.HandoffId,
            second.LeaseOwner,
            second.LeaseGeneration,
            secondScanJobId,
            now.AddSeconds(1)));
        var completed = await store.CompleteAttemptAsync(
            second.HandoffId,
            second.AttemptGeneration,
            secondScanJobId,
            MoveScanTerminalOutcome.Succeeded,
            error: null,
            found: 1,
            created: 1,
            scanPath: handoff.TargetPath,
            now.AddSeconds(2));
        Assert.Equal(MoveScanAttemptOutcome.Completed, completed.Outcome);

        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Succeeded, persisted.Status);
        Assert.Equal(second.AttemptGeneration, persisted.AttemptGeneration);
        var terminal = await db.History.AsNoTracking()
            .Where(history => history.IdempotencyKey != null
                && history.IdempotencyKey.StartsWith($"handoff:{handoff.Id:N}:attempt:"))
            .ToListAsync();
        Assert.Contains(terminal, history => history.EventType == HistoryEvents.ScanFailed);
        Assert.Contains(terminal, history => history.EventType == HistoryEvents.ScanCompleted);
    }

    [Fact]
    public async Task StaleAttemptCannotCompleteNewerGeneration()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var first = await store.TryClaimAsync(
            handoff.Id,
            "worker-one",
            now,
            now.AddMinutes(5));
        Assert.NotNull(first);
        var firstScanJobId = Guid.NewGuid();
        Assert.True(await store.MarkDispatchedAsync(
            first!.HandoffId,
            first.LeaseOwner,
            first.LeaseGeneration,
            firstScanJobId,
            now));
        var firstFailure = await store.CompleteAttemptAsync(
            first.HandoffId,
            first.AttemptGeneration,
            firstScanJobId,
            MoveScanTerminalOutcome.Failed,
            error: "retry",
            found: 0,
            created: 0,
            scanPath: handoff.TargetPath,
            now);
        Assert.Equal(MoveScanAttemptOutcome.Failed, firstFailure.Outcome);
        Assert.True(await store.RequeueAsync(
            handoff.Id,
            firstScanJobId,
            first.AttemptGeneration,
            "retry",
            now.AddSeconds(1)));
        var second = await store.TryClaimAsync(
            handoff.Id,
            "worker-two",
            now.AddSeconds(1),
            now.AddMinutes(6));
        Assert.NotNull(second);

        var stale = await store.CompleteAttemptAsync(
            first.HandoffId,
            first.AttemptGeneration,
            firstScanJobId,
            MoveScanTerminalOutcome.Succeeded,
            error: null,
            found: 1,
            created: 1,
            scanPath: handoff.TargetPath,
            now.AddSeconds(2));

        Assert.Equal(MoveScanAttemptOutcome.Superseded, stale.Outcome);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Claimed, persisted.Status);
        Assert.Equal(second!.AttemptGeneration, persisted.AttemptGeneration);
        var firstAttemptHistory = await db.History.AsNoTracking()
            .Where(history => history.IdempotencyKey != null
                && history.IdempotencyKey.Contains($":attempt:{first.AttemptGeneration}:"))
            .ToListAsync();
        var firstTerminal = Assert.Single(firstAttemptHistory);
        Assert.Equal(HistoryEvents.ScanFailed, firstTerminal.EventType);
    }

    [Fact]
    public async Task StaleClaimRelease_CannotResetNewerClaim()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var first = await store.TryClaimAsync(
            handoff.Id,
            "worker-one",
            now,
            now.AddSeconds(1));
        Assert.NotNull(first);
        var second = await store.TryClaimAsync(
            handoff.Id,
            "worker-two",
            now.AddSeconds(2),
            now.AddMinutes(5));
        Assert.NotNull(second);

        var released = await store.ReleaseClaimAsync(
            handoff.Id,
            first!.LeaseOwner,
            first.LeaseGeneration,
            "stale release",
            now.AddSeconds(3));

        Assert.False(released);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Claimed, persisted.Status);
        Assert.Equal(second!.LeaseOwner, persisted.LeaseOwner);
        Assert.Equal(second.LeaseGeneration, persisted.LeaseGeneration);
    }

    [Fact]
    public async Task MarkDispatched_ExpiredClaimCannotPublishScanJob()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.TryClaimAsync(
            handoff.Id,
            "expired-dispatch-worker",
            now,
            now.AddSeconds(1));
        Assert.NotNull(claim);

        var dispatched = await store.MarkDispatchedAsync(
            claim!.HandoffId,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            Guid.NewGuid(),
            now.AddSeconds(2));

        Assert.False(dispatched);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Null(persisted.ActiveScanJobId);
    }

    [Fact]
    public async Task RenewAttemptLease_RequiresCurrentAttemptAndActiveScanJob()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.TryClaimAsync(
            handoff.Id,
            "heartbeat-worker",
            now,
            now.AddMinutes(5));
        Assert.NotNull(claim);
        var scanJobId = Guid.NewGuid();
        Assert.True(await store.MarkDispatchedAsync(
            claim!.HandoffId,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            scanJobId,
            now));

        var renewed = await store.RenewAttemptLeaseAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            scanJobId,
            now.AddMinutes(1),
            now.AddMinutes(6));
        var wrongScan = await store.RenewAttemptLeaseAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            Guid.NewGuid(),
            now.AddMinutes(2),
            now.AddMinutes(7));

        Assert.Equal(MoveScanLeaseRenewalOutcome.Renewed, renewed.Outcome);
        Assert.Equal(MoveScanLeaseRenewalOutcome.Superseded, wrongScan.Outcome);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(now.AddMinutes(6).UtcDateTime, persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task RenewAttemptLease_ExpiredClaimCannotBeRevived()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.TryClaimAsync(
            handoff.Id,
            "expired-heartbeat-worker",
            now,
            now.AddSeconds(1));
        Assert.NotNull(claim);
        var scanJobId = Guid.NewGuid();
        Assert.True(await store.MarkDispatchedAsync(
            claim!.HandoffId,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            scanJobId,
            now));

        var renewal = await store.RenewAttemptLeaseAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            scanJobId,
            now.AddSeconds(2),
            now.AddMinutes(5));

        Assert.Equal(MoveScanLeaseRenewalOutcome.Superseded, renewal.Outcome);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(now.AddSeconds(1).UtcDateTime, persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task CompleteAttempt_AfterLeaseExpiryIsSupersededAndRemainsReclaimable()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.TryClaimAsync(
            handoff.Id,
            "expired-completion-worker",
            now,
            now.AddSeconds(1));
        Assert.NotNull(claim);
        var scanJobId = Guid.NewGuid();
        Assert.True(await store.MarkDispatchedAsync(
            claim!.HandoffId,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            scanJobId,
            now));

        var completion = await store.CompleteAttemptAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            scanJobId,
            MoveScanTerminalOutcome.Succeeded,
            error: null,
            found: 1,
            created: 1,
            scanPath: handoff.TargetPath,
            now.AddSeconds(2));

        Assert.Equal(MoveScanAttemptOutcome.Superseded, completion.Outcome);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Claimed, persisted.Status);
        Assert.Equal(scanJobId, persisted.ActiveScanJobId);
        Assert.Equal(now.AddSeconds(1).UtcDateTime, persisted.LeaseExpiresAt);
        Assert.Empty(await db.History.AsNoTracking()
            .Where(history => history.IdempotencyKey ==
                $"handoff:{handoff.Id:N}:attempt:{claim.AttemptGeneration}:terminal")
            .ToListAsync());
        Assert.Contains(
            handoff.Id,
            await store.GetClaimableIdsAsync(now.AddSeconds(2), 10));
    }

    [Fact]
    public async Task RenewAttemptLease_AfterTerminalCommit_ReturnsAuthoritativeOutcome()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.TryClaimAsync(
            handoff.Id,
            "terminal-heartbeat-worker",
            now,
            now.AddMinutes(5));
        Assert.NotNull(claim);
        var scanJobId = Guid.NewGuid();
        Assert.True(await store.MarkDispatchedAsync(
            claim!.HandoffId,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            scanJobId,
            now));
        await store.CompleteAttemptAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            scanJobId,
            MoveScanTerminalOutcome.Failed,
            error: "terminal failure",
            found: 0,
            created: 0,
            scanPath: handoff.TargetPath,
            now);

        var renewal = await store.RenewAttemptLeaseAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            scanJobId,
            now.AddMinutes(1),
            now.AddMinutes(6));

        Assert.Equal(MoveScanLeaseRenewalOutcome.Failed, renewal.Outcome);
        Assert.Equal("terminal failure", renewal.Error);
    }

    [Fact]
    public async Task ContradictoryTerminalCallback_ReturnsAuthoritativeFirstOutcome()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.TryClaimAsync(
            handoff.Id,
            "terminal-worker",
            now,
            now.AddMinutes(5));
        Assert.NotNull(claim);
        var scanJobId = Guid.NewGuid();
        Assert.True(await store.MarkDispatchedAsync(
            claim!.HandoffId,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            scanJobId,
            now));

        var completed = await store.CompleteAttemptAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            scanJobId,
            MoveScanTerminalOutcome.Succeeded,
            error: null,
            found: 1,
            created: 1,
            scanPath: handoff.TargetPath,
            now);
        var contradictoryFailure = await store.CompleteAttemptAsync(
            claim.HandoffId,
            claim.AttemptGeneration,
            scanJobId,
            MoveScanTerminalOutcome.Failed,
            error: "late failure",
            found: 0,
            created: 0,
            scanPath: handoff.TargetPath,
            now.AddSeconds(1));

        Assert.Equal(MoveScanAttemptOutcome.Completed, completed.Outcome);
        Assert.Equal(MoveScanAttemptOutcome.Completed, contradictoryFailure.Outcome);
        await using var db = await GetFactory().CreateDbContextAsync();
        var terminal = await db.History.AsNoTracking()
            .Where(history => history.IdempotencyKey ==
                $"handoff:{handoff.Id:N}:attempt:{claim.AttemptGeneration}:terminal")
            .ToListAsync();
        var eventEntry = Assert.Single(terminal);
        Assert.Equal(HistoryEvents.ScanCompleted, eventEntry.EventType);
        Assert.Equal(HistoryOutcome.Succeeded, eventEntry.Outcome);
    }

    [Fact]
    public async Task TryClaim_MissingTargetIdentityFailsHandoffInsteadOfHotLoopingPending()
    {
        var handoff = await InsertPendingHandoffAsync();
        await using (var db = await GetFactory().CreateDbContextAsync())
        {
            var move = await db.MoveJobs.SingleAsync(candidate => candidate.Id == handoff.MoveJobId);
            move.TargetPathSyntax = null;
            move.TargetCaseSensitivity = null;
            move.TargetCaseSensitivityMode = null;
            move.TargetIdentityBoundary = null;
            await db.SaveChangesAsync();
        }
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;

        var claim = await store.TryClaimAsync(
            handoff.Id,
            "invalid-identity-worker",
            now,
            now.AddMinutes(5));

        Assert.Null(claim);
        await using var verification = await GetFactory().CreateDbContextAsync();
        var persisted = await verification.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Failed, persisted.Status);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Contains("no authoritative target", persisted.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            handoff.Id,
            await store.GetClaimableIdsAsync(now.AddMinutes(10), 10));
    }

    [Fact]
    public async Task TryClaim_InvalidTargetIdentityBoundaryFailsHandoffInsteadOfLeavingClaimed()
    {
        var handoff = await InsertPendingHandoffAsync();
        await using (var db = await GetFactory().CreateDbContextAsync())
        {
            var move = await db.MoveJobs.SingleAsync(candidate => candidate.Id == handoff.MoveJobId);
            move.TargetIdentityBoundary = FileSystemPathIdentity.ResolveNativeAbsolutePath(
                Path.Join(Path.GetTempPath(), $"unrelated-handoff-boundary-{Guid.NewGuid():N}"));
            await db.SaveChangesAsync();
        }
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;

        var claim = await store.TryClaimAsync(
            handoff.Id,
            "invalid-boundary-worker",
            now,
            now.AddMinutes(5));

        Assert.Null(claim);
        await using var verification = await GetFactory().CreateDbContextAsync();
        var persisted = await verification.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Failed, persisted.Status);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Contains("identity is invalid", persisted.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            handoff.Id,
            await store.GetClaimableIdsAsync(now.AddMinutes(10), 10));
    }

    [Fact]
    public async Task ConcurrentClaims_OnlyOneWorkerAcquiresHandoff()
    {
        var handoff = await InsertPendingHandoffAsync();
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var now = DateTimeOffset.UtcNow;

        var claims = await Task.WhenAll(
            store.TryClaimAsync(handoff.Id, "worker-one", now, now.AddMinutes(5)),
            store.TryClaimAsync(handoff.Id, "worker-two", now, now.AddMinutes(5)));

        Assert.Single(claims, claim => claim != null);
    }

    [Fact]
    public async Task TryClaimAsync_RootProofWithoutTrackedFileManifest_FailsClosed()
    {
        var handoff = await InsertPendingHandoffAsync();
        await using (var db = await GetFactory().CreateDbContextAsync())
        {
            var moveJobId = await db.MoveScanHandoffs
                .Where(candidate => candidate.Id == handoff.Id)
                .Select(candidate => candidate.MoveJobId)
                .SingleAsync();
            var entries = await db.MoveJobEntries
                .Where(entry => entry.MoveJobId == moveJobId)
                .ToListAsync();
            db.MoveJobEntries.RemoveRange(entries);
            db.MoveJobEntries.Add(new MoveJobEntry
            {
                MoveJobId = moveJobId,
                RelativePath = string.Empty,
                EntryType = MoveJobEntryType.Directory
            });
            await db.SaveChangesAsync();
        }

        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var claim = await store.TryClaimAsync(
            handoff.Id,
            "worker",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Null(claim);
        await using var verification = await GetFactory().CreateDbContextAsync();
        var persisted = await verification.MoveScanHandoffs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Failed, persisted.Status);
        Assert.Contains(
            "tracked-file",
            persisted.LastError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryClaimAsync_TargetAuthorizationWithoutSourceManifest_FailsClosed()
    {
        var target = FileService.GetTempDirectory("move-scan-handoff-auth-only");
        await using (var db = await GetFactory().CreateDbContextAsync())
        {
            var move = new MoveJob
            {
                AudiobookId = 42,
                SourcePath = target,
                RequestedPath = target,
                Status = MoveJobStatus.Completed,
                Phase = MoveJobPhase.RecordingCompletion
            };
            SetPathIdentities(move);
            move.IdentityKeyVersion = MoveManifestIdentity.Version;
            db.MoveJobs.Add(move);
            var authorization = MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                2,
                "test-target-generation");
            authorization.MoveJobId = move.Id;
            db.MoveJobEntries.Add(authorization);
            db.MoveScanHandoffs.Add(new MoveScanHandoff
            {
                MoveJobId = move.Id,
                AudiobookId = move.AudiobookId,
                TargetPath = target,
                Status = MoveScanHandoffStatus.Pending
            });
            await db.SaveChangesAsync();
        }

        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        await using var lookup = await GetFactory().CreateDbContextAsync();
        var handoffId = await lookup.MoveScanHandoffs
            .Select(handoff => handoff.Id)
            .SingleAsync();

        var claim = await store.TryClaimAsync(
            handoffId,
            "worker",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Null(claim);
        await using var verification = await GetFactory().CreateDbContextAsync();
        var handoff = await verification.MoveScanHandoffs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == handoffId);
        Assert.Equal(MoveScanHandoffStatus.Failed, handoff.Status);
        Assert.Contains(
            "no durable target manifest",
            handoff.LastError,
            StringComparison.OrdinalIgnoreCase);
    }

    private IDbContextFactory<ListenArrDbContext> GetFactory() =>
        _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();

    private async Task<MoveJob> InsertRunningMoveAsync(int audiobookId, string targetPath)
    {
        await using var db = await GetFactory().CreateDbContextAsync();
        var job = new MoveJob
        {
            AudiobookId = audiobookId,
            SourcePath = targetPath + "-source",
            RequestedPath = targetPath,
            Status = MoveJobStatus.Running,
            Phase = MoveJobPhase.RecordingCompletion,
            LeaseOwner = "completion-worker",
            LeaseGeneration = 1,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ActiveDeduplicationKey = $"test:{Guid.NewGuid():N}"
        };
        SetPathIdentities(job);
        db.MoveJobs.Add(job);
        db.MoveJobEntries.Add(new MoveJobEntry
        {
            MoveJobId = job.Id,
            RelativePath = "book.m4b",
            EntryType = MoveJobEntryType.File,
            Length = 1,
            Sha256 = new string('A', 64)
        });
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<MoveScanHandoff> InsertPendingHandoffAsync()
    {
        var target = FileService.GetTempDirectory("move-scan-handoff-store");
        await using var db = await GetFactory().CreateDbContextAsync();
        var move = new MoveJob
        {
            AudiobookId = 42,
            SourcePath = target,
            RequestedPath = target,
            Status = MoveJobStatus.Completed,
            Phase = MoveJobPhase.RecordingCompletion
        };
        var handoff = new MoveScanHandoff
        {
            MoveJobId = move.Id,
            AudiobookId = move.AudiobookId,
            TargetPath = target,
            Status = MoveScanHandoffStatus.Pending
        };
        SetPathIdentities(move);
        db.MoveJobs.Add(move);
        db.MoveJobEntries.Add(new MoveJobEntry
        {
            MoveJobId = move.Id,
            RelativePath = "book.m4b",
            EntryType = MoveJobEntryType.File,
            Length = 1,
            Sha256 = new string('A', 64)
        });
        db.MoveScanHandoffs.Add(handoff);
        await db.SaveChangesAsync();
        return handoff;
    }

    private static void SetPathIdentities(MoveJob job)
    {
        var source = FileSystemPathIdentity.ResolveNativeAbsolutePath(job.SourcePath!);
        var target = FileSystemPathIdentity.ResolveNativeAbsolutePath(job.RequestedPath!);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var sourceBoundary = Path.GetPathRoot(source)!;
        var targetBoundary = Path.GetPathRoot(target)!;
        job.SourcePath = source;
        job.RequestedPath = target;
        job.SetSourceIdentity(PathIdentitySnapshot.FromResolution(
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            sourceBoundary,
            source));
        job.SetTargetIdentity(PathIdentitySnapshot.FromResolution(
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            targetBoundary,
            target));
        job.IdentityKeyVersion = MoveManifestIdentity.Version;
    }
}
