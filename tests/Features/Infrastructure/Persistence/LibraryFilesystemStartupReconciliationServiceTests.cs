using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "LibraryFilesystemStartupReconciliationServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class LibraryFilesystemStartupReconciliationServiceTests : BaseTests
{
    [Fact]
    public async Task StartAsync_ReturnsWhileReconciliationIsBlocked_ThenCompletesInRequiredOrder()
    {
        var rootEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRoot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var root = new Mock<IRootFolderObjectIdentityReconciler>(MockBehavior.Strict);
        root.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken cancellationToken) =>
            {
                order.Add("root");
                rootEntered.TrySetResult();
                await releaseRoot.Task.WaitAsync(cancellationToken);
            });
        var relocation = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocation.Setup(service => service.ReconcileActiveAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                order.Add("relocation");
                return Task.CompletedTask;
            });
        var ownership = new Mock<ILibraryDirectoryOwnershipReconciler>(MockBehavior.Strict);
        ownership.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                order.Add("ownership");
                return Task.CompletedTask;
            });
        var deletion = new Mock<IAudiobookDeletionIntentReconciler>(MockBehavior.Strict);
        deletion.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                order.Add("deletion");
                return Task.CompletedTask;
            });
        var rename = new Mock<IFileRenameRecoveryReconciler>(MockBehavior.Strict);
        rename.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                order.Add("rename");
                return Task.CompletedTask;
            });
        var files = new Mock<IAudiobookFileIdentityReconciler>(MockBehavior.Strict);
        files.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                order.Add("files");
                return Task.FromResult(new AudiobookFileIdentityReconciliationResult(0, 0, 0, 0));
            });

        using var provider = BuildProvider(
            root.Object,
            relocation.Object,
            ownership.Object,
            files.Object,
            deletion.Object,
            rename.Object);
        var readiness = new LibraryFilesystemReadiness();
        var service = new LibraryFilesystemStartupReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            readiness,
            NullLogger<LibraryFilesystemStartupReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await rootEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LibraryFilesystemInitializationStatus.Running, readiness.Current.Status);
        Assert.Equal("RootFolderObjectIdentities", readiness.Current.Phase);
        Assert.False(readiness.Current.IsReady);
        Assert.Equal(["root"], order);

        releaseRoot.TrySetResult();
        await readiness.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(
            ["root", "relocation", "ownership", "deletion", "rename", "files"],
            order);
        Assert.True(readiness.Current.IsReady);
    }

    [Fact]
    public async Task ReconciliationFailure_MarksFilesystemFailedWithoutEscapingHostedService()
    {
        var root = new Mock<IRootFolderObjectIdentityReconciler>(MockBehavior.Strict);
        root.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var relocation = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocation.Setup(service => service.ReconcileActiveAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ownership = new Mock<ILibraryDirectoryOwnershipReconciler>(MockBehavior.Strict);
        ownership.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("injected ownership failure"));
        var files = new Mock<IAudiobookFileIdentityReconciler>(MockBehavior.Strict);

        using var provider = BuildProvider(root.Object, relocation.Object, ownership.Object, files.Object);
        var readiness = new LibraryFilesystemReadiness();
        var service = new LibraryFilesystemStartupReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            readiness,
            NullLogger<LibraryFilesystemStartupReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(LibraryFilesystemInitializationStatus.Failed, readiness.Current.Status);
        Assert.Equal("LibraryDirectoryOwnership", readiness.Current.Phase);
        Assert.Equal("filesystem_initialization_failed", readiness.Current.ErrorCode);
        files.Verify(service => service.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcilerLocalCancellation_MarksFilesystemFailedWithoutStoppingHost()
    {
        var root = new Mock<IRootFolderObjectIdentityReconciler>(MockBehavior.Strict);
        root.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var relocation = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocation.Setup(service => service.ReconcileActiveAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("injected reconciliation timeout"));
        var ownership = new Mock<ILibraryDirectoryOwnershipReconciler>(MockBehavior.Strict);
        var files = new Mock<IAudiobookFileIdentityReconciler>(MockBehavior.Strict);

        using var provider = BuildProvider(root.Object, relocation.Object, ownership.Object, files.Object);
        var readiness = new LibraryFilesystemReadiness();
        var service = new LibraryFilesystemStartupReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            readiness,
            NullLogger<LibraryFilesystemStartupReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(LibraryFilesystemInitializationStatus.Failed, readiness.Current.Status);
        Assert.Equal("RootFolderRelocations", readiness.Current.Phase);
        Assert.Equal("filesystem_initialization_failed", readiness.Current.ErrorCode);
        ownership.Verify(service => service.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
        files.Verify(service => service.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HostShutdownDuringReconciliation_DoesNotReportInitializationFailure()
    {
        var rootEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var root = new Mock<IRootFolderObjectIdentityReconciler>(MockBehavior.Strict);
        root.Setup(service => service.ReconcileAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken cancellationToken) =>
            {
                rootEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        var relocation = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var ownership = new Mock<ILibraryDirectoryOwnershipReconciler>(MockBehavior.Strict);
        var files = new Mock<IAudiobookFileIdentityReconciler>(MockBehavior.Strict);

        using var provider = BuildProvider(root.Object, relocation.Object, ownership.Object, files.Object);
        var readiness = new LibraryFilesystemReadiness();
        var service = new LibraryFilesystemStartupReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            readiness,
            NullLogger<LibraryFilesystemStartupReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await rootEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(LibraryFilesystemInitializationStatus.Running, readiness.Current.Status);
        Assert.Null(readiness.Current.ErrorCode);
        relocation.Verify(service => service.ReconcileActiveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ServiceProvider BuildProvider(
        IRootFolderObjectIdentityReconciler root,
        IRootFolderRelocationService relocation,
        ILibraryDirectoryOwnershipReconciler ownership,
        IAudiobookFileIdentityReconciler files,
        IAudiobookDeletionIntentReconciler? deletion = null,
        IFileRenameRecoveryReconciler? rename = null) =>
        new ServiceCollection()
            .AddScoped(_ => root)
            .AddScoped(_ => relocation)
            .AddScoped(_ => ownership)
            .AddScoped(_ => deletion ?? Mock.Of<IAudiobookDeletionIntentReconciler>(service =>
                service.ReconcileAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask))
            .AddScoped(_ => rename ?? Mock.Of<IFileRenameRecoveryReconciler>(service =>
                service.ReconcileAsync(It.IsAny<CancellationToken>()) == Task.CompletedTask))
            .AddScoped(_ => files)
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

}
