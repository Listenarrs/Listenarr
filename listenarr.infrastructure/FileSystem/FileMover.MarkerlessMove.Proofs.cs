using System.Security.Cryptography;

using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static async Task<MarkerlessSourceProof>
        CaptureMarkerlessSourceProofAsync(
            PinnedDirectoryCreation.PinnedFileEntry source,
            CancellationToken cancellationToken,
            bool includeSha256 = true)
    {
        var physicalObjectIdentity = source.GetObjectIdentity();
        await using var stream = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        var length = stream.Length;
        if (!includeSha256)
        {
            return new MarkerlessSourceProof(
                physicalObjectIdentity,
                length,
                Sha256: null);
        }

        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new MarkerlessSourceProof(
            physicalObjectIdentity,
            length,
            Convert.ToHexString(hash));
    }

    private async Task<FileMutationJournal> EnsureMarkerlessSourceHashAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(journal.SourceSha256))
        {
            return journal;
        }
        if (!source.VisiblePathMatches()
            || !string.Equals(
                source.GetObjectIdentity(),
                journal.SourcePhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The markerless move source changed before content hashing.");
        }

        await using var stream = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        if (stream.Length != journal.SourceLength)
        {
            throw new IOException(
                "The markerless move source length changed before content hashing.");
        }

        stream.Position = 0;
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return await _fileMutationJournalStore!.SetSourceSha256Async(
            journal.OperationId,
            journal.SourcePhysicalObjectIdentity,
            journal.SourceLength,
            hash,
            cancellationToken);
    }

    private static async Task<bool> MatchesMarkerlessSourceProofAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (!source.VisiblePathMatches()
            || (journal.Action == FileAction.HardlinkCopy
                ? !MatchesHardlinkSourceIdentity(
                    source,
                    journal.SourcePhysicalObjectIdentity)
                : !string.Equals(
                    source.GetObjectIdentity(),
                    journal.SourcePhysicalObjectIdentity,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        return await MatchesMarkerlessContentAsync(
            source,
            journal.SourceLength,
            journal.SourceSha256,
            cancellationToken);
    }

    private static async Task<bool> MatchesMarkerlessTargetContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry target,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(journal.SourceSha256)
            && (journal.Action == FileAction.HardlinkCopy
                ? !MatchesHardlinkSourceIdentity(
                    target,
                    journal.SourcePhysicalObjectIdentity)
                : !string.Equals(
                    target.GetObjectIdentity(),
                    journal.SourcePhysicalObjectIdentity,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        return await MatchesMarkerlessContentAsync(
            target,
            journal.SourceLength,
            journal.SourceSha256,
            cancellationToken);
    }

    private static async Task<bool> MatchesMarkerlessContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry file,
        long expectedLength,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            return await file.MatchesAsync(
                expectedLength,
                expectedSha256,
                cancellationToken);
        }

        await using var stream = file.OpenReadStream(
            bufferSize: 1,
            asynchronous: false);
        return stream.Length == expectedLength;
    }

    private static bool TargetMatchesMarkerlessJournal(
        PinnedDirectoryCreation.PinnedFileEntry target,
        FileMutationJournal journal) =>
        target.VisiblePathMatches()
        && !string.IsNullOrWhiteSpace(
            journal.TargetPhysicalObjectIdentity)
        && string.Equals(
            target.GetObjectIdentity(),
            journal.TargetPhysicalObjectIdentity,
            StringComparison.Ordinal);

    private static bool OwnerMetadataReconciledTargetMatches(
        FileMoveGateLease gate,
        FileMutationJournal journal)
    {
        if (journal.State != FileMutationJournalState.OwnerMetadataReconciled
            || !gate.DestinationParent.VisiblePathMatches())
        {
            return false;
        }

        using var target = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        return target != null && TargetMatchesMarkerlessJournal(target, journal);
    }

    private static async Task CopyMarkerlessFileAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        PinnedDirectoryCreation.PinnedFileEntry target,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        await using var targetStream = target.OpenWriteStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        targetStream.SetLength(0);
        await sourceStream.CopyToAsync(
            targetStream,
            128 * 1024,
            cancellationToken);
        await targetStream.FlushAsync(cancellationToken);
        targetStream.Flush(flushToDisk: true);
    }

    private sealed record MarkerlessSourceProof(
        string PhysicalObjectIdentity,
        long Length,
        string? Sha256);
}
