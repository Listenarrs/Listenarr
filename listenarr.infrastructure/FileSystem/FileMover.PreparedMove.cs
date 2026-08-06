using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    public async Task<bool> CompletePreparedMoveAsync(
        string source,
        string destination,
        IAudiobookFileRegistrationLease registrationLease,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(registrationLease);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

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

            if (!registrationLease.MatchesCurrentPublication())
            {
                return false;
            }

            var leaseMatchesDestination = await IsSameFilesystemPathAsync(
                registrationLease.PublicPath,
                destination);
            if (leaseMatchesDestination != true)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    source,
                    destination,
                    "The registration lease does not identify the requested destination path");
                return false;
            }

            if (await IsSameFilesystemPathAsync(source, destination) == true)
            {
                return registrationLease.MatchesCurrentPublication();
            }

            using var gate = await TryAcquireFileMoveGateAsync(source, destination);
            if (gate == null
                || !gate.SourceParent.VisiblePathMatches()
                || !gate.DestinationParent.VisiblePathMatches()
                || !registrationLease.MatchesCurrentPublication())
            {
                return false;
            }

            var claimName = GetPreparedMoveClaimName(gate, operationId);
            var claimOutcome =
                gate.SourceParent.TryOpenExistingFileForStableDeleteWithOutcome(
                    claimName,
                    out var recoveredClaim);
            using (recoveredClaim)
            {
                if (claimOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return false;
                }

                if (claimOutcome == PinnedFileOpenOutcome.Opened)
                {
                    return await RecoverPreparedMoveClaimAsync(
                        gate,
                        recoveredClaim!,
                        registrationLease);
                }
            }

            var sourceOutcome =
                gate.SourceParent.TryOpenExistingFileForStableDeleteWithOutcome(
                    gate.SourceName,
                    out var sourceEntry);
            using (sourceEntry)
            {
                if (sourceOutcome == PinnedFileOpenOutcome.NotFound)
                {
                    return registrationLease.MatchesCurrentPublication();
                }

                if (sourceOutcome != PinnedFileOpenOutcome.Opened
                    || sourceEntry == null
                    || string.IsNullOrWhiteSpace(
                        registrationLease.SourcePhysicalObjectIdentity)
                    || !string.Equals(
                        sourceEntry.GetObjectIdentity(),
                        registrationLease.SourcePhysicalObjectIdentity,
                        StringComparison.Ordinal)
                    || !await RegistrationLeaseMatchesFileAsync(
                        registrationLease,
                        sourceEntry)
                    || !sourceEntry.VisiblePathMatches()
                    || !registrationLease.MatchesCurrentPublication())
                {
                    return false;
                }

                sourceEntry.MoveWithinParent(claimName);
                FlushFileMoveDirectory(
                    gate.SourceParent,
                    "prepared move source quarantine");

                var quarantined = true;
                try
                {
                    if (!await RegistrationLeaseMatchesFileAsync(
                            registrationLease,
                            sourceEntry)
                        || !sourceEntry.VisiblePathMatches()
                        || !registrationLease.MatchesCurrentPublication())
                    {
                        _ = TryRestorePreparedMoveClaim(
                            gate,
                            sourceEntry);
                        return false;
                    }

                    sourceEntry.Delete(immediateWindows: true);
                    quarantined = false;
                    FlushFileMoveDirectory(
                        gate.SourceParent,
                        "prepared move source retirement");
                    if (AfterPreparedMoveSourceDeletedForTestAsync != null)
                    {
                        await AfterPreparedMoveSourceDeletedForTestAsync(
                            destination);
                    }

                    var recreatedOutcome =
                        gate.SourceParent.TryOpenExistingFileWithOutcome(
                            gate.SourceName,
                            requireDeleteAccess: false,
                            out var recreatedSource);
                    recreatedSource?.Dispose();
                    if (recreatedOutcome != PinnedFileOpenOutcome.NotFound)
                    {
                        _ = await TryPreserveDeletedPreparedMoveSourceAsync(
                            gate,
                            sourceEntry,
                            claimName,
                            preferVisibleSource: false);
                        LogMutation(
                            FileMutationOutcome.Blocked,
                            FileAction.Move,
                            source,
                            destination,
                            "A new source generation appeared during prepared move completion");
                        return false;
                    }

                    if (!registrationLease.MatchesCurrentPublication())
                    {
                        _ = await TryPreserveDeletedPreparedMoveSourceAsync(
                            gate,
                            sourceEntry,
                            claimName,
                            preferVisibleSource: true);
                        return false;
                    }

                    LogMutation(
                        FileMutationOutcome.Success,
                        FileAction.Move,
                        source,
                        destination,
                        "Retired the verified source while preserving the registered destination generation");
                    return true;
                }
                catch
                {
                    if (quarantined)
                    {
                        _ = TryRestorePreparedMoveClaim(gate, sourceEntry);
                    }
                    else
                    {
                        _ = await TryPreserveDeletedPreparedMoveSourceAsync(
                            gate,
                            sourceEntry,
                            claimName,
                            preferVisibleSource: true);
                    }

                    throw;
                }
            }
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

    private async Task<bool> RecoverPreparedMoveClaimAsync(
        FileMoveGateLease gate,
        PinnedDirectoryCreation.PinnedFileEntry claim,
        IAudiobookFileRegistrationLease registrationLease)
    {
        var sourceOutcome = gate.SourceParent.TryOpenExistingFileWithOutcome(
            gate.SourceName,
            requireDeleteAccess: false,
            out var sourceEntry);
        sourceEntry?.Dispose();
        if (sourceOutcome != PinnedFileOpenOutcome.NotFound)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(
                registrationLease.SourcePhysicalObjectIdentity)
            || !string.Equals(
                claim.GetObjectIdentity(),
                registrationLease.SourcePhysicalObjectIdentity,
                StringComparison.Ordinal)
            || !await RegistrationLeaseMatchesFileAsync(
                registrationLease,
                claim)
            || !claim.VisiblePathMatches()
            || !registrationLease.MatchesCurrentPublication())
        {
            _ = TryRestorePreparedMoveClaim(gate, claim);
            return false;
        }

        claim.Delete(immediateWindows: true);
        try
        {
            FlushFileMoveDirectory(
                gate.SourceParent,
                "recovered prepared move source retirement");
            if (AfterPreparedMoveSourceDeletedForTestAsync != null)
            {
                await AfterPreparedMoveSourceDeletedForTestAsync(
                    gate.DestinationPath);
            }

            var recreatedOutcome =
                gate.SourceParent.TryOpenExistingFileWithOutcome(
                    gate.SourceName,
                    requireDeleteAccess: false,
                    out var recreatedSource);
            recreatedSource?.Dispose();
            if (recreatedOutcome != PinnedFileOpenOutcome.NotFound)
            {
                _ = await TryPreserveDeletedPreparedMoveSourceAsync(
                    gate,
                    claim,
                    claim.FileName,
                    preferVisibleSource: false);
                return false;
            }

            if (!registrationLease.MatchesCurrentPublication())
            {
                _ = await TryPreserveDeletedPreparedMoveSourceAsync(
                    gate,
                    claim,
                    claim.FileName,
                    preferVisibleSource: true);
                return false;
            }

            return true;
        }
        catch
        {
            _ = await TryPreserveDeletedPreparedMoveSourceAsync(
                gate,
                claim,
                claim.FileName,
                preferVisibleSource: true);
            throw;
        }
    }

    private static async Task<bool> RegistrationLeaseMatchesFileAsync(
        IAudiobookFileRegistrationLease registrationLease,
        PinnedDirectoryCreation.PinnedFileEntry candidate)
    {
        await using var candidateStream = candidate.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        return await registrationLease.MatchesContentAsync(
            candidateStream,
            CancellationToken.None);
    }

    private async Task<bool> TryPreserveDeletedPreparedMoveSourceAsync(
        FileMoveGateLease gate,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        string claimName,
        bool preferVisibleSource)
    {
        if (preferVisibleSource
            && await sourceEntry.TryRestoreUnlinkedCopyToAsync(
                gate.SourceParent,
                gate.SourceName))
        {
            return true;
        }

        return await sourceEntry.TryRestoreUnlinkedCopyToAsync(
            gate.SourceParent,
            claimName);
    }

    private bool TryRestorePreparedMoveClaim(
        FileMoveGateLease gate,
        PinnedDirectoryCreation.PinnedFileEntry claim)
    {
        try
        {
            if (!gate.SourceParent.VisiblePathMatches()
                || !claim.VisiblePathMatches())
            {
                return false;
            }

            var sourceOutcome = gate.SourceParent.TryOpenExistingFileWithOutcome(
                gate.SourceName,
                requireDeleteAccess: false,
                out var sourceEntry);
            sourceEntry?.Dispose();
            if (sourceOutcome != PinnedFileOpenOutcome.NotFound)
            {
                return false;
            }

            claim.MoveWithinParent(gate.SourceName);
            FlushFileMoveDirectory(
                gate.SourceParent,
                "prepared move source restoration");
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Could not restore a prepared move source claim: {Source}",
                LogRedaction.SanitizeFilePath(gate.SourcePath));
            return false;
        }
    }

    private static string GetPreparedMoveClaimName(
        FileMoveGateLease gate,
        Guid? operationId)
    {
        var identity = FormattableString.Invariant(
            $"{operationId?.ToString("N") ?? "none"}\0{gate.SourceIdentity}\0{gate.DestinationIdentity}");
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $".listenarr-registration-move-{digest[..32]}.claim";
    }
}
