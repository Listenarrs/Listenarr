namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedFileEntry
    {
        internal async Task<bool> TryRestoreUnlinkedCopyToAsync(
            PinnedDirectoryAnchor destinationParent,
            string destinationName)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destinationParent);
            ValidateLeafName(destinationName);
            if (!destinationParent.VisiblePathMatches())
            {
                return false;
            }

            var temporaryName =
                $".listenarr-registration-restore-{Guid.NewGuid():N}.tmp";
            PinnedFileEntry? temporary = null;
            var published = false;
            try
            {
                temporary = destinationParent.CreateNewFile(temporaryName);
                await using (var sourceStream = new FileStream(
                    DuplicateSafeHandle(_fileHandle),
                    FileAccess.Read,
                    bufferSize: 128 * 1024,
                    isAsync: false))
                await using (var destinationStream = temporary.OpenWriteStream(
                    bufferSize: 128 * 1024,
                    asynchronous: false))
                {
                    sourceStream.Position = 0;
                    await sourceStream.CopyToAsync(destinationStream);
                    await destinationStream.FlushAsync();
                    destinationStream.Flush(flushToDisk: true);
                }

                PreserveMetadataTo(temporary);
                temporary.FlushToDisk();
                temporary.MoveWithinParent(destinationName);
                published = true;
                using var destinationParentHandle =
                    destinationParent.DuplicateHandleForOperation();
                FlushDirectoryPathToDisk(
                    destinationParentHandle,
                    destinationParent.FullPath,
                    destinationParent.FollowsVisibleFinalLink);
                return temporary.VisiblePathMatches();
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                if (published)
                {
                    try
                    {
                        return temporary?.VisiblePathMatches() == true;
                    }
                    catch (Exception verificationException) when (
                        verificationException is not (
                            OperationCanceledException or OutOfMemoryException
                                or StackOverflowException))
                    {
                        return false;
                    }
                }

                if (temporary != null)
                {
                    try
                    {
                        if (temporary.VisiblePathMatches())
                        {
                            temporary.Delete(immediateWindows: true);
                        }
                    }
                    catch (Exception cleanupException) when (cleanupException is not (
                        OperationCanceledException or OutOfMemoryException
                            or StackOverflowException))
                    {
                        // Leave the private temporary recovery file in place rather
                        // than risk deleting an unrelated replacement generation.
                    }
                }

                return false;
            }
            finally
            {
                temporary?.Dispose();
            }
        }
    }
}
