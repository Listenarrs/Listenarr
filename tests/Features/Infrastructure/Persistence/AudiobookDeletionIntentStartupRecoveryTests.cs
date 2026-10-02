using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

/// <summary>
/// Startup-level evidence for the deletion-intent phase of library filesystem
/// reconciliation. A deletion intent that can never be reconciled must not take
/// the filesystem readiness gate down with it, while a filesystem that is
/// genuinely failing still must.
/// </summary>
[Trait("Name", "AudiobookDeletionIntentStartupRecoveryTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookDeletionIntentStartupRecoveryTests : BaseTests
{
    [Fact]
    public async Task OrphanedDeletionIntent_LeavesTheFilesystemGateOpen()
    {
        var intentId = await CreateOrphanedPlannedIntentAsync("startup-orphan-first");
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        var readiness = new LibraryFilesystemReadiness();

        await RunStartupReconciliationAsync(filesystem.Object, readiness);

        Assert.Equal(
            LibraryFilesystemInitializationStatus.Ready,
            readiness.Current.Status);
        Assert.Null(readiness.Current.ErrorCode);
        readiness.EnsureReady();
        filesystem.VerifyNoOtherCalls();
        Assert.Equal(
            AudiobookDeletionIntentState.NeedsAttention,
            await GetIntentStateAsync(intentId));
    }

    [Fact]
    public async Task OrphanedDeletionIntent_SurvivesASecondStartupWithTheGateStillOpen()
    {
        var intentId = await CreateOrphanedPlannedIntentAsync("startup-orphan-restart");
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        var firstReadiness = new LibraryFilesystemReadiness();
        await RunStartupReconciliationAsync(filesystem.Object, firstReadiness);
        Assert.Equal(
            LibraryFilesystemInitializationStatus.Ready,
            firstReadiness.Current.Status);
        Assert.Equal(
            AudiobookDeletionIntentState.NeedsAttention,
            await GetIntentStateAsync(intentId));

        var secondReadiness = new LibraryFilesystemReadiness();
        await RunStartupReconciliationAsync(filesystem.Object, secondReadiness);

        Assert.Equal(
            LibraryFilesystemInitializationStatus.Ready,
            secondReadiness.Current.Status);
        secondReadiness.EnsureReady();
        filesystem.VerifyNoOtherCalls();
        Assert.Equal(
            AudiobookDeletionIntentState.NeedsAttention,
            await GetIntentStateAsync(intentId));
    }

    /// <summary>
    /// Apparatus control. A ready gate would look the same if the deletion phase
    /// never ran at all, so the same harness must still be able to close it.
    /// </summary>
    [Fact]
    public async Task NonTransientCleanupFailure_StillClosesTheFilesystemGate()
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Startup Non Transient Cleanup")
            .WithBasePath(FileService.GetTempDirectory("startup-non-transient"))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        var filesystem = new Mock<IAudiobookFilesystemDeleteService>(MockBehavior.Strict);
        filesystem.Setup(service => service.DeleteAsync(
                It.IsAny<Audiobook>(),
                true,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Injected non-transient cleanup failure."));
        var readiness = new LibraryFilesystemReadiness();

        await RunStartupReconciliationAsync(filesystem.Object, readiness);

        Assert.Equal(
            LibraryFilesystemInitializationStatus.Failed,
            readiness.Current.Status);
        Assert.Equal("filesystem_initialization_failed", readiness.Current.ErrorCode);
        Assert.Equal("AudiobookDeletionRecovery", readiness.Current.Phase);
        var blocked = Assert.Throws<ApplicationUnavailableException>(readiness.EnsureReady);
        Assert.Equal("filesystem_initialization_failed", blocked.Code);
        Assert.Equal(
            AudiobookDeletionIntentState.Planned,
            await GetIntentStateAsync(intent.Id));
    }

    private async Task RunStartupReconciliationAsync(
        IAudiobookFilesystemDeleteService filesystemDeleteService,
        LibraryFilesystemReadiness readiness)
    {
        var reconciler = new AudiobookDeletionIntentReconciler(
            _provider.GetRequiredService<IAudiobookDeletionIntentStore>(),
            _audiobookRepository,
            _provider.GetRequiredService<IAudiobookDeletionCommitService>(),
            filesystemDeleteService,
            NullLogger<AudiobookDeletionIntentReconciler>.Instance);
        using var provider = BuildStartupProvider(reconciler);
        using var service = new LibraryFilesystemStartupReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            readiness,
            NullLogger<LibraryFilesystemStartupReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);
    }

    private async Task<Guid> CreateOrphanedPlannedIntentAsync(string tempDirectoryName)
    {
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Startup Delete Recovery Orphan")
            .WithBasePath(FileService.GetTempDirectory(tempDirectoryName))
            .Build());
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intent = await store.GetOrCreateAsync(audiobook.Id, deleteFolder: true);
        Assert.True(await _audiobookRepository.DeleteByIdAsync(audiobook.Id));
        Assert.Null(await _audiobookRepository.GetByIdAsync(audiobook.Id));
        return intent.Id;
    }

    private async Task<AudiobookDeletionIntentState> GetIntentStateAsync(Guid intentId)
    {
        var store = _provider.GetRequiredService<IAudiobookDeletionIntentStore>();
        var intents = await store.GetActiveAsync();
        var intent = Assert.Single(intents, candidate => candidate.Id == intentId);
        return intent.State;
    }

    private static ServiceProvider BuildStartupProvider(
        IAudiobookDeletionIntentReconciler deletion) =>
        new ServiceCollection()
            .AddScoped(_ => Mock.Of<IRootFolderObjectIdentityReconciler>(service =>
                service.ReconcileAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask))
            .AddScoped(_ => Mock.Of<IRootFolderRelocationService>(service =>
                service.ReconcileActiveAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask))
            .AddScoped(_ => Mock.Of<ILibraryDirectoryOwnershipReconciler>(service =>
                service.ReconcileAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask))
            .AddScoped(_ => deletion)
            .AddScoped(_ => Mock.Of<IFileRegistrationRecoveryService>(service =>
                service.AdoptCommittedAnonymousAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask
                && service.ReconcileAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask))
            .AddScoped<ICompatibilityFilePublicationRecoveryService>(
                _ => new NoOpCompatibilityRecoveryService())
            .AddScoped(_ => Mock.Of<IFileRenameRecoveryReconciler>(service =>
                service.ReconcileAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask))
            .AddScoped(_ => Mock.Of<IAudiobookFileIdentityReconciler>(service =>
                service.ReconcileAsync(It.IsAny<CancellationToken>())
                    == Task.FromResult(new AudiobookFileIdentityReconciliationResult(0, 0, 0, 0))))
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

    private sealed class NoOpCompatibilityRecoveryService
        : ICompatibilityFilePublicationRecoveryService
    {
        public Task ReconcileAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
