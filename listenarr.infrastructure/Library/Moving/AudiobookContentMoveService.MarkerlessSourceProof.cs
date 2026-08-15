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
        using (var root = PinnedDirectoryCreation.OpenPinnedBoundary(source))
        {
            var rootIdentity = root.GetDirectoryObjectIdentity();
            var endpoints = await GetEndpointObjectIdentitiesAsync(
                request.JobId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(
                    endpoints.SourceDirectoryObjectIdentity)
                && !string.Equals(
                    endpoints.SourceDirectoryObjectIdentity,
                    rootIdentity,
                    StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    "The markerless move source root changed physical generation.");
            }
            if (!root.VisiblePathMatches())
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
                && !File.Exists(fullPath)
                && IsVerifiedMarkerlessNativeRenameEntry(entry))
            {
                continue;
            }

            var parentPath = Path.GetDirectoryName(fullPath)
                ?? throw new MoveNeedsAttentionException(
                    "A source manifest entry has no parent directory.");
            using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
            string identity;
            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                using var directory = parent.OpenExistingChild(
                    Path.GetFileName(fullPath));
                identity = directory.GetDirectoryObjectIdentity();
                if (!directory.VisiblePathMatches())
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
            }

            if (!string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
                && !string.Equals(
                    entry.SourcePhysicalObjectIdentity,
                    identity,
                    StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    $"Source entry changed physical generation: {entry.RelativePath}");
            }
            if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity))
            {
                await UpdateSourceEntryProofAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    identity,
                    entry.Sha256,
                    cancellationToken);
                entry.SourcePhysicalObjectIdentity = identity;
            }
        }
    }

    private static async Task<string> ComputeMarkerlessSourceProofHashAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        string fullPath,
        PinnedDirectoryCreation.PinnedFileEntry file,
        long completedWorkUnits,
        long totalWorkUnits,
        CancellationToken cancellationToken)
    {
        var initialLastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
        await using var stream = file.OpenReadStream(
            bufferSize: 1024 * 1024,
            asynchronous: false);
        if (stream.Length != entry.Length
            || initialLastWriteTimeUtc != entry.LastWriteTimeUtc)
        {
            throw new MoveNeedsAttentionException(
                $"Source file metadata changed before content proof was captured: {entry.RelativePath}");
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
                || !file.VisiblePathMatches()
                || File.GetLastWriteTimeUtc(fullPath) != initialLastWriteTimeUtc)
            {
                throw new MoveNeedsAttentionException(
                    $"Source file changed while its content proof was being captured: {entry.RelativePath}");
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
