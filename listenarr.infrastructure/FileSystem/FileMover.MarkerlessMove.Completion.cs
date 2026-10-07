using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static async Task<RegistrationPublicationMatchOutcome>
        ProbeMarkerlessMoveCompletionAsync(
            FileMoveGateLease pathLock,
            FileMutationJournal journal,
            CancellationToken cancellationToken,
            bool requirePersistedParentIdentity = true,
            bool requireTargetPhysicalIdentity = true)
    {
        try
        {
            var sourceParentVisibility = pathLock.SourceParent.ProbeVisiblePathMatch();
            var destinationParentVisibility =
                pathLock.DestinationParent.ProbeVisiblePathMatch();
            if (sourceParentVisibility == RegistrationPublicationMatchOutcome.Unavailable
                || destinationParentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                return RegistrationPublicationMatchOutcome.Unavailable;
            }
            if (sourceParentVisibility != RegistrationPublicationMatchOutcome.Match
                || destinationParentVisibility != RegistrationPublicationMatchOutcome.Match
                || (requirePersistedParentIdentity
                    && (string.IsNullOrWhiteSpace(
                            journal.SourceParentDirectoryObjectIdentity)
                        || string.IsNullOrWhiteSpace(
                            journal.DestinationParentDirectoryObjectIdentity)
                        || !pathLock.SourceParent.MatchesDirectoryObjectIdentity(
                            journal.SourceParentDirectoryObjectIdentity)
                        || !pathLock.DestinationParent.MatchesDirectoryObjectIdentity(
                            journal.DestinationParentDirectoryObjectIdentity))))
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }

            var sourceOpenOutcome = pathLock.SourceParent.TryOpenExistingFileWithOutcome(
                pathLock.SourceName,
                requireDeleteAccess: false,
                out var sourceEntry);
            using (sourceEntry)
            {
                if (sourceOpenOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return RegistrationPublicationMatchOutcome.Unavailable;
                }
                if (sourceOpenOutcome == PinnedFileOpenOutcome.Opened)
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
            }

            var targetOpenOutcome =
                pathLock.DestinationParent.TryOpenExistingFileWithOutcome(
                    pathLock.DestinationName,
                    requireDeleteAccess: false,
                    out var targetEntry);
            using (targetEntry)
            {
                if (targetOpenOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return RegistrationPublicationMatchOutcome.Unavailable;
                }
                if (targetOpenOutcome != PinnedFileOpenOutcome.Opened
                    || targetEntry == null
                    || !TargetMatchesMarkerlessJournal(
                        targetEntry,
                        journal,
                        requireTargetPhysicalIdentity)
                    || !await MatchesMarkerlessTargetContentAsync(
                        targetEntry,
                        journal,
                        cancellationToken))
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
            }

            return RegistrationPublicationMatchOutcome.Match;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return RegistrationPublicationMatchOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
    }

    private static async Task<RegistrationPublicationMatchOutcome>
        ProbeMarkerlessMoveRetainedCompletionAsync(
            FileMoveGateLease pathLock,
            FileMutationJournal journal,
            CancellationToken cancellationToken,
            bool requireTargetPhysicalIdentity)
    {
        try
        {
            var sourceParentVisibility =
                pathLock.SourceParent.ProbeVisiblePathMatch();
            var destinationParentVisibility =
                pathLock.DestinationParent.ProbeVisiblePathMatch();
            if (sourceParentVisibility == RegistrationPublicationMatchOutcome.Unavailable
                || destinationParentVisibility
                    == RegistrationPublicationMatchOutcome.Unavailable)
            {
                return RegistrationPublicationMatchOutcome.Unavailable;
            }
            if (sourceParentVisibility != RegistrationPublicationMatchOutcome.Match
                || destinationParentVisibility
                    != RegistrationPublicationMatchOutcome.Match)
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }

            var sourceOutcome =
                pathLock.SourceParent.TryOpenExistingFileWithOutcome(
                    pathLock.SourceName,
                    requireDeleteAccess: false,
                    out var sourceEntry);
            using (sourceEntry)
            {
                if (sourceOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return RegistrationPublicationMatchOutcome.Unavailable;
                }
                if (sourceOutcome != PinnedFileOpenOutcome.Opened
                    || sourceEntry == null
                    || !sourceEntry.VisiblePathMatches()
                    || !await MatchesMarkerlessSourceProofAsync(
                        sourceEntry,
                        journal,
                        cancellationToken))
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
            }

            var targetOutcome =
                pathLock.DestinationParent.TryOpenExistingFileWithOutcome(
                    pathLock.DestinationName,
                    requireDeleteAccess: false,
                    out var targetEntry);
            using (targetEntry)
            {
                if (targetOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return RegistrationPublicationMatchOutcome.Unavailable;
                }
                if (targetOutcome != PinnedFileOpenOutcome.Opened
                    || targetEntry == null
                    || !TargetMatchesMarkerlessJournal(
                        targetEntry,
                        journal,
                        requireTargetPhysicalIdentity)
                    || !await MatchesMarkerlessTargetContentAsync(
                        targetEntry,
                        journal,
                        cancellationToken))
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
            }

            return RegistrationPublicationMatchOutcome.Match;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return RegistrationPublicationMatchOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
    }

    private async Task ValidateMarkerlessMoveJournalAsync(
        FileMutationJournal journal,
        FileMoveGateLease pathLock,
        int? audiobookId,
        int? audiobookFileId)
    {
        if (journal.ProtocolVersion != FileMutationProtocol.Current
            || journal.Action != FileAction.Move
            || journal.AudiobookId != audiobookId
            || journal.AudiobookFileId != audiobookFileId
            || !await JournalPathsMatchGateAsync(journal, pathLock))
        {
            throw new InvalidOperationException(
                "The durable markerless move identity does not match the requested operation.");
        }
    }

    private async Task MarkMarkerlessMoveNeedsAttentionAsync(
        FileMutationJournal journal,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.NeedsAttention,
            journal.TargetPhysicalObjectIdentity,
            journal.AudiobookId,
            reason,
            cancellationToken);
        _logger.LogWarning(
            "Markerless file move {OperationId} requires attention: {Reason}",
            journal.OperationId,
            reason);
    }
}
