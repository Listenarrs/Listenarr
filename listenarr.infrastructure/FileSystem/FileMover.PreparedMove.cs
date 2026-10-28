using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    public async Task<bool> CompletePreparedMoveAsync(
        string source,
        string destination,
        IAudiobookFileRegistrationLease registrationLease,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(registrationLease);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (operationId == Guid.Empty)
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                FileAction.Move,
                source,
                destination,
                "A durable prepared move requires a non-empty operation ID");
            return false;
        }

        try
        {
            var markerlessResult = await TryCompletePreparedMoveMarkerlessAsync(
                source,
                destination,
                registrationLease,
                operationId);
            if (markerlessResult.HasValue)
            {
                return markerlessResult.Value;
            }

            LogMutation(
                FileMutationOutcome.Blocked,
                FileAction.Move,
                source,
                destination,
                "Durable markerless registration state is unavailable");
            return false;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Prepared move completion failed: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(source),
                LogRedaction.SanitizeFilePath(destination));
            return false;
        }
    }
}
