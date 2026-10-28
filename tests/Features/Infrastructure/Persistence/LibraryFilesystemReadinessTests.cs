using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "LibraryFilesystemReadinessTests")]
[Trait("Category", "Infrastructure")]
public sealed class LibraryFilesystemReadinessTests : BaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureReady_PendingOrRunning_FailsClosed(bool running)
    {
        var readiness = new LibraryFilesystemReadiness();
        if (running)
        {
            readiness.MarkRunning("TestPhase");
        }

        var exception = Assert.Throws<ApplicationUnavailableException>(readiness.EnsureReady);

        Assert.Equal("filesystem_initializing", exception.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureMetadataRepairReady_PendingOrRunning_FailsClosed(bool running)
    {
        var readiness = new LibraryFilesystemReadiness();
        if (running)
        {
            readiness.MarkRunning("AudiobookFileIdentities");
        }

        var exception = Assert.Throws<ApplicationUnavailableException>(
            readiness.EnsureMetadataRepairReady);

        Assert.Equal("metadata_repair_initializing", exception.Code);
    }

    [Fact]
    public void EnsureMetadataRepairReady_Failed_FailsClosed()
    {
        var readiness = new LibraryFilesystemReadiness();
        readiness.MarkRunning("FileRenameRecovery");
        readiness.MarkFailed(
            "filesystem_initialization_failed",
            "Injected startup recovery failure.",
            "FileRenameRecovery");

        var exception = Assert.Throws<ApplicationUnavailableException>(
            readiness.EnsureMetadataRepairReady);

        Assert.Equal("metadata_repair_initialization_failed", exception.Code);
    }

    [Fact]
    public async Task MarkReady_ReleasesWaitersAndAllowsMutation()
    {
        var readiness = new LibraryFilesystemReadiness();
        readiness.MarkRunning("TestPhase");
        var waiter = readiness.WaitUntilReadyAsync();

        Assert.False(waiter.IsCompleted);

        readiness.MarkReady();

        await waiter;
        readiness.EnsureReady();
        readiness.EnsureMetadataRepairReady();
        Assert.True(readiness.Current.IsReady);
        Assert.Equal(LibraryFilesystemInitializationStatus.Ready, readiness.Current.Status);
    }

    [Fact]
    public async Task MarkFailed_KeepsMutationBlockedAndWaitersBlockedUntilShutdown()
    {
        var readiness = new LibraryFilesystemReadiness();
        readiness.MarkRunning("LibraryDirectoryOwnership");
        var settled = readiness.WaitUntilSettledAsync();
        readiness.MarkFailed(
            "filesystem_initialization_failed",
            "Injected startup reconciliation failure.",
            "LibraryDirectoryOwnership");

        var exception = Assert.Throws<ApplicationUnavailableException>(readiness.EnsureReady);
        Assert.Equal("filesystem_initialization_failed", exception.Code);
        Assert.Equal(
            "Injected startup reconciliation failure.",
            readiness.Current.ErrorMessage);
        await settled;

        using var cancellation = new CancellationTokenSource();
        var waiter = readiness.WaitUntilReadyAsync(cancellation.Token);
        Assert.False(waiter.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
    }

    [Fact]
    public void TerminalReadyState_CannotBeDowngradedByLateFailure()
    {
        var readiness = new LibraryFilesystemReadiness();
        readiness.MarkReady();

        readiness.MarkFailed(
            "filesystem_initialization_failed",
            "late failure",
            "LatePhase");

        Assert.True(readiness.Current.IsReady);
        Assert.Equal(LibraryFilesystemInitializationStatus.Ready, readiness.Current.Status);
    }
}
