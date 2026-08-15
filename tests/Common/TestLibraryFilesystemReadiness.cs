using Listenarr.Application.Common.Exceptions;

namespace Listenarr.Tests.Common;

internal sealed class TestLibraryFilesystemReadiness :
    ILibraryFilesystemReadiness,
    ILibraryFilesystemMutationGate
{
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public LibraryFilesystemReadinessSnapshot Current { get; private set; } = new(
        LibraryFilesystemInitializationStatus.Pending);

    public static TestLibraryFilesystemReadiness Ready()
    {
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetReady();
        return readiness;
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        _ready.Task.WaitAsync(cancellationToken);

    public void EnsureReady()
    {
        if (Current.IsReady)
        {
            return;
        }

        throw new ApplicationUnavailableException(
            Current.Status == LibraryFilesystemInitializationStatus.Failed
                ? "filesystem_initialization_failed"
                : "filesystem_initializing",
            Current.ErrorMessage ?? "Filesystem initialization is not ready.");
    }

    public void EnsureMetadataRepairReady()
    {
        if (Current.IsReady)
        {
            return;
        }

        throw new ApplicationUnavailableException(
            Current.Status == LibraryFilesystemInitializationStatus.Failed
                ? "metadata_repair_initialization_failed"
                : "metadata_repair_initializing",
            Current.ErrorMessage ?? "Filesystem initialization is not ready for metadata repair.");
    }

    public void SetRunning(string phase = "Test") =>
        Current = new LibraryFilesystemReadinessSnapshot(
            LibraryFilesystemInitializationStatus.Running,
            phase);

    public void SetReady()
    {
        Current = new LibraryFilesystemReadinessSnapshot(
            LibraryFilesystemInitializationStatus.Ready);
        _ready.TrySetResult();
    }

    public void SetFailed(string message = "Filesystem initialization failed.") =>
        Current = new LibraryFilesystemReadinessSnapshot(
            LibraryFilesystemInitializationStatus.Failed,
            "Test",
            "filesystem_initialization_failed",
            message);
}
