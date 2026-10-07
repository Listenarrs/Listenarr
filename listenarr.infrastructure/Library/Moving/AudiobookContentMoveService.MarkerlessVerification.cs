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
        if (targetVerificationLease != null)
        {
            targetVerificationLease.SetTargetRoot(
                OpenPinnedMoveDescendant(
                    request,
                    target,
                    target,
                    request.TargetSemantics,
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

            if (entry.CopyState != MoveJobEntryCopyState.Verified)
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
                sourceEndpoint: false);
            var openOutcome = parent.TryOpenExistingFileWithOutcome(
                Path.GetFileName(targetPath),
                requireDeleteAccess: false,
                out var openedFile);
            using var file = openedFile;
            if (openOutcome == PinnedFileOpenOutcome.NotFound)
            {
                throw new MoveNeedsAttentionException(
                    $"A verified markerless target file is missing: {entry.RelativePath}");
            }
            if (openOutcome != PinnedFileOpenOutcome.Opened || file == null)
            {
                throw new IOException(
                    $"A verified markerless target file is temporarily unavailable: {entry.RelativePath}");
            }
            ValidateMarkerlessTargetEntry(entry, file);
            PinnedDirectoryCreation.PinnedFileEntry? leasedTargetEntry = null;
            var hasProtectedContentProof = targetVerificationLease != null
                && targetVerificationLease.TryGet(
                    entry.RelativePath,
                    out leasedTargetEntry);
            if (string.IsNullOrWhiteSpace(entry.Sha256))
            {
                throw new MoveNeedsAttentionException(
                    $"A verified markerless target lacks durable content proof: {entry.RelativePath}");
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
                    || !leasedTargetEntry.IdentifiesSameEntry(file))
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
                    $"A markerless target file parent is temporarily unavailable after verification: {entry.RelativePath}"))
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
        // Persisted physical identity is diagnostic only. The live pinned entry,
        // visible-path check, and manifest content proof are the operation authority.
        _ = request;
        if (!sourceEntry.IsRegularFile()
            || !PinnedFileVisibleOrThrowUnavailable(
                sourceEntry,
                $"A markerless source file is temporarily unavailable: {entry.RelativePath}"))
        {
            throw new MoveNeedsAttentionException(
                $"A markerless source file changed or is no longer a regular file: {entry.RelativePath}");
        }
    }

    private static void ValidateMarkerlessTargetEntry(
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry)
    {
        if (!targetEntry.IsRegularFile()
            || !PinnedFileVisibleOrThrowUnavailable(
                targetEntry,
                $"A markerless target file is temporarily unavailable: {entry.RelativePath}"))
        {
            throw new MoveNeedsAttentionException(
                $"A markerless target file changed or is no longer a regular file: {entry.RelativePath}");
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
