using System.Buffers;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task HandleExistingMarkerlessTargetAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry,
        long completedUnitsBeforeFile,
        long totalUnits,
        CancellationToken cancellationToken)
    {
        _ = sourceEntry;
        _ = completedUnitsBeforeFile;
        _ = totalUnits;

        ValidateMarkerlessTargetEntry(entry, targetEntry);
        if (entry.CopyState is not (
            MoveJobEntryCopyState.Pending
                or MoveJobEntryCopyState.Staged
                or MoveJobEntryCopyState.Published
                or MoveJobEntryCopyState.Verified))
        {
            throw new MoveNeedsAttentionException(
                $"The persisted markerless target-file state is inconsistent: {entry.RelativePath}");
        }

        // Reaching this method means the final-name target already existed before
        // this publication attempt. Persisted state can identify recovery work,
        // but cannot authorize mutating that pathname after a process boundary.
        // Recovery may only adopt bytes that match the durable content proof.
        if (!await PinnedFileMatchesManifestAsync(
                targetEntry,
                entry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"The existing target does not match the move's verified content proof and was preserved: {entry.RelativePath}");
        }

        if (entry.CopyState != MoveJobEntryCopyState.Verified)
        {
            var currentIdentity = PinnedDirectoryCreation.CaptureDiagnosticIdentity(targetEntry.GetObjectIdentity);
            await UpdateTargetEntryStateAsync(
                request.JobId,
                request.LeaseToken,
                entry.RelativePath,
                MoveJobEntryCopyState.Verified,
                currentIdentity,
                cancellationToken);
            entry.CopyState = MoveJobEntryCopyState.Verified;
            entry.TargetPhysicalObjectIdentity = currentIdentity;
        }
    }

    private async Task WriteMarkerlessTargetAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry,
        long completedWorkUnitsBeforeFile,
        long totalWorkUnits,
        CancellationToken cancellationToken)
    {
        ValidateMarkerlessSourceEntry(request, entry, sourceEntry);
        ValidateMarkerlessTargetEntry(entry, targetEntry);
        await using (var sourceStream = sourceEntry.OpenReadStream(
            bufferSize: 1024 * 1024,
            asynchronous: false))
        await using (var targetStream = targetEntry.OpenWriteStream(
            bufferSize: 1024 * 1024,
            asynchronous: false))
        {
            targetStream.SetLength(0);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                long copied = 0;
                long lastReported = 0;
                var reportInterval = Math.Max(
                    16L * 1024 * 1024,
                    Math.Max(totalWorkUnits / 100, 1));
                while (true)
                {
                    var read = await sourceStream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await targetStream.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                    copied += read;
                    if (copied - lastReported >= reportInterval)
                    {
                        lastReported = copied;
                        await ReportProgressAsync(
                            request,
                            CalculateWeightedProgress(
                                5,
                                65,
                                completedWorkUnitsBeforeFile + copied,
                                totalWorkUnits),
                            "Copying",
                            cancellationToken);
                    }
                }

                if (copied != entry.Length)
                {
                    throw new IOException(
                        $"Markerless source length changed while copying: {entry.RelativePath}");
                }

                await targetStream.FlushAsync(cancellationToken);
                targetStream.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        // The independently opened write stream is identity-verified against the
        // pinned entry and Flush(true) is the durability barrier. The observation
        // handle may be read-only during recovery and must not be flushed again.
        faultInjector?.OnCopyMutation(
            request.JobId,
            CopyMutationFaultPoint
                .AfterMarkerlessFileWriteBeforePublishedState);
        try
        {
            faultInjector?.OnCopyMutation(
                request.JobId,
                CopyMutationFaultPoint.BeforeMarkerlessMetadataPreservation);
            sourceEntry.PreserveMarkerlessMetadataTo(targetEntry);
        }
        catch (Exception exception) when (
            WorkerExceptionClassifier.IsNonFatal(exception))
        {
            // Metadata preservation is best-effort, but only while the pinned file
            // still owns the visible destination pathname. A replacement race must
            // remain a hard failure instead of being treated as a metadata warning.
            ValidateMarkerlessTargetEntry(entry, targetEntry);
            logger.LogDebug(
                exception,
                "Non-fatal: failed to preserve markerless file metadata for {File}",
                LogRedaction.SanitizeFilePath(targetEntry.FullPath));
        }
        await UpdateTargetEntryStateAsync(
            request.JobId,
            request.LeaseToken,
            entry.RelativePath,
            MoveJobEntryCopyState.Published,
            entry.TargetPhysicalObjectIdentity,
            cancellationToken);
        entry.CopyState = MoveJobEntryCopyState.Published;

        ValidateMarkerlessTargetEntry(entry, targetEntry);
        if (!await PinnedFileMatchesManifestAsync(
                targetEntry,
                entry,
                cancellationToken))
        {
            throw new IOException(
                $"Markerless target verification failed: {entry.RelativePath}");
        }

        await UpdateTargetEntryStateAsync(
            request.JobId,
            request.LeaseToken,
            entry.RelativePath,
            MoveJobEntryCopyState.Verified,
            entry.TargetPhysicalObjectIdentity,
            cancellationToken);
        entry.CopyState = MoveJobEntryCopyState.Verified;
    }
}
