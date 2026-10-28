namespace Listenarr.Infrastructure.FileSystem;

/// <summary>
/// Wraps a verified target registration lease with the exact live source entry
/// captured by the same operation. The source entry is intentionally
/// process-local and is never serialized into the mutation journal.
/// </summary>
internal sealed class MarkerlessRegistrationPublicationLease :
    IAudiobookFileRegistrationLease,
    IAudiobookFileRegistrationPublicationProbe
{
    private readonly IAudiobookFileRegistrationLease _targetLease;
    private PinnedDirectoryCreation.PinnedFileEntry? _sourceEntry;
    private bool _disposed;

    internal MarkerlessRegistrationPublicationLease(
        IAudiobookFileRegistrationLease targetLease,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry)
    {
        ArgumentNullException.ThrowIfNull(targetLease);
        ArgumentNullException.ThrowIfNull(sourceEntry);
        _targetLease = targetLease;
        _sourceEntry = sourceEntry;
    }

    internal PinnedDirectoryCreation.PinnedFileEntry SourceEntry =>
        _sourceEntry
        ?? throw new InvalidOperationException(
            "The live source-retirement authority has already been released.");

    internal void ReleaseSourceAuthority()
    {
        var sourceEntry = Interlocked.Exchange(ref _sourceEntry, null);
        sourceEntry?.Dispose();
    }

    public string PublicPath => _targetLease.PublicPath;
    public string MetadataPath => _targetLease.MetadataPath;
    public string PhysicalObjectIdentity => _targetLease.PhysicalObjectIdentity;
    public bool HasDurablePhysicalObjectIdentity =>
        _targetLease.HasDurablePhysicalObjectIdentity;
    public bool SupportsMetadataWrite => _targetLease.SupportsMetadataWrite;

    // Persisted source identity is deliberately absent. Source retirement is
    // authorized only by SourceEntry while this live lease remains undisposed.
    public string? SourcePhysicalObjectIdentity => null;

    public Stream OpenMetadataReadStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targetLease.OpenMetadataReadStream();
    }

    public Stream OpenMetadataWriteStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targetLease.OpenMetadataWriteStream();
    }

    public bool MatchesCurrentPublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targetLease.MatchesCurrentPublication();
    }

    public RegistrationPublicationMatchOutcome ProbeCurrentPublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targetLease is IAudiobookFileRegistrationPublicationProbe probe
            ? probe.ProbeCurrentPublication()
            : _targetLease.MatchesCurrentPublication()
                ? RegistrationPublicationMatchOutcome.Match
                : RegistrationPublicationMatchOutcome.Mismatch;
    }

    public bool PrepareCleanupRecovery(int audiobookId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targetLease.PrepareCleanupRecovery(audiobookId);
    }

    public RegistrationPublicationCompletion CompletePublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targetLease.CompletePublication();
    }

    public Task<bool> MatchesContentAsync(
        Stream candidateStream,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targetLease.MatchesContentAsync(
            candidateStream,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseSourceAuthority();
        _targetLease.Dispose();
        _disposed = true;
    }
}
