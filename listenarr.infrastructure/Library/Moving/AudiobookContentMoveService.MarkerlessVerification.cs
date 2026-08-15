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
        ValidateExistingDestinationContents(
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
            using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
            using var file = parent.OpenExistingFile(
                Path.GetFileName(targetPath),
                requireDeleteAccess: false);
            ValidateMarkerlessTargetEntry(entry, file);
            PinnedDirectoryCreation.PinnedFileEntry? leasedTargetEntry = null;
            var hasProtectedContentProof = targetVerificationLease != null
                && targetVerificationLease.TryGet(
                    entry.RelativePath,
                    out leasedTargetEntry);
            if (hasProtectedContentProof)
            {
                if (leasedTargetEntry == null
                    || !leasedTargetEntry.VisiblePathMatches()
                    || !leasedTargetEntry.IdentifiesSameEntry(file)
                    || !string.Equals(
                        leasedTargetEntry.GetObjectIdentity(),
                        entry.TargetPhysicalObjectIdentity,
                        StringComparison.Ordinal)
                    || !leasedTargetEntry.MatchesMetadata(
                        entry.Length,
                        entry.LastWriteTimeUtc))
                {
                    throw new MoveNeedsAttentionException(
                        $"A protected markerless target generation changed after native publication: {entry.RelativePath}");
                }
            }
            else if (IsVerifiedMarkerlessNativeRenameEntry(entry))
            {
                if (!file.MatchesMetadata(entry.Length, entry.LastWriteTimeUtc))
                {
                    throw new MoveNeedsAttentionException(
                        $"A markerless native-rename target changed metadata after publication: {entry.RelativePath}");
                }
            }
            else
            {
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
                        $"A markerless target file failed final verification: {entry.RelativePath}");
                }
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
            || !string.Equals(
                entry.SourcePhysicalObjectIdentity,
                sourceEntry.GetObjectIdentity(),
                StringComparison.Ordinal)
            || !sourceEntry.VisiblePathMatches())
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
            || !string.Equals(
                entry.TargetPhysicalObjectIdentity,
                targetEntry.GetObjectIdentity(),
                StringComparison.Ordinal)
            || !targetEntry.VisiblePathMatches())
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
