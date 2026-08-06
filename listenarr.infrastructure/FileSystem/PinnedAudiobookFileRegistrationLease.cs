using System.Buffers;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class PinnedAudiobookFileRegistrationLease :
    IAudiobookFileRegistrationLease
{
    private readonly PinnedDirectoryCreation.PinnedFileEntry _file;
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle? _stableHandle;
    private readonly Func<int, bool>? _prepareCleanupRecovery;
    private readonly Func<int, bool>? _commitRegistration;
    private readonly Func<bool>? _completePublication;
    private int? _cleanupRecoveryAudiobookId;
    private bool _cleanupRecoveryPrepared;
    private bool _registrationCommitted;
    private bool _publicationCompleted;
    private bool _disposed;

    private PinnedAudiobookFileRegistrationLease(
        PinnedDirectoryCreation.PinnedFileEntry file,
        Microsoft.Win32.SafeHandles.SafeFileHandle? stableHandle,
        string publicPath,
        string metadataPath,
        string physicalObjectIdentity,
        string? sourcePhysicalObjectIdentity,
        Func<int, bool>? prepareCleanupRecovery,
        Func<bool>? completePublication,
        Func<int, bool>? commitRegistration)
    {
        _file = file;
        _stableHandle = stableHandle;
        _prepareCleanupRecovery = prepareCleanupRecovery;
        _commitRegistration = commitRegistration;
        _completePublication = completePublication;
        PublicPath = publicPath;
        MetadataPath = metadataPath;
        PhysicalObjectIdentity = physicalObjectIdentity;
        SourcePhysicalObjectIdentity = sourcePhysicalObjectIdentity;
    }

    public string PublicPath { get; }
    public string MetadataPath { get; }
    public string PhysicalObjectIdentity { get; }
    public string? SourcePhysicalObjectIdentity { get; }

    public Stream OpenMetadataReadStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _file.OpenIndependentReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
    }

    public Stream OpenMetadataWriteStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _file.OpenIndependentWriteStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
    }

    internal static PinnedAudiobookFileRegistrationLease Open(
        string publicPath,
        string? expectedPhysicalObjectIdentity = null,
        string? sourcePhysicalObjectIdentity = null,
        Func<int, bool>? prepareCleanupRecovery = null,
        Func<bool>? completePublication = null,
        Func<int, bool>? commitRegistration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        var canonicalPath = Path.GetFullPath(publicPath);
        var parentPath = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException(
                "The audiobook file path has no parent directory.");
        using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            parentPath,
            createMissing: false);
        var file = parent.OpenExistingFileForStableRead(
            Path.GetFileName(canonicalPath));
        return Create(
            file,
            canonicalPath,
            expectedPhysicalObjectIdentity,
            sourcePhysicalObjectIdentity,
            prepareCleanupRecovery,
            completePublication,
            commitRegistration);
    }

    internal static PinnedAudiobookFileRegistrationLease Create(
        PinnedDirectoryCreation.PinnedFileEntry file,
        string publicPath,
        string? expectedPhysicalObjectIdentity = null,
        string? sourcePhysicalObjectIdentity = null,
        Func<int, bool>? prepareCleanupRecovery = null,
        Func<bool>? completePublication = null,
        Func<int, bool>? commitRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        Microsoft.Win32.SafeHandles.SafeFileHandle? stableHandle = null;
        try
        {
            var canonicalPath = Path.GetFullPath(publicPath);
            var physicalObjectIdentity = file.GetObjectIdentity();
            if (!file.VisiblePathMatches()
                || (!string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity)
                    && !string.Equals(
                        physicalObjectIdentity,
                        expectedPhysicalObjectIdentity,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The audiobook file generation does not match the expected physical identity.");
            }

            if (OperatingSystem.IsWindows())
            {
                return new PinnedAudiobookFileRegistrationLease(
                    file,
                    null,
                    canonicalPath,
                    canonicalPath,
                    physicalObjectIdentity,
                    sourcePhysicalObjectIdentity,
                    prepareCleanupRecovery,
                    completePublication,
                    commitRegistration);
            }

            if (OperatingSystem.IsLinux())
            {
                stableHandle = file.DuplicateHandleForOperation();
                var metadataPath = FormattableString.Invariant(
                    $"/proc/{Environment.ProcessId}/fd/{stableHandle.DangerousGetHandle().ToInt32()}");
                if (!File.Exists(metadataPath))
                {
                    throw new PlatformNotSupportedException(
                        "The Linux proc filesystem is unavailable for stable metadata extraction.");
                }

                var result = new PinnedAudiobookFileRegistrationLease(
                    file,
                    stableHandle,
                    canonicalPath,
                    metadataPath,
                    physicalObjectIdentity,
                    sourcePhysicalObjectIdentity,
                    prepareCleanupRecovery,
                    completePublication,
                    commitRegistration);
                stableHandle = null;
                return result;
            }

            throw new PlatformNotSupportedException(
                "Stable metadata extraction is supported only on Windows and Linux.");
        }
        catch
        {
            stableHandle?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public async Task<bool> MatchesContentAsync(
        Stream candidateStream,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(candidateStream);

        await using var publishedStream = _file.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        if (candidateStream.CanSeek)
        {
            if (candidateStream.Length != publishedStream.Length)
            {
                return false;
            }

            candidateStream.Position = 0;
        }
        publishedStream.Position = 0;

        var candidateBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var publishedBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                var candidateRead = await candidateStream.ReadAsync(
                    candidateBuffer.AsMemory(0, candidateBuffer.Length),
                    cancellationToken);
                var publishedRead = await publishedStream.ReadAsync(
                    publishedBuffer.AsMemory(0, publishedBuffer.Length),
                    cancellationToken);
                if (candidateRead != publishedRead)
                {
                    return false;
                }

                if (candidateRead == 0)
                {
                    return true;
                }

                if (!candidateBuffer.AsSpan(0, candidateRead).SequenceEqual(
                        publishedBuffer.AsSpan(0, publishedRead)))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(candidateBuffer);
            ArrayPool<byte>.Shared.Return(publishedBuffer);
        }
    }

    public bool MatchesCurrentPublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return _file.VisiblePathMatches()
                && string.Equals(
                    _file.GetObjectIdentity(),
                    PhysicalObjectIdentity,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public bool PrepareCleanupRecovery(int audiobookId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        if (_cleanupRecoveryAudiobookId.HasValue
            && _cleanupRecoveryAudiobookId.Value != audiobookId)
        {
            throw new InvalidOperationException(
                "The registration lease is already bound to another audiobook.");
        }

        _cleanupRecoveryAudiobookId = audiobookId;
        if (_cleanupRecoveryPrepared || _prepareCleanupRecovery == null)
        {
            _cleanupRecoveryPrepared = true;
            return true;
        }

        _cleanupRecoveryPrepared = _prepareCleanupRecovery(audiobookId);
        return _cleanupRecoveryPrepared;
    }

    public RegistrationPublicationCompletion CompletePublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_publicationCompleted)
        {
            return RegistrationPublicationCompletion.Completed;
        }
        if ((_prepareCleanupRecovery != null || _commitRegistration != null)
            && !_cleanupRecoveryPrepared)
        {
            throw new InvalidOperationException(
                "Durable cleanup recovery must be prepared before publication is completed.");
        }

        if (!_registrationCommitted && _commitRegistration != null)
        {
            var audiobookId = _cleanupRecoveryAudiobookId
                ?? throw new InvalidOperationException(
                    "The registration lease has no durable audiobook owner.");
            if (!_commitRegistration(audiobookId))
            {
                return RegistrationPublicationCompletion.CommittedCleanupPending;
            }

            _registrationCommitted = true;
        }

        if (_completePublication != null && !_completePublication())
        {
            return RegistrationPublicationCompletion.CommittedCleanupPending;
        }

        _publicationCompleted = true;
        return RegistrationPublicationCompletion.Completed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stableHandle?.Dispose();
        _file.Dispose();
        _disposed = true;
    }
}
