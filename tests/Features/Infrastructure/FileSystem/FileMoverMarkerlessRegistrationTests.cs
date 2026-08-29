using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

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
    public async Task CheckPublicationSource_IdentityUnsupported_ReturnsContentOnlyProof()
    {
        var scenario = await CreateScenarioAsync(
            "registration-source-content-only-capability");
        var capability = Assert.IsAssignableFrom<IFilePublicationSourceCapability>(
            CreateMover(forceContentOnlySourceProof: true));

        var result = await capability.CheckAsync(scenario.Source);

        Assert.True(result.IsSupported, result.Reason);
        Assert.True(result.SourceProof.HasValue);
        var proof = result.SourceProof.Value;
        Assert.False(proof.HasDurablePhysicalObjectIdentity);
        Assert.Equal(FilePublicationSourceAuthority.ContentOnly, proof.Authority);
        Assert.Equal(5, proof.Length);
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
    public async Task PrepareMove_ForcedCrossVolumeCopiesRegistersThenRetiresExactSource()
    {
        var scenario = await CreateScenarioAsync("registration-cross-volume-blocked");
        var mover = CreateMover(forceCrossVolume: true);

        using var lease = await mover.PrepareActionForRegistrationAsync(
            FileAction.Move,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId);

        Assert.NotNull(lease);
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        Assert.True(lease.PrepareCleanupRecovery(73));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());
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
            audiobookId: 73);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [LinuxFact]
    public async Task PrepareCompanionMove_UsesCompanionRecoveryOwnerAcrossVolumes()
    {
        var scenario = await CreateScenarioAsync("registration-companion-cross-volume");
        var mover = CreateMover(forceCrossVolume: true);
        var capability = Assert.IsAssignableFrom<
            IFilePublicationSourceCapability>(mover);
        var sourceCapability = await capability.CheckAsync(scenario.Source);
        Assert.True(sourceCapability.IsSupported, sourceCapability.Reason);

        var preparation = await mover.PrepareActionForRegistrationDetailedAsync(
            FilePublicationPlan.Durable(FileAction.Move),
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            sourceCapability.SourceProof!.Value,
            isCompanionFile: true,
            companionAudiobookId: 73);

        using var lease = Assert.IsAssignableFrom<
            IAudiobookFileRegistrationLease>(preparation.RegistrationLease);
        Assert.True(lease.PrepareCleanupRecovery(73));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var committedDb = await factory.CreateDbContextAsync())
        {
            var committed = await committedDb.FileMutationJournals
                .SingleAsync(candidate =>
                    candidate.OperationId == scenario.OperationId);
            Assert.Equal(73, committed.AudiobookId);
            Assert.Equal(
                FileMutationOwner.RegistrationCompanionFile,
                committed.AudiobookFileId);
            Assert.Equal(
                FileMutationJournalState.RegistrationCommitted,
                committed.State);
        }

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
            audiobookId: 73);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [CrossVolumeFact]
    public async Task PrepareCompanionMove_RealCrossVolumeCopiesThenRetiresSource()
    {
        var sourceRoot = FileService.GetTempDirectory(
            "registration-real-cross-volume-source");
        var source = Path.Join(sourceRoot, "cover.jpg");
        await File.WriteAllTextAsync(source, "cover");
        var providedDestinationRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                CrossVolumeFactAttribute.DestinationPathEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "A real cross-volume destination was not provided."));
        var destinationRoot = Path.Join(
            providedDestinationRoot,
            $"listenarr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationRoot);
        var destination = Path.Join(destinationRoot, "cover.jpg");
        var operationId = Guid.NewGuid();

        try
        {
            var mover = CreateMover();
            var capability = Assert.IsAssignableFrom<
                IFilePublicationSourceCapability>(mover);
            var sourceCapability = await capability.CheckAsync(source);
            Assert.True(sourceCapability.IsSupported, sourceCapability.Reason);

            var preparation = await mover
                .PrepareActionForRegistrationDetailedAsync(
                    FilePublicationPlan.Durable(FileAction.Move),
                    source,
                    destination,
                    operationId,
                    expectedRegisteredPhysicalObjectIdentity: null,
                    sourceCapability.SourceProof!.Value,
                    isCompanionFile: true,
                    companionAudiobookId: 75);
            using var lease = Assert.IsAssignableFrom<
                IAudiobookFileRegistrationLease>(
                    preparation.RegistrationLease);
            Assert.NotEqual(
                lease.SourcePhysicalObjectIdentity,
                lease.PhysicalObjectIdentity);
            Assert.True(lease.PrepareCleanupRecovery(75));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                lease.CompletePublication());
            Assert.True(await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease,
                operationId));

            Assert.False(File.Exists(source));
            Assert.Equal("cover", await File.ReadAllTextAsync(destination));
            await AssertJournalStateAsync(
                operationId,
                FileMutationJournalState.Completed,
                audiobookId: 75);
        }
        finally
        {
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareCompatibilityMove_CopiesAndRetainsSourceWithoutDurableMoveJournal()
    {
        var scenario = await CreateScenarioAsync("registration-compatible-move");
        var mover = CreateMover();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("audio")));
        var proof = new FilePublicationSourceProof(
            $"content-only:{hash}",
            5,
            hash,
            FilePublicationSourceAuthority.ContentOnly);

        var preparation = await mover.PrepareActionForRegistrationDetailedAsync(
            FilePublicationPlan.Additive(FileAction.Move),
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            proof,
            isCompanionFile: true,
            companionAudiobookId: 74);

        Assert.True(preparation.IsSuccess, preparation.Message);
        Assert.Equal(FileAction.Copy, preparation.EffectiveAction);
        Assert.Equal(
            FilePublicationSourceDisposition.Retained,
            preparation.SourceDisposition);
        using var lease = Assert.IsAssignableFrom<
            IAudiobookFileRegistrationLease>(preparation.RegistrationLease);
        Assert.False(lease.HasDurablePhysicalObjectIdentity);
        Assert.True(lease.PrepareCleanupRecovery(74));
        Assert.Equal(
            RegistrationPublicationCompletion.Completed,
            lease.CompletePublication());

        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.FileMutationJournals.ToListAsync());
        var journal = await db.CompatibilityFilePublicationJournals
            .SingleAsync(candidate =>
                candidate.OperationId == scenario.OperationId);
        Assert.Equal(
            CompatibilityFilePublicationState.Completed,
            journal.State);
        Assert.Equal(FileAction.Move, journal.RequestedAction);
        Assert.Equal(FileAction.Copy, journal.EffectiveAction);
        Assert.True(journal.IsCompanionFile);
        AssertNoLibraryArtifacts(scenario.Root);
    }

    [Fact]
    public async Task PrepareCompatibilityMove_VerifiedCleanup_RemainsRegistrationCommittedUntilBatchCoordinator()
    {
        var scenario = await CreateScenarioAsync("registration-compatible-verified-cleanup");
        var mover = CreateMover();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("audio")));
        var proof = new FilePublicationSourceProof(
            $"content-only:{hash}",
            5,
            hash,
            FilePublicationSourceAuthority.ContentOnly);
        var batchId = Guid.NewGuid();
        var plan = FilePublicationPlan.VerifiedCleanup(
            batchId,
            CompatibilityCleanupOwner.Listenarr,
            sourceRootFolderId: null,
            sourcePolicyRevision: null,
            destinationRootFolderId: 87,
            destinationPolicyRevision: 4,
            sourceStorageContractRevision: null,
            destinationStorageContractRevision: 9);

        var preparation = await mover.PrepareActionForRegistrationDetailedAsync(
            plan,
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            proof);

        Assert.True(preparation.IsSuccess, preparation.Message);
        using var lease = Assert.IsAssignableFrom<IAudiobookFileRegistrationLease>(
            preparation.RegistrationLease);
        Assert.True(lease.PrepareCleanupRecovery(74));
        Assert.Equal(
            RegistrationPublicationCompletion.CommittedCleanupPending,
            lease.CompletePublication());
        Assert.Equal(
            RegistrationPublicationCompletion.CommittedCleanupPending,
            lease.CompletePublication());

        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.CompatibilityFilePublicationJournals
            .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
        Assert.Equal(batchId, journal.BatchId);
        Assert.Equal(CompatibilityCleanupOwner.Listenarr, journal.CleanupOwner);
        Assert.Equal(74, journal.AudiobookId);
        Assert.Equal(
            CompatibilityFilePublicationState.RegistrationCommitted,
            journal.State);
    }

    [Fact]
    public async Task PrepareCompatibilityMove_BlockedLogIncludesFileContext()
    {
        var scenario = await CreateScenarioAsync(
            "registration-compatible-blocked-log");
        LogLevel? capturedLevel = null;
        string? capturedMessage = null;
        var logger = new Mock<ILogger<FileMover>>();
        logger.Setup(candidate => candidate.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                capturedLevel = (LogLevel)invocation.Arguments[0];
                capturedMessage = invocation.Arguments[2]?.ToString();
            }));
        var mover = CreateMover(
            logger: logger.Object,
            options: Options.Create(new FileMoverOptions
            {
                WeakPublicationMode = WeakPublicationMode.Disabled
            }));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("audio")));
        var proof = new FilePublicationSourceProof(
            $"content-only:{hash}",
            5,
            hash,
            FilePublicationSourceAuthority.ContentOnly);

        var preparation = await mover.PrepareActionForRegistrationDetailedAsync(
            FilePublicationPlan.Additive(FileAction.Move),
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            proof);

        Assert.False(preparation.IsSuccess);
        Assert.Equal("compatibility_publication_disabled", preparation.ReasonCode);
        Assert.Equal(LogLevel.Warning, capturedLevel);
        Assert.NotNull(capturedMessage);
        Assert.Contains(Path.GetFileName(scenario.Source), capturedMessage);
        Assert.Contains(Path.GetFileName(scenario.Destination), capturedMessage);
    }

    [NetworkStorageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareCompatibilityMove_OnNetworkStorage_CopiesAndRetainsSource(
        bool isCompanionFile)
    {
        var providedRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                NetworkStorageTheoryAttribute.PathEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "A network filesystem path was not provided."));
        var scenarioRoot = Path.Join(
            providedRoot,
            $"listenarr-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Join(scenarioRoot, "source");
        var destinationDirectory = Path.Join(scenarioRoot, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "cover.jpg");
        var destination = Path.Join(destinationDirectory, "cover.jpg");
        await File.WriteAllTextAsync(source, "cover");
        var operationId = Guid.NewGuid();

        try
        {
            var nativeCapability = Assert.IsAssignableFrom<
                IFilePublicationSourceCapability>(CreateMover());
            var nativeResult = await nativeCapability.CheckAsync(source);
            Assert.True(nativeResult.IsSupported, nativeResult.Reason);

            var mover = CreateMover(forceContentOnlySourceProof: true);
            var weakCapability = Assert.IsAssignableFrom<
                IFilePublicationSourceCapability>(mover);
            var sourceCapability = await weakCapability.CheckAsync(source);
            Assert.True(sourceCapability.IsSupported, sourceCapability.Reason);
            var proof = Assert.NotNull(sourceCapability.SourceProof);
            Assert.False(proof.HasDurablePhysicalObjectIdentity);
            Assert.Equal(FilePublicationSourceAuthority.ContentOnly, proof.Authority);
            var preparation = await mover
                .PrepareActionForRegistrationDetailedAsync(
                    FilePublicationPlan.Additive(FileAction.Move),
                    source,
                    destination,
                    operationId,
                    expectedRegisteredPhysicalObjectIdentity: null,
                    proof,
                    isCompanionFile,
                    companionAudiobookId: isCompanionFile ? 76 : null);

            Assert.True(preparation.IsSuccess, preparation.Message);
            Assert.Equal(FileAction.Copy, preparation.EffectiveAction);
            Assert.Equal(
                FilePublicationSourceDisposition.Retained,
                preparation.SourceDisposition);
            using var lease = Assert.IsAssignableFrom<
                IAudiobookFileRegistrationLease>(
                    preparation.RegistrationLease);
            Assert.False(lease.HasDurablePhysicalObjectIdentity);
            Assert.True(lease.PrepareCleanupRecovery(76));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                lease.CompletePublication());

            Assert.Equal("cover", await File.ReadAllTextAsync(source));
            Assert.Equal("cover", await File.ReadAllTextAsync(destination));
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var journal = await db.CompatibilityFilePublicationJournals
                .SingleAsync(candidate =>
                    candidate.OperationId == operationId);
            Assert.Equal(
                CompatibilityFilePublicationState.Completed,
                journal.State);
            Assert.Equal(isCompanionFile, journal.IsCompanionFile);
        }
        finally
        {
            if (Directory.Exists(scenarioRoot))
            {
                Directory.Delete(scenarioRoot, recursive: true);
            }
        }
    }

    [ForeignOwnedNetworkStorageFact]
    [SupportedOSPlatform("linux")]
    public async Task PreserveMarkerlessMetadata_OnNetworkStorage_FromForeignOwnedSource_KeepsOwnerWriteMode()
    {
        var providedRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                NetworkStorageTheoryAttribute.PathEnvironmentVariable)!);
        var source = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                ForeignOwnedNetworkStorageFactAttribute.SourcePathEnvironmentVariable)!);
        var scenarioRoot = Path.Join(
            providedRoot,
            $"listenarr-foreign-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scenarioRoot);
        var destination = Path.Join(scenarioRoot, "cover.jpg");

        try
        {
            var sourceMode = File.GetUnixFileMode(source);
            Assert.True(sourceMode.HasFlag(UnixFileMode.UserWrite));
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using var _ = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            });

            using var sourceParent = PinnedDirectoryCreation
                .OpenPinnedHierarchyNoFollow(
                    Path.GetDirectoryName(source)!,
                    createMissing: false);
            using var destinationParent = PinnedDirectoryCreation
                .OpenPinnedHierarchyNoFollow(
                    scenarioRoot,
                    createMissing: false);
            using var sourceEntry = sourceParent
                .OpenExistingFileForStableRead(Path.GetFileName(source));
            using var destinationEntry = destinationParent
                .CreateNewFile(Path.GetFileName(destination));
            await using (var sourceStream = sourceEntry.OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false))
            await using (var destinationStream = destinationEntry.OpenWriteStream(
                bufferSize: 128 * 1024,
                asynchronous: false))
            {
                await sourceStream.CopyToAsync(destinationStream);
            }

            sourceEntry.PreserveMarkerlessMetadataTo(destinationEntry);

            var destinationMode = File.GetUnixFileMode(destination);
            Assert.Equal(sourceMode, destinationMode);
            Assert.True(destinationMode.HasFlag(UnixFileMode.UserWrite));
            using var metadataStream = destinationEntry.OpenWriteStream(
                bufferSize: 4096,
                asynchronous: false);
            Assert.True(metadataStream.CanWrite);
        }
        finally
        {
            if (Directory.Exists(scenarioRoot))
            {
                Directory.Delete(scenarioRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareCompatibilityCopy_PreexistingTargetIsPreservedAndNeedsAttention()
    {
        var scenario = await CreateScenarioAsync("registration-compatible-existing");
        await File.WriteAllTextAsync(scenario.Destination, "foreign");
        var mover = CreateMover();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("audio")));
        var proof = new FilePublicationSourceProof(
            $"content-only:{hash}",
            5,
            hash,
            FilePublicationSourceAuthority.ContentOnly);

        var preparation = await mover.PrepareActionForRegistrationDetailedAsync(
            FilePublicationPlan.Additive(FileAction.Copy),
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            proof);

        Assert.False(preparation.IsSuccess);
        Assert.Null(preparation.RegistrationLease);
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("foreign", await File.ReadAllTextAsync(scenario.Destination));
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.CompatibilityFilePublicationJournals
            .SingleAsync(candidate =>
                candidate.OperationId == scenario.OperationId);
        Assert.Equal(
            CompatibilityFilePublicationState.NeedsAttention,
            journal.State);
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
        IRootFolderStorageHealthResolver? rootFolderStorageHealthResolver = null,
        bool forceContentOnlySourceProof = false,
        ILogger<FileMover>? logger = null,
        IOptions<FileMoverOptions>? options = null)
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            logger ?? new NullLogger<FileMover>(),
            options: options,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System,
            readOnlyFileSystemProbe: readOnlyFileSystemProbe,
            rootFolderRepository: rootFolderRepository,
            rootFolderStorageHealthResolver: rootFolderStorageHealthResolver)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "file-mover-markerless-registration-locks"),
            ForceCrossVolumeForTest = forceCrossVolume,
            ForceContentOnlySourceProofForTest = forceContentOnlySourceProof,
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
