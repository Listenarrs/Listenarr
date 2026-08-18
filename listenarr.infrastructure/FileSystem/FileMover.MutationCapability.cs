using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> IsNewMutationBlockedByReadOnlyAsync(
        FileAction action,
        string source,
        string destination,
        Guid operationId)
    {
        if (_fileMutationJournalStore != null
            && await _fileMutationJournalStore.GetAsync(
                operationId,
                CancellationToken.None) != null)
        {
            return false;
        }

        return IsKnownReadOnlyMutationEndpoint(action, source, destination);
    }

    private bool IsKnownReadOnlyMutationEndpoint(
        FileAction action,
        string source,
        string destination)
    {
        if (IsKnownReadOnlyParent(destination, "destination"))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "The destination filesystem is mounted read-only");
            return true;
        }

        if (action == FileAction.Move
            && IsKnownReadOnlyParent(source, "source"))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "The source filesystem is mounted read-only");
            return true;
        }

        return false;
    }

    private bool IsKnownReadOnlyParent(string path, string endpoint)
    {
        try
        {
            var current = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                {
                    return _readOnlyFileSystemProbe(current) == true;
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException
                or StackOverflowException))
        {
            _logger.LogDebug(
                exception,
                "Could not determine whether the {Endpoint} filesystem is read-only before file mutation",
                endpoint);
        }

        // This probe can deny mutation when read-only status is proven. Failure to
        // prove read-only does not grant authority; the existing generation-bound
        // mutation/recovery checks remain authoritative and fail closed independently.
        return false;
    }

}
