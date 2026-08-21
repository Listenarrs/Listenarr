using System.Security.Cryptography;

using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static async Task<FilePublicationSourceProof>
        CaptureContentOnlySourceProofAsync(
            PinnedDirectoryCreation.PinnedFileEntry source,
            CancellationToken cancellationToken)
    {
        await using var stream = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        var length = stream.Length;
        stream.Position = 0;
        var sha256 = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return new FilePublicationSourceProof(
            $"content-only:{sha256}",
            length,
            sha256,
            FilePublicationSourceAuthority.ContentOnly);
    }

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
        if (!VisiblePathMatchesOrThrowUnavailable(
                source,
                "The markerless move source is temporarily unavailable before content hashing.")
            || !source.MatchesObjectIdentity(
                journal.SourcePhysicalObjectIdentity))
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

    private static bool MatchesExpectedSourceProof(
        MarkerlessSourceProof actual,
        FilePublicationSourceProof expected) =>
        expected.HasDurablePhysicalObjectIdentity
        &&
        PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
            actual.PhysicalObjectIdentity,
            expected.PhysicalObjectIdentity)
        && actual.Length == expected.Length
        && string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal);

    private static bool JournalMatchesExpectedSourceProof(
        FileMutationJournal journal,
        FilePublicationSourceProof expected) =>
        PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
            journal.SourcePhysicalObjectIdentity,
            expected.PhysicalObjectIdentity)
        && journal.SourceLength == expected.Length
        && string.Equals(
            journal.SourceSha256,
            expected.Sha256,
            StringComparison.Ordinal);

    private static async Task<bool> MatchesMarkerlessSourceProofAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (!VisiblePathMatchesOrThrowUnavailable(
                source,
                "The markerless source is temporarily unavailable while its physical generation is being verified.")
            || (journal.Action == FileAction.HardlinkCopy
                ? !MatchesHardlinkSourceIdentity(
                    source,
                    journal.SourcePhysicalObjectIdentity)
                : !source.MatchesObjectIdentity(
                    journal.SourcePhysicalObjectIdentity)))
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
                : !target.MatchesObjectIdentity(
                    journal.SourcePhysicalObjectIdentity)))
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
        VisiblePathMatchesOrThrowUnavailable(
            target,
            "The markerless target is temporarily unavailable while its physical generation is being verified.")
        && !string.IsNullOrWhiteSpace(
            journal.TargetPhysicalObjectIdentity)
        && target.MatchesObjectIdentity(
            journal.TargetPhysicalObjectIdentity);

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
