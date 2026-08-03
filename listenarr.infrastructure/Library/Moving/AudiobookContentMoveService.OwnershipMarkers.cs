using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const int OwnershipMarkerVersion = 1;
    private const string TemporaryDirectoryArtifactType = "temporary-directory";
    private const string QuarantineDirectoryArtifactType = "quarantine-directory";
    private const string CleanupTombstoneArtifactType = "cleanup-tombstone";

    private sealed record MoveOwnershipMarker(
        int Version,
        string ArtifactType,
        Guid JobId,
        string Source,
        string Target,
        string DirectoryPath,
        string? OwnedArtifactType = null,
        string? OwnedDirectoryPath = null);

    private MoveOwnershipMarker CreateOwnershipMarker(
        string artifactType,
        Guid jobId,
        string source,
        string target,
        string directoryPath,
        string? ownedArtifactType = null,
        string? ownedDirectoryPath = null) =>
        new(
            OwnershipMarkerVersion,
            artifactType,
            jobId,
            Path.GetFullPath(source),
            Path.GetFullPath(target),
            Path.GetFullPath(directoryPath),
            ownedArtifactType,
            string.IsNullOrWhiteSpace(ownedDirectoryPath)
                ? null
                : Path.GetFullPath(ownedDirectoryPath));

    private async Task<MoveOwnershipMarker> RecoverOrReadOwnershipMarkerAsync(
        string markerPath,
        MoveOwnershipMarker expected,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        MoveLeaseToken leaseToken,
        Func<Task> authorizeMutation)
    {
        var markerDirectory = Path.GetDirectoryName(Path.GetFullPath(markerPath))
            ?? throw new MoveNeedsAttentionException("The ownership marker directory is unavailable.");
        ValidateExistingMoveDirectory(markerDirectory, "ownership-marker directory");

        if (File.Exists(markerPath))
        {
            var marker = ReadOwnershipMarker(markerPath);
            ValidateOwnershipMarker(
                marker,
                expected,
                sourceSemantics,
                targetSemantics,
                directorySemantics);
            await DeleteValidatedOwnershipWriteFilesAsync(
                markerPath,
                expected,
                sourceSemantics,
                targetSemantics,
                directorySemantics,
                leaseToken,
                authorizeMutation);
            return marker;
        }

        var validWrites = new List<(string Path, MoveOwnershipMarker Marker)>();
        var discardedTruncatedPredecessor = false;
        foreach (var writePath in Directory.EnumerateFiles(
            markerDirectory,
            Path.GetFileName(markerPath) + ".writing-*",
            SearchOption.TopDirectoryOnly))
        {
            ValidateOwnershipMarkerWritePath(writePath, markerDirectory);
            if (!TryParseMarkerWriteIdentity(writePath, markerPath, out var writeIdentity)
                || writeIdentity.JobId != expected.JobId)
            {
                throw new MoveNeedsAttentionException(
                    "An ownership-marker write filename does not match the active move job.");
            }

            if (writeIdentity.LeaseGeneration > leaseToken.Generation)
            {
                throw new MoveNeedsAttentionException(
                    "A future-generation ownership-marker write file was preserved.");
            }

            var recoveredRead = ReadOwnershipMarkerResult(writePath);
            if (recoveredRead.State == MarkerReadState.TemporarilyUnreadable)
            {
                throw new IOException(
                    "An ownership-marker write file is temporarily unreadable and was preserved.",
                    recoveredRead.Error);
            }
            if (recoveredRead.State == MarkerReadState.Unsupported)
            {
                throw new MoveNeedsAttentionException(
                    "An ownership-marker write file uses an unsupported marker version and was preserved.");
            }
            if (recoveredRead.State == MarkerReadState.CorruptOrTruncated)
            {
                if (writeIdentity.LeaseGeneration >= leaseToken.Generation)
                {
                    throw new MoveNeedsAttentionException(
                        "A current or future-generation ownership-marker write file is truncated and was preserved.");
                }

                await RetireCorruptOwnershipWriteAsync(
                    writePath,
                    markerPath,
                    writeIdentity,
                    authorizeMutation);
                discardedTruncatedPredecessor = true;
                continue;
            }

            var recovered = recoveredRead.Marker
                ?? throw new MoveNeedsAttentionException("The ownership-marker write file is missing.");
            ValidateOwnershipMarker(
                recovered,
                expected,
                sourceSemantics,
                targetSemantics,
                directorySemantics);
            validWrites.Add((writePath, recovered));
        }

        if (validWrites.Count == 0)
        {
            if (discardedTruncatedPredecessor)
            {
                throw new InterruptedOwnershipPublicationException(
                    "A truncated predecessor ownership marker was removed; the empty owned directory must be reclaimed.");
            }

            throw new MoveNeedsAttentionException(
                "The owned directory has no valid ownership marker.");
        }

        if (validWrites.Count != 1)
        {
            throw new MoveNeedsAttentionException(
                "The owned directory has multiple incomplete ownership marker publications.");
        }

        var validWritePath = validWrites[0].Path;
        if (OperatingSystem.IsWindows())
        {
            await authorizeMutation();
            ValidateOwnershipMarkerWritePath(validWritePath, markerDirectory);
            File.SetAttributes(
                validWritePath,
                File.GetAttributes(validWritePath) | FileAttributes.Hidden);
        }

        return await PublishRecoveredOwnershipWriteAsync(
            markerPath,
            validWritePath,
            expected,
            sourceSemantics,
            targetSemantics,
            directorySemantics,
            authorizeMutation);
    }

    private static MoveOwnershipMarker ReadOwnershipMarker(string markerPath)
    {
        if (!FileSystemSafety.TryValidateMutationTarget(
                markerPath,
                [Path.GetDirectoryName(markerPath)],
                out markerPath,
                out var markerReason))
        {
            throw new MoveNeedsAttentionException(markerReason);
        }

        if (File.Exists(markerPath)
            && (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException("The ownership marker is linked.");
        }

        var result = ReadOwnershipMarkerResult(markerPath);
        return result.State switch
        {
            MarkerReadState.Valid => result.Marker!,
            MarkerReadState.TemporarilyUnreadable => throw new IOException(
                "The ownership marker is temporarily unreadable.",
                result.Error),
            MarkerReadState.Unsupported => throw new MoveNeedsAttentionException(
                "The ownership marker uses an unsupported marker version and was preserved."),
            MarkerReadState.CorruptOrTruncated => throw new MoveNeedsAttentionException(
                "The ownership marker is corrupt or truncated."),
            _ => throw new MoveNeedsAttentionException("The ownership marker is missing.")
        };
    }

    private static MarkerReadResult<MoveOwnershipMarker> ReadOwnershipMarkerResult(string path)
    {
        var result = ReadJsonMarker<MoveOwnershipMarker>(path);
        if (result.State == MarkerReadState.Valid
            && result.Marker!.Version != OwnershipMarkerVersion)
        {
            return new MarkerReadResult<MoveOwnershipMarker>(
                MarkerReadState.Unsupported,
                result.Marker);
        }

        return result;
    }

    private static void ValidateOwnershipMarker(
        MoveOwnershipMarker marker,
        MoveOwnershipMarker expected,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics)
    {
        if (marker.Version != OwnershipMarkerVersion
            || marker.JobId != expected.JobId
            || !string.Equals(marker.ArtifactType, expected.ArtifactType, StringComparison.Ordinal)
            || !string.Equals(marker.OwnedArtifactType, expected.OwnedArtifactType, StringComparison.Ordinal)
            || (marker.OwnedDirectoryPath == null) != (expected.OwnedDirectoryPath == null))
        {
            throw new MoveNeedsAttentionException(
                "The owned directory is owned by another job, artifact type, or unsupported marker version.");
        }

        try
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    marker.Source,
                    out var markerSource,
                    out _,
                    sourceSemantics.Syntax)
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    marker.Target,
                    out var markerTarget,
                    out _,
                    targetSemantics.Syntax)
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    marker.DirectoryPath,
                    out var markerDirectory,
                    out _,
                    directorySemantics.Syntax))
            {
                throw new MoveNeedsAttentionException(
                    "The ownership marker contains a path that is unavailable on this host.");
            }

            string? markerOwnedDirectory = null;
            if (marker.OwnedDirectoryPath != null
                && !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    marker.OwnedDirectoryPath,
                    out markerOwnedDirectory,
                    out _,
                    directorySemantics.Syntax))
            {
                throw new MoveNeedsAttentionException(
                    "The ownership marker contains an owned-directory path that is unavailable on this host.");
            }

            if (!FileSystemPathIdentity.AreEquivalent(markerSource, expected.Source, sourceSemantics)
                || !FileSystemPathIdentity.AreEquivalent(markerTarget, expected.Target, targetSemantics)
                || !FileSystemPathIdentity.AreEquivalent(
                    markerDirectory,
                    expected.DirectoryPath,
                    directorySemantics)
                || (markerOwnedDirectory != null
                    && expected.OwnedDirectoryPath != null
                    && !FileSystemPathIdentity.AreEquivalent(
                        markerOwnedDirectory,
                        expected.OwnedDirectoryPath,
                        directorySemantics)))
            {
                throw new MoveNeedsAttentionException(
                    "The ownership marker does not match the persisted source, target, or owned directory.");
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                "The ownership marker contains an invalid filesystem identity.");
        }
    }

    private static async Task DeleteValidatedOwnershipWriteFilesAsync(
        string markerPath,
        MoveOwnershipMarker expected,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        MoveLeaseToken leaseToken,
        Func<Task> authorizeMutation)
    {
        var markerDirectory = Path.GetDirectoryName(markerPath)
            ?? throw new MoveNeedsAttentionException("The ownership marker directory is unavailable.");
        foreach (var writePath in Directory.EnumerateFiles(
            markerDirectory,
            Path.GetFileName(markerPath) + ".writing-*",
            SearchOption.TopDirectoryOnly))
        {
            ValidateOwnershipMarkerWritePath(writePath, markerDirectory);
            if (!TryParseMarkerWriteIdentity(writePath, markerPath, out var writeIdentity)
                || writeIdentity.JobId != expected.JobId)
            {
                throw new MoveNeedsAttentionException(
                    "An ownership-marker write filename does not match the active move job.");
            }

            if (writeIdentity.LeaseGeneration > leaseToken.Generation)
            {
                throw new MoveNeedsAttentionException(
                    "A future-generation ownership-marker write file was preserved.");
            }

            var writeRead = ReadOwnershipMarkerResult(writePath);
            if (writeRead.State == MarkerReadState.TemporarilyUnreadable)
            {
                throw new IOException(
                    "An ownership-marker write file is temporarily unreadable and was preserved.",
                    writeRead.Error);
            }
            if (writeRead.State == MarkerReadState.Unsupported)
            {
                throw new MoveNeedsAttentionException(
                    "An ownership-marker write file uses an unsupported marker version and was preserved.");
            }
            if (writeRead.State == MarkerReadState.CorruptOrTruncated)
            {
                if (writeIdentity.LeaseGeneration >= leaseToken.Generation)
                {
                    throw new MoveNeedsAttentionException(
                        "A current or future-generation ownership-marker write file is truncated and was preserved.");
                }

                await RetireCorruptOwnershipWriteAsync(
                    writePath,
                    markerPath,
                    writeIdentity,
                    authorizeMutation);
                continue;
            }

            var writeMarker = writeRead.Marker
                ?? throw new MoveNeedsAttentionException("The ownership-marker write file is missing.");
            ValidateOwnershipMarker(
                writeMarker,
                expected,
                sourceSemantics,
                targetSemantics,
                directorySemantics);
            await RetireValidatedOwnershipWriteAsync(
                writePath,
                expected,
                sourceSemantics,
                targetSemantics,
                directorySemantics,
                authorizeMutation);
        }
    }

    private static void ValidateNewOwnershipMarkerWritePath(
        string writePath,
        string markerDirectory)
    {
        ValidateExistingMoveDirectory(markerDirectory, "ownership-marker directory");
        if (!FileSystemSafety.TryValidateMutationTarget(
                writePath,
                [markerDirectory],
                out writePath,
                out var writeReason))
        {
            throw new MoveNeedsAttentionException(writeReason);
        }

        if (File.Exists(writePath) || Directory.Exists(writePath))
        {
            throw new MoveNeedsAttentionException(
                "The ownership-marker temporary path appeared before creation.");
        }
    }

    private static void ValidateOwnershipMarkerPublicationPaths(
        string markerDirectory,
        string writePath,
        string markerPath)
    {
        ValidateExistingMoveDirectory(markerDirectory, "ownership-marker directory");
        ValidateOwnershipMarkerWritePath(writePath, markerDirectory);
        if (!FileSystemSafety.TryValidateMutationTarget(
                markerPath,
                [markerDirectory],
                out _,
                out var markerReason))
        {
            throw new MoveNeedsAttentionException(markerReason);
        }

        if (File.Exists(markerPath) || Directory.Exists(markerPath))
        {
            throw new MoveNeedsAttentionException(
                "The authoritative ownership marker appeared before publication.");
        }
    }

    private static void ValidateOwnershipMarkerWritePath(
        string writePath,
        string markerDirectory)
    {
        ValidateExistingMoveDirectory(markerDirectory, "ownership-marker directory");
        if (!FileSystemSafety.TryValidateMutationTarget(
                writePath,
                [markerDirectory],
                out writePath,
                out var writeReason))
        {
            throw new MoveNeedsAttentionException(writeReason);
        }

        if (!File.Exists(writePath)
            || (File.GetAttributes(writePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "An ownership-marker temporary file is missing or linked.");
        }
    }

    private static string GetCleanupDirectoryPath(
        string directoryPath,
        string ownedArtifactType,
        Guid jobId)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(directoryPath))
            ?? throw new MoveNeedsAttentionException("The owned directory parent is unavailable.");
        return Path.Join(
            parent,
            $".listenarr-{ownedArtifactType}-{jobId:N}.cleanup-dir");
    }

    private static string GetCleanupTombstonePath(
        string directoryPath,
        string ownedArtifactType,
        Guid jobId)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(directoryPath))
            ?? throw new MoveNeedsAttentionException("The owned directory parent is unavailable.");
        return Path.Join(
            parent,
            $".listenarr-{ownedArtifactType}-{jobId:N}.cleanup.json");
    }
}
