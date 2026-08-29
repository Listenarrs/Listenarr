using System.Buffers;
using System.Security.Cryptography;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task VerifyMarkerlessTargetAsync(
        AudiobookContentMoveRequest request,
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken,
        double? progressStart = null,
        double progressSpan = 0,
        string? progressPhase = null,
        MarkerlessTargetVerificationLease? targetVerificationLease = null)
    {
        await CaptureOrValidateMarkerlessTargetRootAsync(
            request,
            target,
            cancellationToken);
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity))
        {
            throw new MoveNeedsAttentionException(
                "Markerless target verification requires a persisted target endpoint generation.");
        }
        if (targetVerificationLease != null)
        {
            targetVerificationLease.SetTargetRoot(
                OpenPinnedMoveDescendant(
                    request,
                    target,
                    target,
                    request.TargetSemantics,
                    endpoints.TargetDirectoryObjectIdentity,
                    sourceEndpoint: false));
        }
        ValidateExistingDestinationContents(
            request,
            request.Source,
            target,
            manifest,
            request.TargetSemantics,
            request.TargetDirectoryOwnership);
        var files = manifest
            .Where(IsPhysicalManifestEntry)
            .Where(entry => entry.EntryType == MoveJobEntryType.File)
            .ToList();
        var totalUnits = files.Sum(GetProgressUnits);
        long completedUnits = 0;
        foreach (var entry in manifest.Where(IsPhysicalManifestEntry))
        {
            var targetPath = ResolveManifestPath(
                target,
                entry,
                request.TargetSemantics,
                "target");
            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                ValidateExistingMoveDirectory(
                    targetPath,
                    "markerless target manifest directory");
                continue;
            }

            if (entry.CopyState != MoveJobEntryCopyState.Verified
                || string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless target file is not durably verified: {entry.RelativePath}");
            }
            var parentPath = Path.GetDirectoryName(targetPath)
                ?? throw new MoveNeedsAttentionException(
                    "A markerless target file has no parent.");
            using var parent = OpenPinnedMoveDescendant(
                request,
                target,
                parentPath,
                request.TargetSemantics,
                endpoints.TargetDirectoryObjectIdentity,
                sourceEndpoint: false);
            using var file = parent.OpenExistingFile(
                Path.GetFileName(targetPath),
                requireDeleteAccess: false);
            ValidateMarkerlessTargetEntry(entry, file);
            PinnedDirectoryCreation.PinnedFileEntry? leasedTargetEntry = null;
            var hasProtectedContentProof = targetVerificationLease != null
                && targetVerificationLease.TryGet(
                    entry.RelativePath,
                    out leasedTargetEntry);
            if (string.IsNullOrWhiteSpace(entry.Sha256))
            {
                if (!IsVerifiedMarkerlessNativeRenameEntry(entry)
                    || string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity))
                {
                    throw new MoveNeedsAttentionException(
                        $"A verified markerless target lacks durable content proof: {entry.RelativePath}");
                }

                entry.Sha256 = await ComputePinnedFileSha256Async(
                    file,
                    cancellationToken);
                var observedLastWriteTimeUtc = file.GetLastWriteTimeUtc();
                await UpdateSourceEntryProofAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    entry.SourcePhysicalObjectIdentity,
                    entry.Sha256,
                    observedLastWriteTimeUtc,
                    cancellationToken);
                entry.LastWriteTimeUtc = observedLastWriteTimeUtc;
            }

            Func<long, Task>? reportFileProgress = null;
            if (progressStart.HasValue && progressSpan > 0)
            {
                reportFileProgress = bytesRead => ReportProgressAsync(
                    request,
                    CalculateWeightedProgress(
                        progressStart.Value,
                        progressSpan,
                        completedUnits + Math.Min(bytesRead, GetProgressUnits(entry)),
                        totalUnits),
                    progressPhase ?? "Verifying target",
                    cancellationToken);
            }
            if (!await PinnedFileMatchesManifestAsync(
                    file,
                    entry,
                    cancellationToken,
                    reportFileProgress))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless target file failed final content verification: {entry.RelativePath}");
            }

            if (hasProtectedContentProof)
            {
                if (leasedTargetEntry == null
                    || !PinnedFileVisibleOrThrowUnavailable(
                        leasedTargetEntry,
                        $"A protected markerless target generation is temporarily unavailable: {entry.RelativePath}")
                    || !leasedTargetEntry.IdentifiesSameEntry(file)
                    || !leasedTargetEntry.MatchesObjectIdentity(
                        entry.TargetPhysicalObjectIdentity))
                {
                    throw new MoveNeedsAttentionException(
                        $"A protected markerless target generation changed after native publication: {entry.RelativePath}");
                }

                targetVerificationLease!.SetContentEvidence(
                    entry.RelativePath,
                    entry.Length,
                    entry.Sha256);
            }

            if (!PinnedFileVisibleOrThrowUnavailable(
                    file,
                    $"A markerless target file is temporarily unavailable after verification: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    parent,
                    $"A markerless target file parent is temporarily unavailable after verification: {entry.RelativePath}")
                || !file.MatchesObjectIdentity(entry.TargetPhysicalObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless target file changed physical generation after verification: {entry.RelativePath}");
            }

            if (targetVerificationLease != null && !hasProtectedContentProof)
            {
                targetVerificationLease.Add(
                    entry.RelativePath,
                    file.OpenStableRegistrationCopy(),
                    entry.Length,
                    entry.Sha256!);
            }

            completedUnits += GetProgressUnits(entry);
            if (progressStart.HasValue && progressSpan > 0)
            {
                await ReportProgressAsync(
                    request,
                    CalculateWeightedProgress(
                        progressStart.Value,
                        progressSpan,
                        completedUnits,
                        totalUnits),
                    progressPhase ?? "Verifying target",
                    cancellationToken);
            }
        }
    }

    private static void ValidateMarkerlessSourceEntry(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry)
    {
        ValidatePinnedSourcePhysicalIdentity(request, entry, sourceEntry);
        if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
            || !sourceEntry.MatchesObjectIdentity(
                entry.SourcePhysicalObjectIdentity)
            || !PinnedFileVisibleOrThrowUnavailable(
                sourceEntry,
                $"A markerless source file is temporarily unavailable: {entry.RelativePath}"))
        {
            throw new MoveNeedsAttentionException(
                $"A markerless source file changed physical generation: {entry.RelativePath}");
        }
    }

    private static void ValidateMarkerlessTargetEntry(
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
            || !targetEntry.MatchesObjectIdentity(
                entry.TargetPhysicalObjectIdentity)
            || !PinnedFileVisibleOrThrowUnavailable(
                targetEntry,
                $"A markerless target file is temporarily unavailable: {entry.RelativePath}"))
        {
            throw new MoveNeedsAttentionException(
                $"A markerless target file changed physical generation: {entry.RelativePath}");
        }
    }

    private static bool PinnedFileLengthMatchesManifest(
        PinnedDirectoryCreation.PinnedFileEntry file,
        MoveJobEntry manifestEntry)
    {
        if (manifestEntry.EntryType != MoveJobEntryType.File)
        {
            return false;
        }

        using var stream = file.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        return stream.Length == manifestEntry.Length;
    }

    private static async Task<string> ComputePinnedFileSha256Async(
        PinnedDirectoryCreation.PinnedFileEntry file,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream(
            bufferSize: 1024 * 1024,
            asynchronous: false);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<bool> PinnedFileMatchesManifestAsync(
        PinnedDirectoryCreation.PinnedFileEntry file,
        MoveJobEntry manifestEntry,
        CancellationToken cancellationToken,
        Func<long, Task>? progressReporter = null)
    {
        if (manifestEntry.EntryType != MoveJobEntryType.File
            || string.IsNullOrWhiteSpace(manifestEntry.Sha256))
        {
            return false;
        }
        await using var stream = file.OpenReadStream(
            bufferSize: 1024 * 1024,
            asynchronous: false);
        if (stream.Length != manifestEntry.Length)
        {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long hashed = 0;
            long lastReported = 0;
            var reportInterval = Math.Max(
                16L * 1024 * 1024,
                Math.Max(manifestEntry.Length / 100, 1));
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
                if (progressReporter != null
                    && hashed - lastReported >= reportInterval)
                {
                    lastReported = hashed;
                    await progressReporter(hashed);
                }
            }

            if (hashed != manifestEntry.Length)
            {
                return false;
            }
            return string.Equals(
                Convert.ToHexString(hash.GetHashAndReset()),
                manifestEntry.Sha256,
                StringComparison.Ordinal);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
