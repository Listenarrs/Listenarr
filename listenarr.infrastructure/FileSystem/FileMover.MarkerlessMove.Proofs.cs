using System.Security.Cryptography;

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
            new FileContentProof(length, sha256));
    }

    private static async Task<MarkerlessSourceProof>
        CaptureMarkerlessSourceProofAsync(
            PinnedDirectoryCreation.PinnedFileEntry source,
            CancellationToken cancellationToken,
            bool includeSha256 = true)
    {
        // v3 persists content proof only. Kernel object identity is deliberately
        // not captured here because it is not restart-stable authority.
        await using var stream = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        var length = stream.Length;
        if (!includeSha256)
        {
            return new MarkerlessSourceProof(
                PhysicalObjectIdentity: null,
                length,
                Sha256: null);
        }

        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new MarkerlessSourceProof(
            PhysicalObjectIdentity: null,
            length,
            Convert.ToHexString(hash));
    }

    private static Task<FileMutationJournal> EnsureMarkerlessSourceHashAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = source;
        if (!string.IsNullOrWhiteSpace(journal.SourceSha256))
        {
            return Task.FromResult(journal);
        }

        throw new InvalidOperationException(
            "The v3 file-mutation journal has no source content proof. Persisted physical identity cannot recreate restart authority.");
    }

    private static bool MatchesExpectedSourceProof(
        MarkerlessSourceProof actual,
        FilePublicationSourceProof expected) =>
        actual.Length == expected.Length
        && string.Equals(
            actual.Sha256,
            expected.Sha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool JournalMatchesExpectedSourceProof(
        FileMutationJournal journal,
        FilePublicationSourceProof expected) =>
        journal.SourceLength == expected.Length
        && string.Equals(
            journal.SourceSha256,
            expected.Sha256,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> MatchesMarkerlessSourceProofAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (!VisiblePathMatchesOrThrowUnavailable(
                source,
                "The markerless source is temporarily unavailable while its content proof is being verified.")
            || string.IsNullOrWhiteSpace(journal.SourceSha256))
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
        if (string.IsNullOrWhiteSpace(journal.SourceSha256))
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
        FileMutationJournal journal,
        bool requirePhysicalIdentity = true) =>
        VisiblePathMatchesOrThrowUnavailable(
            target,
            "The markerless target is temporarily unavailable while its current publication is being verified.")
        && (!requirePhysicalIdentity
            || (!string.IsNullOrWhiteSpace(
                    journal.TargetPhysicalObjectIdentity)
                && target.MatchesObjectIdentity(
                    journal.TargetPhysicalObjectIdentity)));

    private static async Task<bool> OwnerMetadataReconciledTargetMatchesAsync(
        FileMoveGateLease gate,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.State != FileMutationJournalState.OwnerMetadataReconciled
            || !gate.DestinationParent.VisiblePathMatches())
        {
            return false;
        }

        using var target = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        return target != null
            && TargetMatchesMarkerlessJournal(target, journal, requirePhysicalIdentity: false)
            && await MatchesMarkerlessTargetContentAsync(target, journal, cancellationToken)
            && target.VisiblePathMatches()
            && gate.DestinationParent.VisiblePathMatches();
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

    private sealed class MarkerlessCreatedTargetLease : IDisposable
    {
        public PinnedDirectoryCreation.PinnedFileEntry? Entry { get; set; }
        public void Dispose() => Entry?.Dispose();
    }

    private sealed record MarkerlessSourceProof(
        string? PhysicalObjectIdentity,
        long Length,
        string? Sha256);
}
