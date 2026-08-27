using System.Buffers;
using System.Security.Cryptography;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CaptureMarkerlessSourceIdentitiesAsync(
        AudiobookContentMoveRequest request,
        string source,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        string sourceEndpointIdentity;
        using (var root = OpenPinnedMoveBoundaryDescendant(
            request,
            source,
            request.SourceSemantics,
            sourceBoundary: true))
        {
            var rootIdentity = root.GetDirectoryObjectIdentity();
            sourceEndpointIdentity = rootIdentity;
            var endpoints = await GetEndpointObjectIdentitiesAsync(
                request.JobId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(
                    endpoints.SourceDirectoryObjectIdentity)
                && !root.MatchesDirectoryObjectIdentity(
                    endpoints.SourceDirectoryObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    "The markerless move source root changed physical generation.");
            }
            if (!PinnedDirectoryVisibleOrThrowUnavailable(
                    root,
                    "The markerless move source root is temporarily unavailable while pinned."))
            {
                throw new MoveNeedsAttentionException(
                    "The markerless move source root changed while it was pinned.");
            }
            if (string.IsNullOrWhiteSpace(
                    endpoints.SourceDirectoryObjectIdentity))
            {
                await UpdateEndpointObjectIdentitiesAsync(
                    request.JobId,
                    request.LeaseToken,
                    rootIdentity,
                    targetDirectoryObjectIdentity: null,
                    cancellationToken);
            }
        }

        foreach (var entry in manifest.Where(IsPhysicalManifestEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ResolveManifestPath(
                source,
                entry,
                request.SourceSemantics,
                "source");
            if (entry.EntryType == MoveJobEntryType.File
                && !TryGetMarkerlessPathAttributes(fullPath, out _)
                && IsVerifiedMarkerlessNativeRenameEntry(entry))
            {
                continue;
            }

            var parentPath = Path.GetDirectoryName(fullPath)
                ?? throw new MoveNeedsAttentionException(
                    "A source manifest entry has no parent directory.");
            using var parent = OpenPinnedMoveDescendant(
                request,
                source,
                parentPath,
                request.SourceSemantics,
                sourceEndpointIdentity,
                sourceEndpoint: true);
            string identity;
            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                using var directory = parent.OpenExistingChild(
                    Path.GetFileName(fullPath));
                identity = directory.GetDirectoryObjectIdentity();
                if (!PinnedDirectoryVisibleOrThrowUnavailable(
                        directory,
                        $"Source directory is temporarily unavailable while pinned: {entry.RelativePath}")
                    || (!string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
                        && !directory.MatchesDirectoryObjectIdentity(
                            entry.SourcePhysicalObjectIdentity)))
                {
                    throw new MoveNeedsAttentionException(
                        $"Source directory changed while pinned: {entry.RelativePath}");
                }
            }
            else
            {
                using var file = parent.OpenExistingFile(
                    Path.GetFileName(fullPath),
                    requireDeleteAccess: false);
                ValidatePinnedSourcePhysicalIdentity(request, entry, file);
                if (!PinnedFileLengthMatchesManifest(file, entry))
                {
                    throw new MoveNeedsAttentionException(
                        $"Source file changed while its generation was captured: {entry.RelativePath}");
                }
                identity = file.GetObjectIdentity();
                if (!string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
                    && !file.MatchesObjectIdentity(
                        entry.SourcePhysicalObjectIdentity))
                {
                    throw new MoveNeedsAttentionException(
                        $"Source entry changed physical generation: {entry.RelativePath}");
                }
            }
            if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity))
            {
                await UpdateSourceEntryProofAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    identity,
                    entry.Sha256,
                    entry.LastWriteTimeUtc,
                    cancellationToken);
                entry.SourcePhysicalObjectIdentity = identity;
            }
        }
    }

    private static async Task<(string Sha256, DateTime LastWriteTimeUtc)> ComputeMarkerlessSourceProofHashAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        string fullPath,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        PinnedDirectoryCreation.PinnedFileEntry file,
        long completedWorkUnits,
        long totalWorkUnits,
        CancellationToken cancellationToken)
    {
        var initialLastWriteTimeUtc = file.GetLastWriteTimeUtc();
        await using var stream = file.OpenReadStream(
            bufferSize: 1024 * 1024,
            asynchronous: false);
        if (stream.Length != entry.Length)
        {
            throw new MoveNeedsAttentionException(
                $"Source file length changed before content proof was captured: {entry.RelativePath}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long hashed = 0;
            long lastReported = 0;
            var reportInterval = Math.Max(
                16L * 1024 * 1024,
                Math.Max(totalWorkUnits / 100, 1));
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                hashed += read;
                if (hashed - lastReported >= reportInterval)
                {
                    lastReported = hashed;
                    await ReportProgressAsync(
                        request,
                        CalculateWeightedProgress(
                            5,
                            65,
                            completedWorkUnits + hashed,
                            totalWorkUnits),
                        "Verifying source",
                        cancellationToken);
                }
            }

            if (hashed != entry.Length
                || !PinnedFileVisibleOrThrowUnavailable(
                    file,
                    $"Source file is temporarily unavailable during content proof capture: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    parent,
                    $"Source file parent is temporarily unavailable during content proof capture: {entry.RelativePath}")
                || file.GetLastWriteTimeUtc() != initialLastWriteTimeUtc)
            {
                throw new MoveNeedsAttentionException(
                    $"Source file changed while its content proof was being captured: {entry.RelativePath}");
            }

            ValidateMarkerlessSourceEntry(request, entry, file);
            return (
                Convert.ToHexString(hash.GetHashAndReset()),
                initialLastWriteTimeUtc);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
