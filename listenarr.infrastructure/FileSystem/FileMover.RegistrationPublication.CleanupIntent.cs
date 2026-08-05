using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed record RegistrationPublicationCleanupCandidate(
    string StateDirectoryPath,
    string StateName,
    int AudiobookId,
    string DestinationPath,
    string PhysicalObjectIdentity,
    string? SourcePath,
    string? SourcePhysicalObjectIdentity,
    bool DestinationExists);

public partial class FileMover
{
    private const string RegistrationCleanupIntentName = "registration.cleanup.json";
    private const int RegistrationCleanupIntentVersion = 2;

    private sealed record RegistrationCleanupIntent(
        int Version,
        int AudiobookId,
        string DestinationName,
        string PhysicalObjectIdentity,
        string? SourcePath = null,
        string? SourcePhysicalObjectIdentity = null);

    private bool PrepareHardlinkRegistrationCleanupRecovery(
        string destination,
        string stateName,
        string expectedPhysicalObjectIdentity,
        int audiobookId,
        string source,
        string sourcePhysicalObjectIdentity)
    {
        try
        {
            var destinationPath = Path.GetFullPath(destination);
            var sourcePath = Path.GetFullPath(source);
            var parentPath = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var statePublication =
                parent.TryOpenExistingChildForPublication(stateName);
            if (statePublication == null)
            {
                using var completed = parent.TryOpenExistingFile(
                    Path.GetFileName(destinationPath),
                    requireDeleteAccess: false);
                return completed != null
                    && parent.VisiblePathMatches()
                    && completed.VisiblePathMatches()
                    && string.Equals(
                        completed.GetObjectIdentity(),
                        expectedPhysicalObjectIdentity,
                        StringComparison.Ordinal);
            }

            using var state = statePublication.OpenCreatedDirectoryAnchor();
            if (!parent.VisiblePathMatches()
                || !state.VisiblePathMatches()
                || !AnchoredStateContainsOnly(
                    state,
                    "publication.claim",
                    RegistrationCleanupIntentName))
            {
                return false;
            }

            using var published = parent.TryOpenExistingFile(
                Path.GetFileName(destinationPath),
                requireDeleteAccess: false);
            if (published == null
                || !published.VisiblePathMatches()
                || !string.Equals(
                    published.GetObjectIdentity(),
                    expectedPhysicalObjectIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }

            using var existingIntent = state.TryOpenExistingFile(
                RegistrationCleanupIntentName,
                requireDeleteAccess: false);
            if (existingIntent != null)
            {
                var intent = ReadRegistrationCleanupIntent(existingIntent);
                return intent != null
                    && intent.Version == RegistrationCleanupIntentVersion
                    && intent.AudiobookId == audiobookId
                    && string.Equals(
                        intent.DestinationName,
                        Path.GetFileName(destinationPath),
                        StringComparison.Ordinal)
                    && string.Equals(
                        intent.PhysicalObjectIdentity,
                        expectedPhysicalObjectIdentity,
                        StringComparison.Ordinal)
                    && string.Equals(
                        Path.GetFullPath(intent.SourcePath ?? string.Empty),
                        sourcePath,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal)
                    && string.Equals(
                        intent.SourcePhysicalObjectIdentity,
                        sourcePhysicalObjectIdentity,
                        StringComparison.Ordinal)
                    && existingIntent.VisiblePathMatches();
            }

            using var claim = state.TryOpenExistingFile(
                "publication.claim",
                requireDeleteAccess: false);
            if (claim == null
                || !claim.VisiblePathMatches()
                || !claim.IdentifiesSameEntry(published))
            {
                return false;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new RegistrationCleanupIntent(
                    RegistrationCleanupIntentVersion,
                    audiobookId,
                    Path.GetFileName(destinationPath),
                    expectedPhysicalObjectIdentity,
                    sourcePath,
                    sourcePhysicalObjectIdentity));
            using var intentEntry = state.CreateNewFile(RegistrationCleanupIntentName);
            using (var stream = intentEntry.OpenWriteStream(
                       bufferSize: 4096,
                       asynchronous: false))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            FlushFileMoveDirectory(
                state,
                "registration-publication cleanup intent creation");
            return parent.VisiblePathMatches()
                && state.VisiblePathMatches()
                && published.VisiblePathMatches()
                && intentEntry.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Could not persist durable registration cleanup intent for {Destination}",
                LogRedaction.SanitizeFilePath(destination));
            return false;
        }
    }

    internal RegistrationPublicationCleanupCandidate?
        TryReadRegistrationPublicationCleanupCandidate(string stateDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectoryPath);
        try
        {
            var fullStatePath = Path.GetFullPath(stateDirectoryPath);
            var stateName = Path.GetFileName(fullStatePath);
            if (string.IsNullOrWhiteSpace(stateName)
                || !stateName.StartsWith(
                    ".listenarr-registration-publication-",
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                || !stateName.EndsWith(
                    ".state",
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return null;
            }

            var parentPath = Path.GetDirectoryName(fullStatePath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return null;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var statePublication =
                parent.TryOpenExistingChildForPublication(stateName);
            if (statePublication == null)
            {
                return null;
            }

            using var state = statePublication.OpenCreatedDirectoryAnchor();
            if (!parent.VisiblePathMatches()
                || !state.VisiblePathMatches()
                || !AnchoredStateContainsOnly(
                    state,
                    "publication.claim",
                    RegistrationCleanupIntentName))
            {
                return null;
            }

            using var intentEntry = state.TryOpenExistingFile(
                RegistrationCleanupIntentName,
                requireDeleteAccess: false);
            if (intentEntry == null || !intentEntry.VisiblePathMatches())
            {
                return null;
            }

            var intent = ReadRegistrationCleanupIntent(intentEntry);
            if (intent == null
                || intent.Version is not (1 or RegistrationCleanupIntentVersion)
                || intent.AudiobookId <= 0
                || string.IsNullOrWhiteSpace(intent.DestinationName)
                || Path.GetFileName(intent.DestinationName) != intent.DestinationName
                || string.IsNullOrWhiteSpace(intent.PhysicalObjectIdentity)
                || (intent.Version == RegistrationCleanupIntentVersion
                    && (string.IsNullOrWhiteSpace(intent.SourcePath)
                        || string.IsNullOrWhiteSpace(
                            intent.SourcePhysicalObjectIdentity))))
            {
                return null;
            }

            var destinationPath = Path.Join(parentPath, intent.DestinationName);
            using var published = parent.TryOpenExistingFile(
                intent.DestinationName,
                requireDeleteAccess: false);
            if (published != null
                && (!published.VisiblePathMatches()
                    || !string.Equals(
                        published.GetObjectIdentity(),
                        intent.PhysicalObjectIdentity,
                        StringComparison.Ordinal)))
            {
                return null;
            }

            using var claim = state.TryOpenExistingFile(
                "publication.claim",
                requireDeleteAccess: false);
            if (claim != null && !claim.VisiblePathMatches())
            {
                return null;
            }
            if (published != null
                && claim != null
                && !claim.IdentifiesSameEntry(published))
            {
                return null;
            }

            string? sourcePath = null;
            if (intent.Version == RegistrationCleanupIntentVersion)
            {
                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        intent.SourcePath!,
                        out var canonicalSourcePath,
                        out _))
                {
                    return null;
                }

                // A committed move intentionally retires the source before the
                // registration-publication state is cleaned up. Candidate discovery
                // therefore validates only the persisted source-path syntax here.
                // Any destructive rollback reopens and verifies the exact source
                // generation again in TryRollbackUncommittedRegistrationPublication.
                sourcePath = canonicalSourcePath;
            }
            else if (published == null)
            {
                return null;
            }

            return new RegistrationPublicationCleanupCandidate(
                fullStatePath,
                stateName,
                intent.AudiobookId,
                destinationPath,
                intent.PhysicalObjectIdentity,
                sourcePath,
                intent.SourcePhysicalObjectIdentity,
                published != null);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogDebug(
                exception,
                "Ignored invalid registration-publication cleanup state at {Path}",
                LogRedaction.SanitizeFilePath(stateDirectoryPath));
            return null;
        }
    }

    internal bool TryCompleteRegistrationPublicationCleanup(
        RegistrationPublicationCleanupCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var current = TryReadRegistrationPublicationCleanupCandidate(
            candidate.StateDirectoryPath);
        if (current == null
            || !current.DestinationExists
            || current.AudiobookId != candidate.AudiobookId
            || !string.Equals(
                current.StateName,
                candidate.StateName,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFullPath(current.DestinationPath),
                Path.GetFullPath(candidate.DestinationPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !string.Equals(
                current.PhysicalObjectIdentity,
                candidate.PhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        return CompleteHardlinkRegistrationPublication(
            current.DestinationPath,
            current.StateName,
            current.PhysicalObjectIdentity);
    }

    private static RegistrationCleanupIntent? ReadRegistrationCleanupIntent(
        PinnedDirectoryCreation.PinnedFileEntry intentEntry)
    {
        try
        {
            using var stream = intentEntry.OpenReadStream(
                bufferSize: 4096,
                asynchronous: false);
            return JsonSerializer.Deserialize<RegistrationCleanupIntent>(stream);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static bool RegistrationCleanupIntentMatchesPublication(
        PinnedDirectoryCreation.PinnedFileEntry intentEntry,
        string destinationName,
        string expectedPhysicalObjectIdentity)
    {
        if (!intentEntry.VisiblePathMatches())
        {
            return false;
        }

        var intent = ReadRegistrationCleanupIntent(intentEntry);
        return intent != null
            && intent.Version is 1 or RegistrationCleanupIntentVersion
            && string.Equals(
                intent.DestinationName,
                destinationName,
                StringComparison.Ordinal)
            && string.Equals(
                intent.PhysicalObjectIdentity,
                expectedPhysicalObjectIdentity,
                StringComparison.Ordinal)
            && intentEntry.VisiblePathMatches();
    }

    private bool DeletePinnedRegistrationCleanupIntent(
        PinnedDirectoryCreation.PinnedDirectoryAnchor state,
        PinnedDirectoryCreation.PinnedFileEntry intent)
    {
        if (!intent.VisiblePathMatches())
        {
            return false;
        }

        intent.Delete(immediateWindows: true);
        intent.Dispose();
        FlushFileMoveDirectory(
            state,
            "registration-publication cleanup intent retirement");
        return true;
    }
}
