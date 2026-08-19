using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileMoverMarkerlessRegistrationTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileMoverMarkerlessRegistrationTests : BaseTests
{
    [Fact]
    public async Task CheckPublicationSource_ExistingStableFile_ReturnsSupported()
    {
        var scenario = await CreateScenarioAsync("registration-source-capability");
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(CreateMover());

        var result = await capability.CheckAsync(scenario.Source);

        Assert.True(result.IsSupported, result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.PhysicalObjectIdentity));
    }

    [Fact]
    public async Task PrepareRegistration_ReadOnlyDestination_BlocksBeforeJournalCreation()
    {
        var scenario = await CreateScenarioAsync("registration-readonly-destination");
        var mover = CreateMover(readOnlyFileSystemProbe: _ => true);
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(mover);
        var sourceProof = await capability.CheckAsync(scenario.Source);
        Assert.True(sourceProof.IsSupported, sourceProof.Reason);
        Assert.True(sourceProof.SourceProof.HasValue);

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            sourceProof.SourceProof.Value);

        Assert.Null(lease);
        Assert.False(File.Exists(scenario.Destination));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.DoesNotContain(
            db.FileMutationJournals,
            journal => journal.OperationId == scenario.OperationId);
    }

    [Fact]
    public async Task PrepareRegistration_ManagedDestinationWithoutMutationCapability_BlocksBeforeJournalCreation()
    {
        var scenario = await CreateScenarioAsync("registration-managed-capability-blocked");
        var root = new RootFolder
        {
            Id = 41,
            Name = "Managed Root",
            Path = scenario.Root,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            ResolvedCaseSensitivity = FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
            PathIdentityState = PathIdentityState.Valid
        };
        var rootRepository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        rootRepository
            .Setup(repository => repository.GetAllAsync())
            .ReturnsAsync([root]);
        var storageHealth = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        storageHealth
            .Setup(resolver => resolver.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderStorageObservation(
                RootFolderStorageState.Limited,
                RootFolderStorageReason.MutationSemanticsUnproven,
                "Select Sensitive or Insensitive explicitly.",
                CanConfirmCurrentFolder: false,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: null));
        var mover = CreateMover(
            readOnlyFileSystemProbe: _ => false,
            rootFolderRepository: rootRepository.Object,
            rootFolderStorageHealthResolver: storageHealth.Object);
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(mover);
        var sourceProof = await capability.CheckAsync(scenario.Source);
        Assert.True(sourceProof.IsSupported, sourceProof.Reason);
        Assert.True(sourceProof.SourceProof.HasValue);

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            sourceProof.SourceProof.Value);

        Assert.Null(lease);
        Assert.False(File.Exists(scenario.Destination));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.DoesNotContain(
            db.FileMutationJournals,
            journal => journal.OperationId == scenario.OperationId);
        rootRepository.VerifyAll();
        storageHealth.VerifyAll();
    }

    [Fact]
    public async Task PrepareRegistration_ManagedDestinationWithMutationCapability_PublishesNormally()
    {
        var scenario = await CreateScenarioAsync("registration-managed-capability-allowed");
        var root = new RootFolder
        {
            Id = 42,
            Name = "Managed Root",
            Path = scenario.Root,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
            PathIdentityState = PathIdentityState.Valid
        };
        var rootRepository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        rootRepository
            .Setup(repository => repository.GetAllAsync())
            .ReturnsAsync([root]);
        var storageHealth = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        storageHealth
            .Setup(resolver => resolver.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderStorageObservation(
                RootFolderStorageState.Healthy,
                RootFolderStorageReason.None,
                Message: null,
                CanConfirmCurrentFolder: false,
                CanChangePath: true,
                CanMutateFilesystem: true,
                ConfirmationToken: null));
        var mover = CreateMover(
            readOnlyFileSystemProbe: _ => false,
            rootFolderRepository: rootRepository.Object,
            rootFolderStorageHealthResolver: storageHealth.Object);

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        Assert.True(File.Exists(scenario.Destination));
        rootRepository.VerifyAll();
        storageHealth.VerifyAll();
    }

    [Fact]
    public async Task PerformMove_ReadOnlySamePath_RemainsIdempotentWithoutJournal()
    {
        var scenario = await CreateScenarioAsync("move-readonly-same-path");
        var mover = CreateMover(readOnlyFileSystemProbe: _ => true);

        var result = await mover.PerformActionOn(
            FileAction.Move,
            scenario.Source,
            scenario.Source,
            scenario.OperationId);

        Assert.True(result);
        Assert.True(File.Exists(scenario.Source));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.DoesNotContain(
            db.FileMutationJournals,
            journal => journal.OperationId == scenario.OperationId);
    }

    [Fact]
    public async Task PrepareRegistration_SourceGenerationChangesAfterCapabilityProof_DoesNotPublishReplacement()
    {
        var scenario = await CreateScenarioAsync(
            "registration-source-generation-race");
        var mover = CreateMover();
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(mover);
        var sourceProof = await capability.CheckAsync(scenario.Source);
        Assert.True(sourceProof.IsSupported, sourceProof.Reason);
        Assert.True(sourceProof.SourceProof.HasValue);

        File.Delete(scenario.Source);
        await File.WriteAllTextAsync(scenario.Source, "replacement-generation");

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            sourceProof.SourceProof.Value);

        Assert.Null(lease);
        Assert.False(File.Exists(scenario.Destination));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.DoesNotContain(
            db.FileMutationJournals,
            journal => journal.OperationId == scenario.OperationId);
    }

    [Fact]
    public async Task PrepareRegistration_SourceContentChangesAfterCapabilityProof_DoesNotPublishRewrittenGeneration()
    {
        var scenario = await CreateScenarioAsync(
            "registration-source-content-race");
        var mover = CreateMover();
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(mover);
        var sourceCapability = await capability.CheckAsync(scenario.Source);
        Assert.True(sourceCapability.IsSupported, sourceCapability.Reason);
        Assert.True(sourceCapability.SourceProof.HasValue);

        await File.WriteAllTextAsync(scenario.Source, "muted");
        var rewrittenCapability = await capability.CheckAsync(scenario.Source);
        Assert.True(rewrittenCapability.IsSupported, rewrittenCapability.Reason);
        Assert.True(rewrittenCapability.SourceProof.HasValue);
        Assert.Equal(
            sourceCapability.SourceProof.Value.PhysicalObjectIdentity,
            rewrittenCapability.SourceProof.Value.PhysicalObjectIdentity);
        Assert.Equal(
            sourceCapability.SourceProof.Value.Length,
            rewrittenCapability.SourceProof.Value.Length);
        Assert.NotEqual(
            sourceCapability.SourceProof.Value.Sha256,
            rewrittenCapability.SourceProof.Value.Sha256);

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            sourceCapability.SourceProof.Value);

        Assert.Null(lease);
        Assert.False(File.Exists(scenario.Destination));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.DoesNotContain(
            db.FileMutationJournals,
            journal => journal.OperationId == scenario.OperationId);
    }

    [Fact]
    public async Task CheckPublicationSource_MissingFile_ReturnsUnsupported()
    {
        var scenario = await CreateScenarioAsync("registration-source-capability-missing");
        File.Delete(scenario.Source);
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(CreateMover());

        var result = await capability.CheckAsync(scenario.Source);

        Assert.False(result.IsSupported);
        Assert.Equal(
            FilePublicationSourceCapabilityFailureKind.Missing,
            result.FailureKind);
        Assert.NotNull(result.Reason);
        Assert.False(File.Exists(scenario.Destination));
    }

    [WindowsFact]
    public async Task CheckPublicationSource_SharingViolation_ReturnsUnavailable()
    {
        var scenario = await CreateScenarioAsync(
            "registration-source-capability-sharing-violation");
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(CreateMover());
        await using var sourceLock = new FileStream(
            scenario.Source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = await capability.CheckAsync(scenario.Source);

        Assert.False(result.IsSupported);
        Assert.Equal(
            FilePublicationSourceCapabilityFailureKind.Unavailable,
            result.FailureKind);
        Assert.NotNull(result.Reason);
        Assert.False(File.Exists(scenario.Destination));
    }

    [LinuxFact]
    public async Task CheckPublicationSource_Directory_ReturnsUnsupported()
    {
        var scenario = await CreateScenarioAsync("registration-source-capability-directory");
        var directorySource = Path.Join(scenario.Root, "directory-source");
        Directory.CreateDirectory(directorySource);
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(CreateMover());

        var result = await capability.CheckAsync(directorySource);

        Assert.False(result.IsSupported);
        Assert.Equal(
            FilePublicationSourceCapabilityFailureKind.Unsupported,
            result.FailureKind);
        Assert.NotNull(result.Reason);
        Assert.False(File.Exists(scenario.Destination));
    }

    [DirectoryLinkFact]
    public async Task CheckPublicationSource_LinkedAncestor_ReturnsUnsupported()
    {
        var scenario = await CreateScenarioAsync("registration-source-capability-linked-ancestor");
        var physicalParent = Path.Join(scenario.Root, "physical-parent");
        var linkedParent = Path.Join(scenario.Root, "linked-parent");
        Directory.CreateDirectory(physicalParent);
        Directory.CreateSymbolicLink(linkedParent, physicalParent);
        var source = Path.Join(linkedParent, "book.m4b");
        await File.WriteAllTextAsync(Path.Join(physicalParent, "book.m4b"), "audio");
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(CreateMover());

        var result = await capability.CheckAsync(source);

        Assert.False(result.IsSupported);
        Assert.NotNull(result.Reason);
        Assert.False(File.Exists(scenario.Destination));
    }

    [LinuxFact]
    public async Task CompleteCopy_TargetReplacedAfterLeaseOpened_MarksNeedsAttention()
    {
        var scenario = await CreateScenarioAsync(
            "registration-copy-target-replaced-before-completion");
        var mover = CreateMover();
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(64));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        File.Delete(scenario.Destination);
        await File.WriteAllTextAsync(scenario.Destination, "foreign-target");

        Assert.Equal(
            RegistrationPublicationCompletion.CommittedCleanupPending,
            lease.CompletePublication());
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 64);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task PerformCopy_PublicationUnavailableAfterCompletion_RemainsCompleted()
    {
        var scenario = await CreateScenarioAsync("registration-copy-completion-unavailable");
        var mover = CreateMover(
            publicationProbeOutcome:
                RegistrationPublicationMatchOutcome.Unavailable);

        Assert.True(await mover.PerformActionOn(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: null);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PerformCopy_TargetReplacedDuringCompletedCommit_MarksNeedsAttention()
    {
        var scenario = await CreateScenarioAsync(
            "registration-copy-target-replaced-during-completed-commit");
        var mover = CreateMover(
            beforeCompletedJournalCommit: () =>
            {
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            });

        Assert.False(await mover.PerformActionOn(
            FileAction.Copy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId));

        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: null);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task PrepareMove_EmptyOperationId_FailsClosedWithoutPublication()
    {
        var scenario = await CreateScenarioAsync("registration-empty-operation-id");
        var mover = CreateMover();

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            Guid.Empty);

        Assert.Null(lease);
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.FileMutationJournals.ToListAsync());
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PrepareMove_ForcedCrossVolumeRejectsBeforePublicationOrJournalCreation()
    {
        var scenario = await CreateScenarioAsync("registration-cross-volume-blocked");
        var mover = CreateMover(forceCrossVolume: true);

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.Null(lease);
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.FileMutationJournals
            .AsNoTracking()
            .AnyAsync(candidate => candidate.OperationId == scenario.OperationId));
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task PrepareMove_RequiresRegistrationCommitBeforeSourceDeletion()
    {
        var scenario = await CreateScenarioAsync("move-authority");
        var mover = CreateMover();

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.True(File.Exists(scenario.Source));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        Assert.True(lease.PrepareCleanupRecovery(17));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.RegistrationCommitted,
            audiobookId: 17);

        Assert.True(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 17);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PrepareMove_GenerationPreservingLinkUnavailable_DoesNotFallBackToCopy()
    {
        var scenario = await CreateScenarioAsync(
            "registration-move-generation-link-unavailable");
        var mover = CreateMover(
            beforePinnedHardlinkCreation: () =>
                throw new IOException("Injected hardlink failure."));

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            mover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                scenario.Source,
                scenario.Destination,
                scenario.OperationId));

        Assert.Contains(
            "could not be published safely",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            audiobookId: null);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PrepareMove_SameVolumePublishesExactSourceGenerationBeforeRegistration()
    {
        var scenario = await CreateScenarioAsync(
            "registration-move-generation-preserving-publication");
        var mover = CreateMover();

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
        Assert.Equal(FileMutationJournalState.TargetVerified, journal.State);
        Assert.False(string.IsNullOrWhiteSpace(journal.SourceSha256));
        Assert.Equal(
            journal.SourcePhysicalObjectIdentity,
            journal.TargetPhysicalObjectIdentity);
        Assert.Equal(
            journal.TargetPhysicalObjectIdentity,
            lease.PhysicalObjectIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PrepareMove_InterruptedAfterGenerationLinkCreation_RetryAdoptsPublishedGeneration()
    {
        var scenario = await CreateScenarioAsync(
            "registration-move-interrupted-generation-link");
        var firstMover = CreateMover(
            afterRegistrationTargetCreatedBeforeState: () =>
                throw new IOException("Injected publication state interruption."));

        await Assert.ThrowsAsync<IOException>(() =>
            firstMover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                scenario.Source,
                scenario.Destination,
                scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            audiobookId: null);

        var retryMover = CreateMover();
        using var retryLease = await retryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(retryLease);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);
        Assert.True(retryLease.PrepareCleanupRecovery(18));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            retryLease.CompletePublication());
        Assert.True(await retryMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            retryLease,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 18);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PrepareMove_ExistingJournal_RemainsRecoverableWhenReadOnlyProbeBlocksNewMutations()
    {
        var scenario = await CreateScenarioAsync(
            "registration-move-readonly-recovery");
        var firstMover = CreateMover(
            afterRegistrationTargetCreatedBeforeState: () =>
                throw new IOException("Injected publication state interruption."));

        await Assert.ThrowsAsync<IOException>(() =>
            firstMover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                scenario.Source,
                scenario.Destination,
                scenario.OperationId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Planned,
            audiobookId: null);

        var retryMover = CreateMover(readOnlyFileSystemProbe: _ => true);
        using var lease = await retryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);
    }

    [LinuxFact]
    public async Task CompleteMove_SourceChangesAfterFinalProof_PreservesChangedGeneration()
    {
        var scenario = await CreateScenarioAsync(
            "registration-move-source-changes-after-final-proof");
        var originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(scenario.Source);
        var mover = CreateMover(
            beforeRegistrationSourceDelete: () =>
            {
                File.WriteAllText(scenario.Source, "other");
                File.SetLastWriteTimeUtc(
                    scenario.Source,
                    originalLastWriteTimeUtc);
                return Task.CompletedTask;
            });

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(19));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        var completed = await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId);

        Assert.False(completed);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("other", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 19);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task PrepareHardlinkCopy_SameVolumePersistsHashlessSourceProof()
    {
        var scenario = await CreateScenarioAsync("registration-hardlink-hashless");
        var mover = CreateMover();

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.HardlinkCopy,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
        Assert.Equal(FileMutationJournalState.TargetVerified, journal.State);
        Assert.Null(journal.SourceSha256);
        Assert.Equal(
            journal.SourcePhysicalObjectIdentity,
            journal.TargetPhysicalObjectIdentity);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task PrepareCopy_CommittedRegistrationCompletesJournalWithoutSourceMutation(
        FileAction action)
    {
        var scenario = await CreateScenarioAsync($"registration-{action}");
        var mover = CreateMover();

        using var lease = await mover.PrepareActionForRegistrationAsync(
            action,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        Assert.True(lease.MatchesCurrentPublication());
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        Assert.True(lease.PrepareCleanupRecovery(23));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 23);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [WindowsFact]
    public async Task PrepareMove_CaseAliasRetryReusesJournalAndCompletes()
    {
        var scenario = await CreateScenarioAsync("registration-case-alias-retry");
        var firstMover = CreateMover();
        string targetIdentity;
        using (var firstLease = await firstMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId))
        {
            Assert.NotNull(firstLease);
            targetIdentity = firstLease.PhysicalObjectIdentity;
        }

        var retryMover = CreateMover();
        using var retryLease = await retryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source.ToUpperInvariant(),
            scenario.Destination.ToUpperInvariant(),
            scenario.OperationId,
            targetIdentity);

        Assert.NotNull(retryLease);
        Assert.True(retryLease.PrepareCleanupRecovery(29));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            retryLease.CompletePublication());
        Assert.True(await retryMover.CompletePreparedMoveAsync(
            scenario.Source.ToUpperInvariant(),
            scenario.Destination.ToUpperInvariant(),
            retryLease,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 29);
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.FileMutationJournals.ToListAsync());
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PrepareMove_RetryAcceptsCompatibleMergedV1JournalAndPreferredExpectedToken()
    {
        var scenario = await CreateScenarioAsync(
            "registration-compatible-v1-retry");
        var firstMover = CreateMover();
        string preferredTargetIdentity;
        using (var firstLease = await firstMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId))
        {
            Assert.NotNull(firstLease);
            preferredTargetIdentity = firstLease.PhysicalObjectIdentity;
        }

        Assert.StartsWith(
            "linux-generation:",
            preferredTargetIdentity,
            StringComparison.Ordinal);
        var mergedV1TargetIdentity =
            LinuxIdentityTestHelper.ToMergedV1AugmentedIdentity(
                preferredTargetIdentity);
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals.SingleAsync(
                candidate => candidate.OperationId == scenario.OperationId);
            Assert.Equal(
                FileMutationJournalState.TargetVerified,
                journal.State);
            journal.TargetPhysicalObjectIdentity = mergedV1TargetIdentity;
            await db.SaveChangesAsync();
        }

        var retryMover = CreateMover();
        using var retryLease = await retryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            preferredTargetIdentity);

        Assert.NotNull(retryLease);
        Assert.True(retryLease.MatchesPhysicalObjectIdentity(
            mergedV1TargetIdentity));
        Assert.True(retryLease.MatchesCurrentPublication());
        Assert.True(retryLease.PrepareCleanupRecovery(30));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            retryLease.CompletePublication());
        Assert.True(await retryMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            retryLease,
            scenario.OperationId));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 30);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task PrepareMove_RetryAfterOwnershipCommitGapReusesVerifiedGeneration()
    {
        var scenario = await CreateScenarioAsync("registration-retry");
        var firstMover = CreateMover();
        string targetIdentity;
        using (var firstLease = await firstMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId))
        {
            Assert.NotNull(firstLease);
            targetIdentity = firstLease.PhysicalObjectIdentity;
        }

        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.TargetVerified,
            audiobookId: null);

        var retryMover = CreateMover();
        using var retryLease = await retryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            targetIdentity);

        Assert.NotNull(retryLease);
        Assert.Equal(targetIdentity, retryLease.PhysicalObjectIdentity);
        Assert.True(retryLease.PrepareCleanupRecovery(31));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            retryLease.CompletePublication());
        Assert.True(await retryMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            retryLease,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 31);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task CompleteMove_TargetPublicationUnavailable_RemainsRegistrationCommittedForRetry()
    {
        var scenario = await CreateScenarioAsync("registration-target-unavailable");
        var preparingMover = CreateMover();
        using var lease = await preparingMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(36));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        var unavailableMover = CreateMover(
            publicationProbeOutcome:
                RegistrationPublicationMatchOutcome.Unavailable);
        Assert.False(await unavailableMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.RegistrationCommitted,
            audiobookId: 36);

        Assert.True(await preparingMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 36);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [WindowsFact]
    public async Task CompleteMove_SourceSharingViolationDoesNotAdvanceDeletionState()
    {
        var scenario = await CreateScenarioAsync("registration-sharing-violation");
        var mover = CreateMover();
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(37));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        await using (var sourceLock = new FileStream(
            scenario.Source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            Assert.False(await mover.CompletePreparedMoveAsync(
                scenario.Source,
                scenario.Destination,
                lease,
                scenario.OperationId));

            Assert.True(File.Exists(scenario.Source));
            Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
            await AssertJournalStateAsync(
                scenario.OperationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                audiobookId: 37);
        }

        Assert.True(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 37);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task CompleteMove_CrashAfterSourceDeletionResumesFromDatabaseAuthorization()
    {
        var scenario = await CreateScenarioAsync("registration-delete-crash");
        var crashingMover = CreateMover(
            afterSourceDeletedBeforeState: () =>
                throw new IOException("Injected crash after source deletion."));
        using var lease = await crashingMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(41));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.False(await crashingMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));
        Assert.False(File.Exists(scenario.Source));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.SourceDeletionAuthorized,
            audiobookId: 41);

        var recoveryMover = CreateMover();
        using var recoveryLease = await recoveryMover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            lease.PhysicalObjectIdentity);
        Assert.NotNull(recoveryLease);
        Assert.True(recoveryLease.PrepareCleanupRecovery(41));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            recoveryLease.CompletePublication());
        Assert.True(await recoveryMover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            recoveryLease,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.Completed,
            audiobookId: 41);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task CompleteMove_MissingDurableJournal_FailsClosedWithoutFilesystemFallback()
    {
        var scenario = await CreateScenarioAsync("registration-journal-missing");
        var mover = CreateMover();
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(47));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
            db.FileMutationJournals.Remove(journal);
            await db.SaveChangesAsync();
        }

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task CompleteMove_ReplacedSourceIsPreservedAndJournalNeedsAttention()
    {
        var scenario = await CreateScenarioAsync("registration-source-replaced");
        var mover = CreateMover();
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(53));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        File.Delete(scenario.Source);
        await File.WriteAllTextAsync(scenario.Source, "foreign-source");

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));

        Assert.Equal("foreign-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 53);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task CompleteMove_SourceRecreatedAfterSourceDeletedState_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync(
            "markerless-registration-source-recreated-after-state");
        var mover = CreateMover(
            afterSourceDeletedState: () =>
            {
                File.WriteAllText(scenario.Source, "replacement");
                return Task.CompletedTask;
            });
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(62));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));

        Assert.Equal("replacement", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 62);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task CompleteMove_TargetReplacedAfterSourceDeletedState_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync(
            "markerless-registration-target-replaced-after-state");
        var mover = CreateMover(
            afterSourceDeletedState: () =>
            {
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            });
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(63));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 63);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task CompleteMove_TargetReplacedDuringCompletedCommit_DoesNotComplete()
    {
        var scenario = await CreateScenarioAsync(
            "markerless-registration-target-replaced-during-completed-commit");
        var mover = CreateMover(
            beforeCompletedJournalCommit: () =>
            {
                File.Delete(scenario.Destination);
                File.WriteAllText(scenario.Destination, "foreign-target");
                return Task.CompletedTask;
            });
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(64));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.False(await mover.CompletePreparedMoveAsync(
            scenario.Source,
            scenario.Destination,
            lease,
            scenario.OperationId));

        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        await AssertJournalStateAsync(
            scenario.OperationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 64);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task CompleteMove_SourceParentReplacedAfterSourceDeletedState_DoesNotComplete()
    {
        var sourceParent = FileService.GetTempDirectory(
            "registration-parent-replaced-after-state-source");
        var displacedSourceParent = sourceParent + "-displaced";
        var destinationParent = FileService.GetTempDirectory(
            "registration-parent-replaced-after-state-destination");
        var source = Path.Join(sourceParent, "source.m4b");
        var destination = Path.Join(destinationParent, "published.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();

        var mover = CreateMover(
            afterSourceDeletedState: async () =>
            {
                Directory.Move(sourceParent, displacedSourceParent);
                Directory.CreateDirectory(sourceParent);
                await File.WriteAllTextAsync(source, "replacement");
            });
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            source,
            destination,
            operationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(61));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.False(await mover.CompletePreparedMoveAsync(
            source,
            destination,
            lease,
            operationId));

        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 61);
        AssertNoLibraryArtifacts(sourceParent);
        AssertNoLibraryArtifacts(displacedSourceParent);
        AssertNoLibraryArtifacts(destinationParent);
    }

    [LinuxFact]
    public async Task CompleteMove_SourceParentReplacedAfterDeleteBeforeState_DoesNotComplete()
    {
        var sourceParent = FileService.GetTempDirectory(
            "registration-parent-replaced-source");
        var displacedSourceParent = sourceParent + "-displaced";
        var destinationParent = FileService.GetTempDirectory(
            "registration-parent-replaced-destination");
        var source = Path.Join(sourceParent, "source.m4b");
        var destination = Path.Join(destinationParent, "published.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var operationId = Guid.NewGuid();
        var replacementAttempted = false;
        Exception? replacementFailure = null;

        var mover = CreateMover(
            afterSourceDeletedBeforeState: async () =>
            {
                replacementAttempted = true;
                try
                {
                    Directory.Move(sourceParent, displacedSourceParent);
                    Directory.CreateDirectory(sourceParent);
                    await File.WriteAllTextAsync(source, "replacement");
                }
                catch (Exception exception)
                {
                    replacementFailure = exception;
                    throw;
                }
            });
        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            source,
            destination,
            operationId);
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(59));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.False(await mover.CompletePreparedMoveAsync(
            source,
            destination,
            lease,
            operationId));

        Assert.True(replacementAttempted);
        Assert.Null(replacementFailure);
        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            audiobookId: 59);
        AssertNoLibraryArtifacts(sourceParent);
        AssertNoLibraryArtifacts(displacedSourceParent);
        AssertNoLibraryArtifacts(destinationParent);
    }

    private FileMover CreateMover(
        Func<Task>? afterSourceDeletedBeforeState = null,
        Func<Task>? afterSourceDeletedState = null,
        bool forceCrossVolume = false,
        RegistrationPublicationMatchOutcome? publicationProbeOutcome = null,
        Func<Task>? beforeCompletedJournalCommit = null,
        Func<Task>? beforeRegistrationSourceDelete = null,
        Func<Task>? afterRegistrationTargetCreatedBeforeState = null,
        Func<Task>? beforePinnedHardlinkCreation = null,
        Func<string, bool?>? readOnlyFileSystemProbe = null,
        IRootFolderRepository? rootFolderRepository = null,
        IRootFolderStorageHealthResolver? rootFolderStorageHealthResolver = null)
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            new NullLogger<FileMover>(),
            dbContextFactory: factory,
            timeProvider: TimeProvider.System,
            readOnlyFileSystemProbe: readOnlyFileSystemProbe,
            rootFolderRepository: rootFolderRepository,
            rootFolderStorageHealthResolver: rootFolderStorageHealthResolver)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "file-mover-markerless-registration-locks"),
            ForceCrossVolumeForTest = forceCrossVolume,
            BeforePinnedHardlinkCreationForTestAsync =
                beforePinnedHardlinkCreation,
            BeforeMarkerlessRegistrationSourceDeleteForTestAsync =
                beforeRegistrationSourceDelete,
            AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync =
                afterRegistrationTargetCreatedBeforeState,
            AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync =
                afterSourceDeletedBeforeState,
            AfterMarkerlessMoveSourceDeletedStateForTestAsync =
                afterSourceDeletedState,
            BeforeMarkerlessCompletedJournalCommitForTestAsync =
                beforeCompletedJournalCommit,
            RegistrationPublicationProbeForTest = publicationProbeOutcome.HasValue
                ? _ => publicationProbeOutcome.Value
                : null
        };
    }

    private async Task<Scenario> CreateScenarioAsync(string name)
    {
        var root = FileService.GetTempDirectory(name);
        var source = Path.Join(root, "source.m4b");
        var destinationDirectory = Path.Join(root, "destination");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Join(destinationDirectory, "published.m4b");
        await File.WriteAllTextAsync(source, "audio");
        return new Scenario(root, source, destination, Guid.NewGuid());
    }

    private async Task AssertJournalStateAsync(
        Guid operationId,
        FileMutationJournalState state,
        int? audiobookId)
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(state, journal.State);
        Assert.Equal(audiobookId, journal.AudiobookId);
    }

    private static void AssertNoLibraryArtifacts(string root)
    {
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories),
            path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains(".listenarr-", StringComparison.Ordinal)
                    || name.EndsWith(".partial", StringComparison.Ordinal)
                    || name.Contains("quarantine", StringComparison.OrdinalIgnoreCase);
            });
    }

    private sealed record Scenario(
        string Root,
        string Source,
        string Destination,
        Guid OperationId);
}
