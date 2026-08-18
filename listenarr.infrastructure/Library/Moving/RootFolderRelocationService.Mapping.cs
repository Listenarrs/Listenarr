namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static RootFolderPathChangeResult Map(
        RootFolderRelocation relocation,
        string currentPath,
        bool canAbandon = false) => new(
        relocation.Id,
        relocation.RootFolderId,
        currentPath,
        relocation.TargetPath,
        relocation.Status,
        relocation.TotalJobs,
        relocation.CompletedJobs,
        relocation.Error,
        relocation.TargetIdentityEnrollmentState,
        relocation.SkippedItems
            .Select(item => item.AudiobookId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray(),
        relocation.Mode,
        relocation.SkippedItems
            .OrderBy(item => item.AudiobookId)
            .Select(item => new RootFolderRelocationSkippedItemResult(
                item.AudiobookId,
                ClassifyMetadataSkipReason(item.Reason)))
            .ToArray(),
        canAbandon);

    private async Task BroadcastAsync(
        RootFolderPathChangeResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await hubBroadcaster.BroadcastAsync(
                "RootFolderRelocationUpdate",
                RootFolderRelocationPublicProjection.Sanitize(result),
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            // The relocation state is already committed. Request or transport
            // cancellation may suppress this best-effort publication, but it must
            // not make the durable operation appear to have failed.
            System.Diagnostics.Trace.TraceWarning(
                "Canceled broadcasting root relocation {0}: {1}",
                result.RelocationId,
                exception.Message);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            System.Diagnostics.Trace.TraceWarning(
                "Failed to broadcast root relocation {0}: {1}",
                result.RelocationId,
                exception.Message);
        }
    }
}
