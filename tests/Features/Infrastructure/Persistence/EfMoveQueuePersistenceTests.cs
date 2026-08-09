/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Mocks;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class EfMoveQueuePersistenceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"move-dedupe-{Guid.NewGuid():N}.db");
    private IDbContextFactory<ListenArrDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ActiveDeduplicationKey_IsUniqueUntilTerminalStatus()
    {
        var persistence = CreatePersistence();
        var first = CreateJob("42:/LIBRARY/BOOK");
        var duplicate = CreateJob("42:/LIBRARY/BOOK");

        await persistence.AddAsync(first);
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => persistence.AddAsync(duplicate));

        var claimedGeneration = await persistence.TryClaimAsync(
            first.Id,
            "worker-a",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2));
        await persistence.UpdateStatusAsync(
            first.Id,
            "worker-a",
            claimedGeneration.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            DateTimeOffset.UtcNow);
        await persistence.AddAsync(duplicate);

        Assert.Equal(duplicate.Id, (await persistence.GetActiveByKeyAsync("42:/LIBRARY/BOOK"))?.Id);
    }

    [Fact]
    public async Task SourceCleanupBoundary_RoundTripsWithMoveJob()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:cleanup-boundary");
        job.SourcePath = "/downloads/Author/Title/test";
        job.SourceCleanupBoundary = "/downloads";

        await persistence.AddAsync(job);
        var persisted = await persistence.GetByIdAsync(job.Id);

        Assert.NotNull(persisted);
        Assert.Equal("/downloads", persisted!.SourceCleanupBoundary);
    }

    [Fact]
    public async Task GetByIdAsync_LoadsCompleteRecoveryAggregate()
    {
        var persistence = CreatePersistence();
        var sourcePath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"listenarr-recovery-source-{Guid.NewGuid():N}"));
        var targetPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"listenarr-recovery-target-{Guid.NewGuid():N}"));
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var identity = new PathIdentitySnapshot(
            semantics.Syntax,
            semantics.CaseSensitivity,
            FileSystemCaseSensitivityMode.Auto,
            Path.GetFullPath(Path.GetTempPath()));
        var job = new MoveJob
        {
            AudiobookId = 42,
            SourcePath = sourcePath,
            RequestedPath = targetPath,
            Status = MoveJobStatus.NeedsAttention,
            Phase = MoveJobPhase.Published,
            ExecutionProtocolVersion = MoveExecutionProtocol.MarkerlessDatabaseState,
            SourceDirectoryCleanupState = MoveJobEntryCleanupState.Deleted,
            TargetDirectoryObjectIdentity = "target-generation",
            FailureKind = MoveFailureKind.Unknown,
            Entries = CreateAuthorizedManifestEntries(
                copyState: MoveJobEntryCopyState.Verified,
                cleanupState: MoveJobEntryCleanupState.Deleted),
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

        await persistence.AddAsync(job);
        var persisted = await persistence.GetByIdAsync(job.Id);

        var loaded = Assert.IsType<MoveJob>(persisted);
        Assert.Single(loaded.CreatedDirectories);
        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal(
            MoveRecoveryDisposition.RetryAvailable,
            MoveRecoveryPolicy.GetDisposition(loaded));
    }

    [Fact]
    public async Task ReconcileIdentityKeys_SelectsMostAdvancedDuplicate()
    {
        var sourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-reconcile-source", "book"));
        var targetPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-reconcile-target", "book"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.AddRange(
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = sourcePath,
                    RequestedPath = targetPath,
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.Planned,
                    IdentityKeyVersion = MoveManifestIdentity.Version,
                    ActiveDeduplicationKey = "stale:first",
                    Entries = CreateAuthorizedManifestEntries()
                },
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = sourcePath,
                    RequestedPath = targetPath,
                    Status = MoveJobStatus.Running,
                    Phase = MoveJobPhase.Published,
                    IdentityKeyVersion = MoveManifestIdentity.Version,
                    ActiveDeduplicationKey = "stale:second",
                    Entries = CreateAuthorizedManifestEntries()
                });
            await db.SaveChangesAsync();
        }

        var persistence = CreatePersistence();
        await persistence.ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.OrderBy(job => job.Phase).ToListAsync();
        Assert.True(
            jobs[0].Status == MoveJobStatus.Superseded,
            jobs[0].Error ?? $"Unexpected status: {jobs[0].Status}");
        Assert.Null(jobs[0].ActiveDeduplicationKey);
        Assert.True(
            jobs[1].Status == MoveJobStatus.Running,
            jobs[1].Error ?? $"Unexpected status: {jobs[1].Status}");
        Assert.StartsWith("v1:move-source:42:", jobs[1].ActiveDeduplicationKey);
    }

    [Theory]
    [InlineData(MoveExecutionProtocol.PreDurableReleased)]
    [InlineData(99)]
    public async Task ReconcileIdentityKeys_UnsupportedExecutionProtocol_RequiresAttention(
        int executionProtocolVersion)
    {
        var job = new MoveJob
        {
            AudiobookId = 42,
            SourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "unsupported-source")),
            RequestedPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "unsupported-target")),
            Status = MoveJobStatus.Queued,
            Phase = MoveJobPhase.Published,
            ExecutionProtocolVersion = executionProtocolVersion,
            ActiveDeduplicationKey = $"unsupported:{Guid.NewGuid():N}",
            Entries = CreateAuthorizedManifestEntries()
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(job);
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.MoveJobs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(MoveJobStatus.NeedsAttention, persisted.Status);
        Assert.Equal(MoveFailureKind.Verification, persisted.FailureKind);
        Assert.Null(persisted.ActiveDeduplicationKey);
        Assert.Contains(
            "predates the durable database execution protocol",
            persisted.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileIdentityKeys_ActiveJobWithoutTargetGeneration_RequiresAttention()
    {
        var sourcePath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            "listenarr-reconcile-missing-target-source",
            Guid.NewGuid().ToString("N")));
        var targetPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            "listenarr-reconcile-missing-target-destination",
            Guid.NewGuid().ToString("N")));
        var active = new MoveJob
        {
            AudiobookId = 42,
            SourcePath = sourcePath,
            RequestedPath = targetPath,
            Status = MoveJobStatus.Queued,
            Phase = MoveJobPhase.Planned,
            IdentityKeyVersion = MoveManifestIdentity.Version,
            ActiveDeduplicationKey = $"stale:{Guid.NewGuid():N}",
            Entries = [CreateManifestEntry()]
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(active);
            await db.SaveChangesAsync();
        }

        var persistence = CreatePersistence();
        await persistence.ReconcileIdentityKeysAsync();

        var reconciled = await persistence.GetByIdAsync(active.Id);
        Assert.NotNull(reconciled);
        Assert.Equal(MoveManifestIdentity.Version, reconciled.IdentityKeyVersion);
        Assert.Equal(MoveJobStatus.NeedsAttention, reconciled.Status);
        Assert.Null(reconciled.ActiveDeduplicationKey);
        Assert.Contains(
            "target-boundary physical-generation authorization",
            reconciled.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoveQueueStartup_ActiveJobWithoutTargetGeneration_DoesNotBlockNewAuthorizedMove()
    {
        var sourcePath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            "listenarr-queue-missing-target-source",
            Guid.NewGuid().ToString("N")));
        var targetPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            "listenarr-queue-missing-target-destination",
            Guid.NewGuid().ToString("N")));
        var active = new MoveJob
        {
            AudiobookId = 42,
            SourcePath = sourcePath,
            RequestedPath = targetPath,
            Status = MoveJobStatus.Queued,
            Phase = MoveJobPhase.Planned,
            IdentityKeyVersion = MoveManifestIdentity.Version,
            ActiveDeduplicationKey = $"stale:{Guid.NewGuid():N}",
            Entries = [CreateManifestEntry()]
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(active);
            await db.SaveChangesAsync();
        }

        var relocationService = new Mock<IRootFolderRelocationService>();
        relocationService.Setup(service => service.ReconcileActiveAsync(
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var persistence = CreatePersistence();
        var queue = new MoveQueueService(
            Mock.Of<ILogger<MoveQueueService>>(),
            persistence,
            new NoopHubBroadcaster(),
            TimeProvider.System,
            BuildSemanticsResolver(),
            relocationService.Object,
            new FilesystemMutationCoordinator());

        await queue.RecoverActiveJobsAsync();
        var reconciled = await persistence.GetByIdAsync(active.Id);
        Assert.NotNull(reconciled);
        Assert.Equal(MoveJobStatus.NeedsAttention, reconciled.Status);
        Assert.Null(reconciled.ActiveDeduplicationKey);
        var sourceResolution = await BuildSemanticsResolver().ResolveAsync(sourcePath);
        var targetResolution = await BuildSemanticsResolver().ResolveAsync(targetPath);
        Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
        Assert.Equal(PathIdentityState.Valid, targetResolution.State);
        var sourceIdentity = PathIdentitySnapshot.FromResolution(
            sourceResolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            sourceResolution.BoundaryPath,
            sourcePath);
        var targetIdentity = PathIdentitySnapshot.FromResolution(
            targetResolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            targetResolution.BoundaryPath,
            targetPath);
        var returnedId = await queue.EnqueueMoveAsync(new MoveEnqueueCommand(
            reconciled.AudiobookId,
            sourcePath,
            sourceIdentity,
            [
                new MoveSourceManifestEntry(
                    "book.m4b",
                    MoveJobEntryType.File,
                    1,
                    DateTime.UnixEpoch,
                    new string('A', 64))
            ],
            targetPath,
            targetIdentity,
            TargetBoundaryDirectoryObjectIdentityVersion: ManagedDirectoryIdentity.CurrentVersion,
            TargetBoundaryDirectoryObjectIdentity: "new-authorized-target-generation"));

        Assert.NotEqual(active.Id, returnedId);
        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs
            .Include(job => job.Entries)
            .OrderBy(job => job.EnqueuedAt)
            .ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(MoveJobStatus.NeedsAttention, jobs.Single(job => job.Id == active.Id).Status);
        var authorized = jobs.Single(job => job.Id == returnedId);
        Assert.Equal(MoveManifestIdentity.Version, authorized.IdentityKeyVersion);
        Assert.NotNull(authorized.ActiveDeduplicationKey);
        Assert.True(MoveManifestIdentity.TryGetTargetBoundaryAuthorization(
            authorized.Entries,
            out _,
            out _));
    }

    [Fact]
    public async Task ReconcileIdentityKeys_CurrentKeyWriteFailure_RollsBackClearedActiveKey()
    {
        var sourcePath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            "listenarr-reconcile-rollback-source",
            Guid.NewGuid().ToString("N")));
        var targetPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            "listenarr-reconcile-rollback-target",
            Guid.NewGuid().ToString("N")));
        var originalKey = $"stale:{Guid.NewGuid():N}";
        var active = new MoveJob
        {
            AudiobookId = 42,
            SourcePath = sourcePath,
            RequestedPath = targetPath,
            Status = MoveJobStatus.Queued,
            Phase = MoveJobPhase.Planned,
            IdentityKeyVersion = MoveManifestIdentity.Version,
            ActiveDeduplicationKey = originalKey,
            Entries = CreateAuthorizedManifestEntries()
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(active);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER fail_current_identity_key
                BEFORE UPDATE OF ActiveDeduplicationKey ON MoveJobs
                WHEN NEW.ActiveDeduplicationKey LIKE 'v1:%'
                BEGIN
                    SELECT RAISE(ABORT, 'simulated current identity-key write failure');
                END;
                """);
        }

        await Assert.ThrowsAsync<PersistenceException>(() =>
            CreatePersistence().ReconcileIdentityKeysAsync());

        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.MoveJobs.SingleAsync();
        Assert.Equal(originalKey, persisted.ActiveDeduplicationKey);
        Assert.Equal(MoveManifestIdentity.Version, persisted.IdentityKeyVersion);
        Assert.Equal(MoveJobStatus.Queued, persisted.Status);
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_DifferentSourcesRemainDistinctDespiteSharedTarget()
    {
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-conflict-{Guid.NewGuid():N}");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var first = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = target + "-source-a",
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:first",
                LeaseOwner = "worker-a",
                LeaseGeneration = 1,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            var second = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = target + "-source-b",
                Status = MoveJobStatus.RetryScheduled,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:second"
            };
            db.MoveJobs.AddRange(first, second);
            db.MoveJobEntries.AddRange(
                new MoveJobEntry
                {
                    MoveJobId = first.Id,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = 1,
                    Sha256 = new string('a', 64)
                },
                new MoveJobEntry
                {
                    MoveJobId = second.Id,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = 1,
                    Sha256 = new string('b', 64)
                },
                CreateTargetAuthorizationEntry(first.Id),
                CreateTargetAuthorizationEntry(second.Id));
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.AsNoTracking().ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, job => job.Status == MoveJobStatus.Running);
        Assert.Contains(jobs, job => job.Status == MoveJobStatus.RetryScheduled);
        Assert.All(jobs, job =>
        {
            Assert.NotNull(job.ActiveDeduplicationKey);
            Assert.Equal(MoveFailureKind.None, job.FailureKind);
        });
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_SameManifestWithoutAuthoritativeEvidence_RequiresAttention()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-no-evidence-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-no-evidence-target-{Guid.NewGuid():N}");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.AddRange(
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = source,
                    RequestedPath = target,
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.Planned,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:no-evidence-first",
                    Entries = CreateAuthorizedManifestEntries()
                },
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = source,
                    RequestedPath = target,
                    Status = MoveJobStatus.RetryScheduled,
                    Phase = MoveJobPhase.Planned,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:no-evidence-second",
                    Entries = CreateAuthorizedManifestEntries()
                });
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.AsNoTracking().ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains(
                "no authoritative recovery owner",
                job.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
        });
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_TargetAuthorizationState_IsNotExecutionEvidence()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-auth-state-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-auth-state-target-{Guid.NewGuid():N}");
        var firstEntries = CreateAuthorizedManifestEntries();
        firstEntries.Single(MoveManifestIdentity.IsTargetBoundaryAuthorization).CopyState =
            MoveJobEntryCopyState.Verified;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.AddRange(
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = source,
                    RequestedPath = target,
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.Planned,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:auth-state-first",
                    Entries = firstEntries
                },
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = source,
                    RequestedPath = target,
                    Status = MoveJobStatus.RetryScheduled,
                    Phase = MoveJobPhase.Planned,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:auth-state-second",
                    Entries = CreateAuthorizedManifestEntries()
                });
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.AsNoTracking().ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains(
                "no authoritative recovery owner",
                job.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
        });
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_SameManifestWithExecutionStateOnBothJobs_RequiresAttention()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-executed-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-executed-target-{Guid.NewGuid():N}");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var first = new MoveJob
            {
                AudiobookId = 42,
                SourcePath = source,
                RequestedPath = target,
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:executed-first",
                Entries = CreateAuthorizedManifestEntries(
                    copyState: MoveJobEntryCopyState.Staged)
            };
            var second = new MoveJob
            {
                AudiobookId = 42,
                SourcePath = source,
                RequestedPath = target,
                Status = MoveJobStatus.RetryScheduled,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:executed-second",
                Entries = CreateAuthorizedManifestEntries(
                    copyState: MoveJobEntryCopyState.Staged)
            };
            db.MoveJobs.AddRange(first, second);
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.AsNoTracking().ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains(
                "Multiple active move jobs own recovery evidence",
                job.Error,
                StringComparison.Ordinal);
            Assert.Null(job.ActiveDeduplicationKey);
        });
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_MalformedCurrentJobMarksNeedsAttentionAndContinues()
    {
        var throwingPath = Path.GetFullPath("/library/bad-book");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.AddRange(
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = Path.GetFullPath("/downloads/bad-book"),
                    RequestedPath = throwingPath,
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.None,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:bad",
                    Entries = CreateAuthorizedManifestEntries()
                },
                new MoveJob
                {
                    AudiobookId = 43,
                    SourcePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "downloads", "good-book")),
                    RequestedPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "library", "good-book")),
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.None,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:good",
                    Entries = CreateAuthorizedManifestEntries()
                });
            await db.SaveChangesAsync();
        }

        var resolver = BuildSemanticsResolver(path =>
            string.Equals(path, throwingPath, StringComparison.Ordinal)
                ? throw new InvalidOperationException("simulated invalid path")
                : null);
        var persistence = CreatePersistence(resolver);

        await persistence.ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var bad = await verification.MoveJobs.SingleAsync(job => job.AudiobookId == 42);
        var good = await verification.MoveJobs.SingleAsync(job => job.AudiobookId == 43);
        Assert.Equal(MoveJobStatus.NeedsAttention, bad.Status);
        Assert.Equal(MoveFailureKind.Verification, bad.FailureKind);
        Assert.Contains("Move path identity could not be reconciled", bad.Error, StringComparison.Ordinal);
        Assert.Null(bad.ActiveDeduplicationKey);
        Assert.Equal(MoveJobStatus.Queued, good.Status);
        Assert.StartsWith("v1:move-source:43:", good.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_ForeignCurrentPaths_ArePreservedAndRequireAttention()
    {
        var sourcePath = OperatingSystem.IsWindows()
            ? "/downloads/foreign-book"
            : @"C:\Downloads\foreign-book";
        var targetPath = OperatingSystem.IsWindows()
            ? "/library/foreign-book"
            : @"C:\Library\foreign-book";
        var jobId = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 44,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:foreign",
                LeaseOwner = "legacy-worker",
                LeaseGeneration = 2,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                Entries = CreateAuthorizedManifestEntries()
            });
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var job = await verification.MoveJobs.SingleAsync(candidate => candidate.Id == jobId);
        Assert.Equal(sourcePath, job.SourcePath);
        Assert.Equal(targetPath, job.RequestedPath);
        Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
        Assert.Equal(MoveFailureKind.Verification, job.FailureKind);
        Assert.Contains("filesystem syntax", job.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(job.ActiveDeduplicationKey);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
        Assert.False(job.TryGetSourceIdentity(out _));
        Assert.False(job.TryGetTargetIdentity(out _));
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_ForeignPersistedIdentity_RequiresAttention()
    {
        var syntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Unix
            : FileSystemPathSyntax.Windows;
        var sourcePath = syntax == FileSystemPathSyntax.Windows
            ? @"C:\Downloads\foreign-identity-book"
            : "/downloads/foreign-identity-book";
        var targetPath = syntax == FileSystemPathSyntax.Windows
            ? @"C:\Library\foreign-identity-book"
            : "/library/foreign-identity-book";
        var sourceBoundary = syntax == FileSystemPathSyntax.Windows ? @"C:\Downloads" : "/downloads";
        var targetBoundary = syntax == FileSystemPathSyntax.Windows ? @"C:\Library" : "/library";
        var job = new MoveJob
        {
            Id = Guid.NewGuid(),
            AudiobookId = 47,
            SourcePath = sourcePath,
            RequestedPath = targetPath,
            Status = MoveJobStatus.Running,
            Phase = MoveJobPhase.Copying,
            IdentityKeyVersion = MoveManifestIdentity.Version,
            ActiveDeduplicationKey = "stale:foreign-identity",
            LeaseOwner = "legacy-worker",
            LeaseGeneration = 2,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            Entries = CreateAuthorizedManifestEntries()
        };
        job.SetSourceIdentity(new PathIdentitySnapshot(
            syntax,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            sourceBoundary));
        job.SetTargetIdentity(new PathIdentitySnapshot(
            syntax,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            targetBoundary));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(job);
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.MoveJobs.SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(sourcePath, persisted.SourcePath);
        Assert.Equal(targetPath, persisted.RequestedPath);
        Assert.Equal(MoveJobStatus.NeedsAttention, persisted.Status);
        Assert.Equal(MoveFailureKind.Verification, persisted.FailureKind);
        Assert.Contains("persisted identity", persisted.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(persisted.ActiveDeduplicationKey);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_InvalidTarget_DoesNotPartiallyRewriteSource()
    {
        var sourcePath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            "reconcile-source",
            "book")) + Path.DirectorySeparatorChar;
        var targetPath = OperatingSystem.IsWindows()
            ? "/library/foreign-book"
            : @"C:\Library\foreign-book";
        var jobId = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 45,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.None,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:partial",
                Entries = CreateAuthorizedManifestEntries()
            });
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var job = await verification.MoveJobs.SingleAsync(candidate => candidate.Id == jobId);
        Assert.Equal(sourcePath, job.SourcePath);
        Assert.Equal(targetPath, job.RequestedPath);
        Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
        Assert.Null(job.ActiveDeduplicationKey);
        Assert.False(job.TryGetSourceIdentity(out _));
        Assert.False(job.TryGetTargetIdentity(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileIdentityKeysAsync_NavigationSegment_IsPreservedAndRequiresAttention(
        bool invalidSource)
    {
        var validSource = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-reconcile-navigation-source", "Title"));
        var validTarget = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-reconcile-navigation-target", "Title"));
        var invalidPath = OperatingSystem.IsWindows()
            ? @"C:\Listenarr\Source\..\Title"
            : "/listenarr/source/../Title";
        var sourcePath = invalidSource ? invalidPath : validSource;
        var targetPath = invalidSource ? validTarget : invalidPath;
        var jobId = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = invalidSource ? 48 : 49,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:navigation",
                LeaseOwner = "legacy-worker",
                LeaseGeneration = 7,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                Entries = CreateAuthorizedManifestEntries()
            });
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var job = await verification.MoveJobs.SingleAsync(candidate => candidate.Id == jobId);
        Assert.Equal(sourcePath, job.SourcePath);
        Assert.Equal(targetPath, job.RequestedPath);
        Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
        Assert.Equal(MoveFailureKind.Verification, job.FailureKind);
        Assert.Contains(invalidSource ? "Source" : "Target", job.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("navigation segment", job.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(job.ActiveDeduplicationKey);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Equal(7, job.LeaseGeneration);
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_RelativeCurrentPath_IsPreservedAndRequiresAttention()
    {
        const string sourcePath = "downloads/relative-book";
        var targetPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-relative-target"));
        var jobId = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 46,
                SourcePath = sourcePath,
                RequestedPath = targetPath,
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.None,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:relative",
                Entries = CreateAuthorizedManifestEntries()
            });
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var job = await verification.MoveJobs.SingleAsync(candidate => candidate.Id == jobId);
        Assert.Equal(sourcePath, job.SourcePath);
        Assert.Equal(targetPath, job.RequestedPath);
        Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
        Assert.Contains("not absolute", job.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(job.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task MarkNeedsAttentionAsync_RequiresExpectedStatusAndClearsActiveLease()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:unsafe-path");
        job.Status = MoveJobStatus.Running;
        job.LeaseOwner = "worker-a";
        job.LeaseGeneration = 3;
        job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
        await persistence.AddAsync(job);

        var staleUpdate = await persistence.MarkNeedsAttentionAsync(
            job.Id,
            MoveJobStatus.Failed,
            "unsafe persisted path",
            DateTimeOffset.UtcNow);

        Assert.False(staleUpdate);
        var unchanged = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Running, unchanged!.Status);
        Assert.Equal("worker-a", unchanged.LeaseOwner);
        Assert.NotNull(unchanged.ActiveDeduplicationKey);

        var updated = await persistence.MarkNeedsAttentionAsync(
            job.Id,
            MoveJobStatus.Running,
            "unsafe persisted path",
            DateTimeOffset.UtcNow);

        Assert.True(updated);
        var persisted = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.NeedsAttention, persisted!.Status);
        Assert.Equal(MoveFailureKind.Verification, persisted.FailureKind);
        Assert.Equal("unsafe persisted path", persisted.Error);
        Assert.Null(persisted.ActiveDeduplicationKey);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Equal(3, persisted.LeaseGeneration);
    }

    [Fact]
    public async Task TryClaimAsync_ConcurrentWorkers_OnlyOneAcquiresLease()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:claim");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;

        var claims = await Task.WhenAll(
            persistence.TryClaimAsync(job.Id, "worker-a", now, now.AddMinutes(2)),
            persistence.TryClaimAsync(job.Id, "worker-b", now, now.AddMinutes(2)));

        Assert.Single(claims, generation => generation.HasValue);
        var claimedJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Running, claimedJob!.Status);
        Assert.Contains(claimedJob.LeaseOwner, new[] { "worker-a", "worker-b" });
        Assert.Equal(1, claimedJob.LeaseGeneration);
    }

    [Fact]
    public async Task TryClaimAsync_ExpiredLease_IncrementsLeaseGeneration()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:reclaim");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(1, await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2)));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var claimedJob = await db.MoveJobs.SingleAsync(candidate => candidate.Id == job.Id);
            claimedJob.LeaseExpiresAt = now.AddSeconds(-1).UtcDateTime;
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await persistence.TryClaimAsync(
            job.Id,
            "worker-b",
            now,
            now.AddMinutes(2)));

        var reclaimedJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(2, reclaimedJob!.LeaseGeneration);
        Assert.Equal("worker-b", reclaimedJob.LeaseOwner);
    }

    [Fact]
    public async Task MatchingUnexpiredOwnership_CanHeartbeatAndUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:valid");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));
        Assert.Equal(1, generation);

        Assert.Equal(
            MoveHeartbeatOutcome.Renewed,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                generation.GetValueOrDefault(),
                now.AddSeconds(1),
                now.AddMinutes(3)));
        var beforeIncrement = await persistence.GetByIdAsync(job.Id);
        Assert.True(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            now.AddSeconds(1)));
        var afterIncrement = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(1, afterIncrement!.AttemptCount);
        Assert.Equal(beforeIncrement!.Status, afterIncrement.Status);
        Assert.Equal(beforeIncrement.Phase, afterIncrement.Phase);
        Assert.Equal(beforeIncrement.ActiveDeduplicationKey, afterIncrement.ActiveDeduplicationKey);
        Assert.Equal(beforeIncrement.LeaseOwner, afterIncrement.LeaseOwner);
        Assert.Equal(beforeIncrement.LeaseGeneration, afterIncrement.LeaseGeneration);
        Assert.Equal(beforeIncrement.LeaseExpiresAt, afterIncrement.LeaseExpiresAt);
        Assert.True(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now.AddSeconds(2)));

        var completed = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Completed, completed!.Status);
        Assert.Null(completed.ActiveDeduplicationKey);
        Assert.Null(completed.LeaseOwner);
        Assert.Null(completed.LeaseExpiresAt);
    }

    [Fact]
    public async Task ExpiredOwnership_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:expired");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddSeconds(1));
        Assert.Equal(1, generation);

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                generation.GetValueOrDefault(),
                now.AddSeconds(2),
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            now.AddSeconds(2)));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now.AddSeconds(2)));
        Assert.Equal(0, (await persistence.GetByIdAsync(job.Id))!.AttemptCount);
        Assert.Equal(2, await persistence.TryClaimAsync(
            job.Id,
            "worker-b",
            now.AddSeconds(2),
            now.AddMinutes(4)));
    }

    [Fact]
    public async Task WrongOwner_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:owner");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-b",
                generation.GetValueOrDefault(),
                now,
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-b",
            generation.GetValueOrDefault(),
            now));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-b",
            generation.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now));
        Assert.Equal(0, (await persistence.GetByIdAsync(job.Id))!.AttemptCount);
    }

    [Fact]
    public async Task NonRunningJob_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:queued");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                1,
                now,
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            1,
            now));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            1,
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now));
        Assert.Equal(0, (await persistence.GetByIdAsync(job.Id))!.AttemptCount);
    }

    [Fact]
    public async Task StaleLeaseGeneration_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:fenced");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var staleGeneration = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));
        Assert.Equal(1, staleGeneration);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var claimedJob = await db.MoveJobs.SingleAsync(candidate => candidate.Id == job.Id);
            claimedJob.LeaseExpiresAt = now.AddSeconds(-1).UtcDateTime;
            await db.SaveChangesAsync();
        }

        var currentGeneration = await persistence.TryClaimAsync(
            job.Id,
            "worker-b",
            now,
            now.AddMinutes(2));
        Assert.Equal(2, currentGeneration);

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                staleGeneration.GetValueOrDefault(),
                now,
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            staleGeneration.GetValueOrDefault(),
            now));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            staleGeneration.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now));

        var currentJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Running, currentJob!.Status);
        Assert.Equal("worker-b", currentJob.LeaseOwner);
        Assert.Equal(2, currentJob.LeaseGeneration);
        Assert.Equal(0, currentJob.AttemptCount);
    }

    [Fact]
    public async Task TerminalReconciliationState_WithSameGeneration_CannotBeOverwrittenByStaleWorker()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:superseded");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));
        Assert.Equal(1, generation);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var claimedJob = await db.MoveJobs.SingleAsync(candidate => candidate.Id == job.Id);
            claimedJob.Status = MoveJobStatus.Superseded;
            claimedJob.Error = "Superseded by reconciliation.";
            claimedJob.LeaseOwner = null;
            claimedJob.LeaseExpiresAt = null;
            await db.SaveChangesAsync();
        }

        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            MoveJobStatus.Failed,
            MoveJobPhase.Finalizing,
            "stale failure",
            MoveFailureKind.Unknown,
            now));

        var currentJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Superseded, currentJob!.Status);
        Assert.Equal("Superseded by reconciliation.", currentJob.Error);
    }

    [Theory]
    [InlineData("reconcile")]
    [InlineData("health")]
    [InlineData("requeue")]
    [InlineData("claim")]
    [InlineData("heartbeat")]
    public async Task ProviderFailure_IsTranslatedToPersistenceException(string operation)
    {
        var unavailablePath = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"missing-{Guid.NewGuid():N}",
            "move.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={unavailablePath};Mode=ReadWrite;Pooling=False")
            .Options;
        var persistence = new EfMoveQueuePersistence(
            new TestDbContextFactory(options),
            BuildSemanticsResolver());
        var job = CreateJob("v1:move:42:s:unavailable");
        var now = DateTimeOffset.UtcNow;

        Task operationTask = operation switch
        {
            "reconcile" => persistence.ReconcileIdentityKeysAsync(),
            "health" => persistence.GetHealthAsync(now),
            "requeue" => persistence.RequeueAsync(CreateRequeueCommand(
                job,
                "v1:move:42:s:unavailable-requeue")),
            "claim" => persistence.TryClaimAsync(job.Id, "worker-a", now, now.AddMinutes(2)),
            "heartbeat" => persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                1,
                now,
                now.AddMinutes(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        await Assert.ThrowsAsync<PersistenceException>(() => operationTask);
    }

    [Fact]
    public async Task RequeueAsync_ResetsRetryAndLeaseStateButPreservesRecoveryPhase()
    {
        var persistence = CreatePersistence();
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var job = CreateJob("v1:move:42:s:requeue-reset");
        job.Status = MoveJobStatus.Failed;
        job.Phase = MoveJobPhase.CleaningSource;
        job.Error = "verification failed";
        job.FailureKind = MoveFailureKind.Verification;
        job.NextAttemptAt = future.UtcDateTime;
        job.LeaseOwner = "worker-a";
        job.LeaseExpiresAt = future.UtcDateTime;
        job.LeaseGeneration = 3;
        job.AttemptCount = 2;
        await persistence.AddAsync(job);

        var result = await persistence.RequeueAsync(CreateRequeueCommand(
            job,
            "v1:move:42:s:requeue-reset-new"));

        Assert.Equal(MoveRequeueOutcome.Requeued, result.Outcome);
        var persisted = await persistence.GetByIdAsync(job.Id);
        Assert.NotNull(persisted);
        Assert.Equal(MoveJobStatus.Queued, persisted.Status);
        Assert.Equal(MoveJobPhase.CleaningSource, persisted.Phase);
        Assert.Null(persisted.Error);
        Assert.Equal(MoveFailureKind.None, persisted.FailureKind);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Equal("v1:move:42:s:requeue-reset-new", persisted.ActiveDeduplicationKey);
        Assert.Equal(3, persisted.LeaseGeneration);
        Assert.Equal(0, persisted.AttemptCount);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/book"), persisted.SourcePath);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/book"), persisted.RequestedPath);
        Assert.Equal(FileSystemPathSemantics.CurrentHostDefault.Syntax, persisted.SourcePathSyntax);
        Assert.Equal(FileSystemPathSemantics.CurrentHostDefault.Syntax, persisted.TargetPathSyntax);
        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.SourceCaseSensitivity);
        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.TargetCaseSensitivity);
        Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, persisted.SourceCaseSensitivityMode);
        Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, persisted.TargetCaseSensitivityMode);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/book"), persisted.SourceIdentityBoundary);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/book"), persisted.TargetIdentityBoundary);
        Assert.Equal(MoveManifestIdentity.Version, persisted.IdentityKeyVersion);
    }

    [Fact]
    public async Task RequeueAsync_ProcessRestartRecoversCommittedRepairBeforePublication()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:restart-requeue");
        job.Status = MoveJobStatus.NeedsAttention;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(
            job,
            "v1:move:42:s:restart-requeue-new");

        Assert.Equal(
            MoveRequeueOutcome.Requeued,
            (await persistence.RequeueAsync(command)).Outcome);
        var relocationService = new Mock<IRootFolderRelocationService>();
        relocationService.Setup(service => service.ReconcileActiveAsync(
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var restartedQueue = new MoveQueueService(
            Mock.Of<ILogger<MoveQueueService>>(),
            persistence,
            Mock.Of<IHubBroadcaster>(),
            TimeProvider.System,
            BuildSemanticsResolver(),
            relocationService.Object,
            Mock.Of<IFilesystemMutationCoordinator>());

        await restartedQueue.RecoverActiveJobsAsync();

        Assert.True(restartedQueue.Reader.TryRead(out var recovered));
        Assert.Equal(job.Id, recovered.Id);
        Assert.True(recovered.TryGetSourceIdentity(out var recoveredSourceIdentity));
        Assert.True(recovered.TryGetTargetIdentity(out var recoveredTargetIdentity));
        Assert.True(FileSystemPathIdentity.AreEquivalent(
            command.SourcePath,
            recovered.SourcePath,
            recoveredSourceIdentity.Semantics));
        Assert.True(FileSystemPathIdentity.AreEquivalent(
            command.TargetPath,
            recovered.RequestedPath,
            recoveredTargetIdentity.Semantics));
    }

    [Fact]
    public async Task RequeueAsync_ConcurrentClaim_DoesNotOverwriteNewerLeaseState()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:stale-requeue");
        job.Status = MoveJobStatus.Failed;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(
            job,
            "v1:move:42:s:stale-requeue-new");
        Assert.Equal(
            MoveRequeueOutcome.Requeued,
            (await persistence.RequeueAsync(command)).Outcome);
        var now = DateTimeOffset.UtcNow;
        var claimedGeneration = await persistence.TryClaimAsync(
            job.Id,
            "worker-new",
            now,
            now.AddMinutes(5));
        Assert.NotNull(claimedGeneration);

        var result = await persistence.RequeueAsync(command with
        {
            ExpectedStatus = MoveJobStatus.Queued
        });

        Assert.Equal(MoveRequeueOutcome.StaleState, result.Outcome);
        var persisted = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Running, persisted!.Status);
        Assert.Equal("worker-new", persisted.LeaseOwner);
        Assert.Equal(claimedGeneration, persisted.LeaseGeneration);
        Assert.Equal("v1:move:42:s:stale-requeue-new", persisted.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task RequeueAsync_DeduplicationCollision_ReturnsConflictingActiveJob()
    {
        var persistence = CreatePersistence();
        var failed = CreateJob("v1:move:42:s:failed-original");
        failed.Status = MoveJobStatus.Failed;
        failed.ActiveDeduplicationKey = null;
        var active = CreateJob("v1:move:42:s:collision");
        await persistence.AddAsync(failed);
        await persistence.AddAsync(active);

        var result = await persistence.RequeueAsync(CreateRequeueCommand(
            failed,
            "v1:move:42:s:collision"));

        Assert.Equal(MoveRequeueOutcome.ConflictingActiveJob, result.Outcome);
        Assert.Equal(active.Id, result.Job?.Id);
        Assert.Equal(MoveJobStatus.Failed, (await persistence.GetByIdAsync(failed.Id))?.Status);
    }

    [Fact]
    public async Task RequeueAsync_MatchingQueuedRepair_ReturnsIdempotentOutcome()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:matching-repair");
        job.Status = MoveJobStatus.Failed;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(job, "v1:move:42:s:matching-repair-new");
        var first = await persistence.RequeueAsync(command);
        var second = await persistence.RequeueAsync(command with
        {
            ExpectedStatus = MoveJobStatus.Queued
        });

        Assert.Equal(MoveRequeueOutcome.Requeued, first.Outcome);
        Assert.Equal(MoveRequeueOutcome.AlreadyQueuedWithMatchingIdentity, second.Outcome);
        Assert.Equal(job.Id, second.Job?.Id);
    }

    [Fact]
    public async Task RequeueAsync_ConcurrentRequests_OnlyOneExpectedStateTransitionWins()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v1:move:42:s:concurrent-requeue");
        job.Status = MoveJobStatus.Failed;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(job, "v1:move:42:s:concurrent-requeue-new");

        var results = await Task.WhenAll(
            persistence.RequeueAsync(command),
            persistence.RequeueAsync(command));

        Assert.Single(results, result => result.Outcome == MoveRequeueOutcome.Requeued);
        Assert.Single(results, result => result.Outcome == MoveRequeueOutcome.StaleState);
        Assert.Equal(MoveJobStatus.Queued, (await persistence.GetByIdAsync(job.Id))?.Status);
    }

    [Theory]
    [InlineData(
        FileSystemPathSyntax.Windows,
        FileSystemCaseSensitivity.Sensitive,
        FileSystemCaseSensitivityMode.Sensitive,
        @"\\server\downloads\Book",
        @"\\server\library\Book",
        @"\\server\downloads",
        @"\\server\library")]
    [InlineData(
        FileSystemPathSyntax.Windows,
        FileSystemCaseSensitivity.Insensitive,
        FileSystemCaseSensitivityMode.Insensitive,
        @"C:\Downloads\Book",
        @"C:\Library\Book",
        @"C:\Downloads",
        @"C:\Library")]
    [InlineData(
        FileSystemPathSyntax.Unix,
        FileSystemCaseSensitivity.Sensitive,
        FileSystemCaseSensitivityMode.Sensitive,
        "/downloads/Author Name/Book",
        "/library/Author Name/Book",
        "/downloads",
        "/library")]
    [InlineData(
        FileSystemPathSyntax.Unix,
        FileSystemCaseSensitivity.Insensitive,
        FileSystemCaseSensitivityMode.Insensitive,
        "/mnt/Downloads/Book",
        "/mnt/Library/Book",
        "/mnt/Downloads",
        "/mnt/Library")]
    public async Task RequeueAsync_PersistsExplicitEndpointSemantics(
        FileSystemPathSyntax syntax,
        FileSystemCaseSensitivity sensitivity,
        FileSystemCaseSensitivityMode requestedMode,
        string sourcePath,
        string targetPath,
        string sourceBoundary,
        string targetBoundary)
    {
        var persistence = CreatePersistence();
        var job = CreateJob($"legacy:{Guid.NewGuid():N}");
        job.Status = MoveJobStatus.NeedsAttention;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var sourceIdentity = new PathIdentitySnapshot(
            syntax,
            sensitivity,
            requestedMode,
            sourceBoundary);
        var targetIdentity = new PathIdentitySnapshot(
            syntax,
            sensitivity,
            requestedMode,
            targetBoundary);

        var result = await persistence.RequeueAsync(new RequeueMoveCommand(
            job.Id,
            MoveJobStatus.NeedsAttention,
            sourcePath,
            sourceIdentity,
            targetPath,
            targetIdentity,
            $"v1:move:42:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow));

        Assert.Equal(MoveRequeueOutcome.Requeued, result.Outcome);
        Assert.Equal(syntax, result.Job?.SourcePathSyntax);
        Assert.Equal(syntax, result.Job?.TargetPathSyntax);
        Assert.Equal(sensitivity, result.Job?.SourceCaseSensitivity);
        Assert.Equal(sensitivity, result.Job?.TargetCaseSensitivity);
        Assert.Equal(requestedMode, result.Job?.SourceCaseSensitivityMode);
        Assert.Equal(requestedMode, result.Job?.TargetCaseSensitivityMode);
    }

    private EfMoveQueuePersistence CreatePersistence(IFileSystemSemanticsResolver? resolver = null) =>
        new(_factory, resolver ?? BuildSemanticsResolver());

    private static IFileSystemSemanticsResolver BuildSemanticsResolver(
        Func<string, Exception?>? exceptionFactory = null)
    {
        var resolver = new Mock<IFileSystemSemanticsResolver>();
        resolver.Setup(service => service.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
            {
                if (exceptionFactory?.Invoke(path) is { } exception)
                {
                    throw exception;
                }

                return ValueTask.FromResult(new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, FileSystemCaseSensitivity.Sensitive),
                    PathIdentityState.Valid,
                    path));
            });
        return resolver.Object;
    }

    private static MoveJobEntry CreateManifestEntry(
        string hashCharacter = "A",
        MoveJobEntryCopyState copyState = MoveJobEntryCopyState.Pending,
        MoveJobEntryCleanupState cleanupState = MoveJobEntryCleanupState.Pending) =>
        new()
        {
            RelativePath = "book.m4b",
            EntryType = MoveJobEntryType.File,
            Length = 1,
            LastWriteTimeUtc = DateTime.UnixEpoch,
            Sha256 = string.Concat(Enumerable.Repeat(hashCharacter, 64)),
            CopyState = copyState,
            CleanupState = cleanupState
        };

    private static List<MoveJobEntry> CreateAuthorizedManifestEntries(
        string hashCharacter = "A",
        MoveJobEntryCopyState copyState = MoveJobEntryCopyState.Pending,
        MoveJobEntryCleanupState cleanupState = MoveJobEntryCleanupState.Pending) =>
        [
            CreateManifestEntry(hashCharacter, copyState, cleanupState),
            MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                ManagedDirectoryIdentity.CurrentVersion,
                "test-target-generation")
        ];

    private static MoveJobEntry CreateTargetAuthorizationEntry(
        Guid jobId,
        string targetGeneration = "test-target-generation")
    {
        var entry = MoveManifestIdentity.CreateTargetBoundaryAuthorization(
            ManagedDirectoryIdentity.CurrentVersion,
            targetGeneration);
        entry.MoveJobId = jobId;
        return entry;
    }

    private static MoveJob CreateJob(string key) => new()
    {
        AudiobookId = 42,
        RequestedPath = "/library/book",
        Status = MoveJobStatus.Queued,
        ActiveDeduplicationKey = key,
        Entries =
        [
            CreateManifestEntry(),
            MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                ManagedDirectoryIdentity.CurrentVersion,
                "test-target-generation")
        ]
    };

    private static RequeueMoveCommand CreateRequeueCommand(
        MoveJob job,
        string key)
    {
        var sourcePath = FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/book");
        var targetPath = FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/book");
        var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
        var sourceIdentity = new PathIdentitySnapshot(
            syntax,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            sourcePath);
        var targetIdentity = new PathIdentitySnapshot(
            syntax,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            targetPath);
        return new RequeueMoveCommand(
            job.Id,
            job.Status,
            sourcePath,
            sourceIdentity,
            targetPath,
            targetIdentity,
            key,
            DateTimeOffset.UtcNow);
    }

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
