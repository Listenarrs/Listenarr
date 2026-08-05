using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private enum HardlinkRegistrationPublicationOutcome
    {
        Published,
        FallbackAllowed,
        RecoveryRequired,
        Conflict
    }

    private readonly record struct HardlinkRegistrationPublicationAttempt(
        HardlinkRegistrationPublicationOutcome Outcome,
        IAudiobookFileRegistrationLease? Lease,
        Exception? Failure);

    private readonly record struct RegistrationPublicationRecovery(
        bool StateFound,
        IAudiobookFileRegistrationLease? Lease);

    private sealed record RegistrationPublicationStateIdentity(
        string CandidatePrefix,
        string CurrentStateName,
        string LegacyStateName);

    private async Task<RegistrationPublicationRecovery>
        TryRecoverHardlinkRegistrationPublicationAsync(
            string source,
            string destination,
            Guid operationId)
    {
        using var gate = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (gate == null)
        {
            return new RegistrationPublicationRecovery(true, null);
        }

        var logicalIdentity = GetRegistrationPublicationLogicalIdentity(
            gate,
            operationId);
        var candidateNames = FindRegistrationPublicationStateCandidates(
            gate.DestinationParent,
            logicalIdentity);
        if (candidateNames.Count == 0)
        {
            return new RegistrationPublicationRecovery(false, null);
        }
        if (candidateNames.Count != 1)
        {
            return new RegistrationPublicationRecovery(true, null);
        }

        using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
            gate.SourceName,
            requireDeleteAccess: false);
        if (sourceEntry == null
            || !sourceEntry.VisiblePathMatches())
        {
            return new RegistrationPublicationRecovery(true, null);
        }

        var stateIdentity = GetRegistrationPublicationStateIdentity(
            logicalIdentity,
            GetRegistrationPublicationSourceIdentity(sourceEntry));
        var stateName = candidateNames[0];
        using var statePublication =
            gate.DestinationParent.TryOpenExistingChildForPublication(stateName);
        if (statePublication == null)
        {
            return new RegistrationPublicationRecovery(true, null);
        }

        using var state = statePublication.OpenCreatedDirectoryAnchor();
        if (!gate.SourceParent.VisiblePathMatches()
            || !gate.DestinationParent.VisiblePathMatches()
            || !state.VisiblePathMatches()
            || !AnchoredStateContainsOnly(
                state,
                "publication.claim",
                RegistrationCleanupIntentName))
        {
            return new RegistrationPublicationRecovery(true, null);
        }

        var claim = state.TryOpenExistingFile(
            "publication.claim",
            requireDeleteAccess: true);
        var destinationEntry = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        try
        {
            if (claim == null)
            {
                // Only a generation-bound state may recreate its missing claim.
                // A legacy empty state or a state for another source generation is
                // preserved and fails closed.
                if (!StateNameEquals(
                        stateName,
                        stateIdentity.CurrentStateName)
                    || destinationEntry != null)
                {
                    return new RegistrationPublicationRecovery(true, null);
                }

                claim = sourceEntry.CreateHardLinkTo(
                    state,
                    "publication.claim");
                FlushFileMoveDirectory(
                    state,
                    "recovered registration-publication claim creation");
            }

            if (!claim.VisiblePathMatches()
                || !sourceEntry.IdentifiesSameEntry(claim)
                || !RegistrationPublicationStateMatchesCurrentSource(
                    stateName,
                    logicalIdentity,
                    sourceEntry))
            {
                return new RegistrationPublicationRecovery(true, null);
            }

            if (destinationEntry == null)
            {
                destinationEntry = claim.CreateHardLinkTo(
                    gate.DestinationParent,
                    gate.DestinationName);
                FlushFileMoveDirectory(
                    gate.DestinationParent,
                    "recovered registration publication");
            }

            if (!destinationEntry.VisiblePathMatches()
                || !claim.IdentifiesSameEntry(destinationEntry)
                || !RegistrationPublicationStateMatchesCurrentSource(
                    stateName,
                    logicalIdentity,
                    sourceEntry))
            {
                return new RegistrationPublicationRecovery(true, null);
            }

            var physicalObjectIdentity = destinationEntry.GetObjectIdentity();
            var sourcePhysicalObjectIdentity = sourceEntry.GetObjectIdentity();
            var registrationLease = PinnedAudiobookFileRegistrationLease.Create(
                destinationEntry.OpenStableRegistrationCopy(),
                destination,
                physicalObjectIdentity,
                sourcePhysicalObjectIdentity,
                audiobookId => PrepareHardlinkRegistrationCleanupRecovery(
                    destination,
                    stateName,
                    physicalObjectIdentity,
                    audiobookId,
                    source,
                    sourcePhysicalObjectIdentity),
                () => CompleteHardlinkRegistrationPublication(
                    destination,
                    stateName,
                    physicalObjectIdentity));
            return new RegistrationPublicationRecovery(true, registrationLease);
        }
        finally
        {
            destinationEntry?.Dispose();
            claim?.Dispose();
        }
    }

    private async Task<HardlinkRegistrationPublicationAttempt>
        TryPublishHardlinkRegistrationAsync(
            FileMoveGateLease gate,
            PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
            string destination,
            Guid operationId)
    {
        var logicalIdentity = GetRegistrationPublicationLogicalIdentity(
            gate,
            operationId);
        var sourceStateIdentity =
            GetRegistrationPublicationSourceIdentity(sourceEntry);
        var stateIdentity = GetRegistrationPublicationStateIdentity(
            logicalIdentity,
            sourceStateIdentity);
        if (FindRegistrationPublicationStateCandidates(
                gate.DestinationParent,
                logicalIdentity).Count != 0)
        {
            return new HardlinkRegistrationPublicationAttempt(
                HardlinkRegistrationPublicationOutcome.Conflict,
                null,
                null);
        }

        PinnedDirectoryCreation? statePublication = null;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? state = null;
        PinnedDirectoryCreation.PinnedFileEntry? claim = null;
        PinnedDirectoryCreation.PinnedFileEntry? published = null;
        var claimPrepared = false;
        try
        {
            statePublication = CreateAnchoredFileMoveStateDirectory(
                gate.DestinationParent,
                stateIdentity.CurrentStateName);
            state = statePublication.OpenCreatedDirectoryAnchor();
            FlushFileMoveDirectory(
                gate.DestinationParent,
                "registration-publication state creation");
            if (AfterRegistrationPublicationStatePreparedForTestAsync != null)
            {
                await AfterRegistrationPublicationStatePreparedForTestAsync();
            }
            if (!RegistrationPublicationSourceIdentityMatches(
                    sourceEntry,
                    sourceStateIdentity))
            {
                return new HardlinkRegistrationPublicationAttempt(
                    HardlinkRegistrationPublicationOutcome.Conflict,
                    null,
                    null);
            }

            claim = sourceEntry.CreateHardLinkTo(state, "publication.claim");
            claimPrepared = true;
            FlushFileMoveDirectory(
                state,
                "registration-publication claim creation");
            if (AfterRegistrationPublicationClaimPreparedForTestAsync != null)
            {
                await AfterRegistrationPublicationClaimPreparedForTestAsync();
            }
            if (!RegistrationPublicationSourceIdentityMatches(
                    sourceEntry,
                    sourceStateIdentity))
            {
                return new HardlinkRegistrationPublicationAttempt(
                    HardlinkRegistrationPublicationOutcome.RecoveryRequired,
                    null,
                    null);
            }

            using var appearedDestination =
                gate.DestinationParent.TryOpenExistingFile(
                    gate.DestinationName,
                    requireDeleteAccess: false);
            if (appearedDestination != null)
            {
                throw new IOException(
                    "The registration destination appeared before publication.");
            }

            published = claim.CreateHardLinkTo(
                gate.DestinationParent,
                gate.DestinationName);
            FlushFileMoveDirectory(
                gate.DestinationParent,
                "registration destination publication");
            if (AfterRegistrationDestinationPublishedForTestAsync != null)
            {
                await AfterRegistrationDestinationPublishedForTestAsync();
            }
            if (!RegistrationPublicationSourceIdentityMatches(
                    sourceEntry,
                    sourceStateIdentity))
            {
                return new HardlinkRegistrationPublicationAttempt(
                    HardlinkRegistrationPublicationOutcome.RecoveryRequired,
                    null,
                    null);
            }

            var physicalObjectIdentity = published.GetObjectIdentity();
            var sourcePhysicalObjectIdentity = sourceEntry.GetObjectIdentity();
            var sourcePath = gate.SourcePath;
            var registrationLease = PinnedAudiobookFileRegistrationLease.Create(
                published.OpenStableRegistrationCopy(),
                destination,
                physicalObjectIdentity,
                sourcePhysicalObjectIdentity,
                audiobookId => PrepareHardlinkRegistrationCleanupRecovery(
                    destination,
                    stateIdentity.CurrentStateName,
                    physicalObjectIdentity,
                    audiobookId,
                    sourcePath,
                    sourcePhysicalObjectIdentity),
                () => CompleteHardlinkRegistrationPublication(
                    destination,
                    stateIdentity.CurrentStateName,
                    physicalObjectIdentity));
            return new HardlinkRegistrationPublicationAttempt(
                HardlinkRegistrationPublicationOutcome.Published,
                registrationLease,
                null);
        }
        catch (Exception exception) when (exception is
            IOException or System.ComponentModel.Win32Exception
                or PlatformNotSupportedException)
        {
            if (statePublication == null || state == null)
            {
                return new HardlinkRegistrationPublicationAttempt(
                    HardlinkRegistrationPublicationOutcome.Conflict,
                    null,
                    exception);
            }

            if (claimPrepared)
            {
                return new HardlinkRegistrationPublicationAttempt(
                    HardlinkRegistrationPublicationOutcome.RecoveryRequired,
                    null,
                    exception);
            }

            var stateIsEmpty = AnchoredStateContainsOnly(state);
            state.Dispose();
            state = null;
            if (stateIsEmpty
                && TryRetireEmptyRegistrationPublicationState(
                    statePublication,
                    gate.DestinationParent,
                    stateIdentity.CurrentStateName))
            {
                return new HardlinkRegistrationPublicationAttempt(
                    HardlinkRegistrationPublicationOutcome.FallbackAllowed,
                    null,
                    exception);
            }

            return new HardlinkRegistrationPublicationAttempt(
                HardlinkRegistrationPublicationOutcome.RecoveryRequired,
                null,
                exception);
        }
        finally
        {
            published?.Dispose();
            claim?.Dispose();
            state?.Dispose();
            statePublication?.Dispose();
        }
    }

    private bool TryRetireEmptyRegistrationPublicationState(
        PinnedDirectoryCreation statePublication,
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string stateName)
    {
        try
        {
            statePublication.RetirePinnedEmptyDirectoryFromNamespace(
                stateName);
            FlushFileMoveDirectory(
                destinationParent,
                "abandoned registration-publication state retirement");
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetRegistrationPublicationLogicalIdentity(
        FileMoveGateLease gate,
        Guid operationId) =>
        FormattableString.Invariant(
            $"{operationId:N}\0{gate.SourceIdentity}\0{gate.DestinationIdentity}\0{FileAction.HardlinkCopy}");

    private static string GetRegistrationPublicationSourceIdentity(
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry)
    {
        using var stream = sourceEntry.OpenReadStream(
            bufferSize: 1,
            asynchronous: false);
        return FormattableString.Invariant(
            $"{sourceEntry.GetObjectIdentity()}\0{stream.Length}");
    }

    private static bool RegistrationPublicationStateMatchesCurrentSource(
        string stateName,
        string logicalIdentity,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry)
    {
        var stateIdentity = GetRegistrationPublicationStateIdentity(
            logicalIdentity,
            GetRegistrationPublicationSourceIdentity(sourceEntry));
        return StateNameEquals(stateName, stateIdentity.LegacyStateName)
            || StateNameEquals(stateName, stateIdentity.CurrentStateName);
    }

    private static bool RegistrationPublicationSourceIdentityMatches(
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        string expectedIdentity) =>
        string.Equals(
            GetRegistrationPublicationSourceIdentity(sourceEntry),
            expectedIdentity,
            StringComparison.Ordinal);

    private static RegistrationPublicationStateIdentity
        GetRegistrationPublicationStateIdentity(
            string logicalIdentity,
            string sourcePhysicalObjectIdentity)
    {
        var logicalName =
            $".listenarr-registration-publication-{HashPathIdentity(logicalIdentity)}";
        return new RegistrationPublicationStateIdentity(
            $"{logicalName}-",
            $"{logicalName}-{HashPathIdentity(sourcePhysicalObjectIdentity)}.state",
            $"{logicalName}.state");
    }

    private static IReadOnlyList<string>
        FindRegistrationPublicationStateCandidates(
            PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
            string logicalIdentity)
    {
        var identity = GetRegistrationPublicationStateIdentity(
            logicalIdentity,
            sourcePhysicalObjectIdentity: string.Empty);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var candidates = Directory.EnumerateFileSystemEntries(
                destinationParent.FullPath,
                ".listenarr-registration-publication-*.state",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name != null
                && (comparer.Equals(name, identity.LegacyStateName)
                    || (name.StartsWith(identity.CandidatePrefix,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal)
                        && name.EndsWith(
                            ".state",
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))))
            .Select(name => name!)
            .Distinct(comparer)
            .ToArray();
        if (!destinationParent.VisiblePathMatches())
        {
            throw new IOException(
                "The registration-publication destination parent changed during state discovery.");
        }

        return candidates;
    }

    private static bool StateNameEquals(string first, string second) =>
        string.Equals(
            first,
            second,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
