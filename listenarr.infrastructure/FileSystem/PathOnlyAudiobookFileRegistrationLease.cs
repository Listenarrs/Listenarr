using System.Buffers;
using System.Security.Cryptography;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class PathOnlyAudiobookFileRegistrationLease :
    IAudiobookFileRegistrationLease,
    IAudiobookFileRegistrationIdentityVerifier,
    IAudiobookFileRegistrationPublicationProbe
{
    private readonly FileStream _pinnedRead;
    private readonly long _expectedLength;
    private readonly string _expectedSha256;
    private readonly Func<int, bool>? _commitRegistration;
    private readonly Func<bool>? _completePublication;
    private int? _audiobookId;
    private bool _cleanupRecoveryPrepared;
    private bool _registrationCommitted;
    private bool _publicationCompleted;
    private bool _disposed;

    private PathOnlyAudiobookFileRegistrationLease(
        FileStream pinnedRead,
        string publicPath,
        long expectedLength,
        string expectedSha256,
        Func<int, bool>? commitRegistration,
        Func<bool>? completePublication)
    {
        _pinnedRead = pinnedRead;
        _expectedLength = expectedLength;
        _expectedSha256 = expectedSha256;
        _commitRegistration = commitRegistration;
        _completePublication = completePublication;
        PublicPath = publicPath;
        MetadataPath = OperatingSystem.IsLinux()
            ? FormattableString.Invariant(
                $"/proc/{Environment.ProcessId}/fd/{pinnedRead.SafeFileHandle.DangerousGetHandle().ToInt32()}")
            : publicPath;
        PhysicalObjectIdentity = $"content-pinned:{expectedSha256}";
    }

    public string PublicPath { get; }
    public string MetadataPath { get; }
    public string PhysicalObjectIdentity { get; }
    public bool HasDurablePhysicalObjectIdentity => false;
    public string? SourcePhysicalObjectIdentity => null;

    internal static PathOnlyAudiobookFileRegistrationLease Open(
        string publicPath,
        long expectedLength,
        string expectedSha256,
        Func<int, bool>? commitRegistration = null,
        Func<bool>? completePublication = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        var canonicalPath = Path.GetFullPath(publicPath);
        var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        try
        {
            if (stream.Length != expectedLength
                || !HashMatches(stream, expectedSha256))
            {
                throw new InvalidOperationException(
                    "The path-only publication changed before registration.");
            }
            if (OperatingSystem.IsLinux()
                && !File.Exists(FormattableString.Invariant(
                    $"/proc/{Environment.ProcessId}/fd/{stream.SafeFileHandle.DangerousGetHandle().ToInt32()}")))
            {
                throw new PlatformNotSupportedException(
                    "The Linux proc filesystem is unavailable for path-only metadata extraction.");
            }

            return new PathOnlyAudiobookFileRegistrationLease(
                stream,
                canonicalPath,
                expectedLength,
                expectedSha256,
                commitRegistration,
                completePublication);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Stream OpenMetadataReadStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new FileStream(
            MetadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
    }

    public Stream OpenMetadataWriteStream() => throw new NotSupportedException(
        "Path-only registration leases do not authorize metadata writes.");

    public async Task<bool> MatchesContentAsync(
        Stream candidateStream,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(candidateStream);
        await using var publishedStream = OpenMetadataReadStream();
        if (candidateStream.CanSeek)
        {
            if (candidateStream.Length != publishedStream.Length)
            {
                return false;
            }
            candidateStream.Position = 0;
        }

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

    public bool MatchesPhysicalObjectIdentity(string expectedPhysicalObjectIdentity) =>
        false;

    public bool MatchesCurrentPublication() =>
        ProbeCurrentPublication() == RegistrationPublicationMatchOutcome.Match;

    public RegistrationPublicationMatchOutcome ProbeCurrentPublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using var visible = new FileStream(
                PublicPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            return visible.Length == _expectedLength
                && HashMatches(visible, _expectedSha256)
                    ? RegistrationPublicationMatchOutcome.Match
                    : RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return RegistrationPublicationMatchOutcome.Unavailable;
        }
    }

    public bool PrepareCleanupRecovery(int audiobookId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        if (_audiobookId.HasValue && _audiobookId.Value != audiobookId)
        {
            throw new InvalidOperationException(
                "The registration lease is already bound to another audiobook.");
        }

        _audiobookId = audiobookId;
        _cleanupRecoveryPrepared = true;
        return true;
    }

    public RegistrationPublicationCompletion CompletePublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_publicationCompleted)
        {
            return RegistrationPublicationCompletion.Completed;
        }
        if (_commitRegistration != null && !_cleanupRecoveryPrepared)
        {
            throw new InvalidOperationException(
                "Compatibility recovery must be prepared before publication is completed.");
        }
        if (!_registrationCommitted && _commitRegistration != null)
        {
            if (!_commitRegistration(_audiobookId
                    ?? throw new InvalidOperationException(
                        "The registration lease has no audiobook owner.")))
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
        _pinnedRead.Dispose();
        _disposed = true;
    }

    private static bool HashMatches(Stream stream, string expectedSha256)
    {
        stream.Position = 0;
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return string.Equals(actual, expectedSha256, StringComparison.Ordinal);
    }
}
