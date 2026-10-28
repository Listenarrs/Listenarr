using System.Buffers;
using System.Security.Cryptography;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static AudiobookContentMoveRequest RetainMarkerlessSource(
        AudiobookContentMoveRequest request) => request with
        {
            ForceCopyAndRetainSource = true,
            SourceCleanupMode = MoveSourceCleanupMode.RetainSource,
            DeleteEmptySource = false
        };

    private async Task<AudiobookContentMoveRequest> ApplyDiagnosticRetentionAsync(
        AudiobookContentMoveRequest request,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        var endpoints = await GetEndpointObjectIdentitiesAsync(request.JobId, cancellationToken);
        // The stores latch retention before publication; this invocation must
        // honor the same decision even when it started with older diagnostics.
        return endpoints.SourceDirectoryObjectIdentity == string.Empty
            || endpoints.TargetDirectoryObjectIdentity == string.Empty
            || manifest.Where(IsPhysicalManifestEntry).Any(entry =>
                entry.SourcePhysicalObjectIdentity == string.Empty)
            ? RetainMarkerlessSource(request) : request;
    }
    private async Task CaptureMarkerlessSourceIdentitiesAsync(
        AudiobookContentMoveRequest request,
        string source,
        IReadOnlyCollection<MoveJobEntry> manifest,
        MarkerlessSourceRetirementLease sourceRetirementLease,
        CancellationToken cancellationToken)
    {
        await CaptureSourceAncestorProofsAsync(request, source, sourceRetirementLease, cancellationToken);
        using (var root = OpenPinnedMoveBoundaryDescendant(
            request,
            source,
            request.SourceSemantics,
            sourceBoundary: true))
        {
            sourceRetirementLease.AddDirectory(source, root);
            var rootIdentity = PinnedDirectoryCreation.CaptureDiagnosticIdentity(root.GetDirectoryObjectIdentity);
            if (!PinnedDirectoryVisibleOrThrowUnavailable(
                    root,
                    "The markerless move source root is temporarily unavailable while pinned."))
            {
                throw new MoveNeedsAttentionException(
                    "The markerless move source root changed while it was pinned.");
            }

            await UpdateEndpointObjectIdentitiesAsync(
                request.JobId,
                request.LeaseToken,
                rootIdentity,
                targetDirectoryObjectIdentity: null,
                cancellationToken);
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
                && IsVerifiedPublishedFileEntry(entry))
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
                sourceEndpoint: true);
            string identity;
            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                using var directory = parent.OpenExistingChild(
                    Path.GetFileName(fullPath));
                sourceRetirementLease.AddDirectory(fullPath, directory);
                identity = PinnedDirectoryCreation.CaptureDiagnosticIdentity(directory.GetDirectoryObjectIdentity);
                if (!PinnedDirectoryVisibleOrThrowUnavailable(
                        directory,
                        $"Source directory is temporarily unavailable while pinned: {entry.RelativePath}"))
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
                identity = PinnedDirectoryCreation.CaptureDiagnosticIdentity(file.GetObjectIdentity);
            }

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
