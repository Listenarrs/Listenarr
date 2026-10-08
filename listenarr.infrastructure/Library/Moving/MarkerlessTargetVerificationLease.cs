using System.Security.Cryptography;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class MarkerlessTargetVerificationLease : IDisposable
{
    private readonly Dictionary<string, TargetFileLease> _entries;
    private readonly FileSystemPathSemantics _semantics;
    private PinnedDirectoryCreation.PinnedDirectoryAnchor? _targetRoot;
    private bool _disposed;

    public MarkerlessTargetVerificationLease(FileSystemPathSemantics semantics)
    {
        _entries = new Dictionary<string, TargetFileLease>(semantics.Comparer);
        _semantics = semantics;
    }

    public bool IsEmpty => _targetRoot == null && _entries.Count == 0;

    public void SetTargetRoot(
        PinnedDirectoryCreation.PinnedDirectoryAnchor targetRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(targetRoot);
        if (_targetRoot != null)
        {
            targetRoot.Dispose();
            return;
        }

        _targetRoot = targetRoot;
    }

    public void Add(
        string relativePath,
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        AddCore(
            relativePath,
            entry,
            expectedLength: null,
            expectedSha256: null);
    }

    public void Add(
        string relativePath,
        PinnedDirectoryCreation.PinnedFileEntry entry,
        long expectedLength,
        string expectedSha256)
    {
        ValidateContentEvidence(expectedLength, expectedSha256);
        AddCore(relativePath, entry, expectedLength, expectedSha256);
    }

    public void SetContentEvidence(
        string relativePath,
        long expectedLength,
        string expectedSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ValidateContentEvidence(expectedLength, expectedSha256);
        if (!_entries.TryGetValue(relativePath, out var leased))
        {
            throw new InvalidOperationException(
                $"No target verification lease exists for '{relativePath}'.");
        }

        leased.ExpectedLength = expectedLength;
        leased.ExpectedSha256 = expectedSha256.ToUpperInvariant();
    }

    private void AddCore(
        string relativePath,
        PinnedDirectoryCreation.PinnedFileEntry entry,
        long? expectedLength,
        string? expectedSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(entry);
        // Consume and close the publication handle now, not at the end of the batch.
        using (entry)
        {
            if (!_entries.TryAdd(relativePath, new TargetFileLease(
                    FilePublicationObservation.Capture(entry), expectedLength,
                    expectedSha256?.ToUpperInvariant())))
            {
                throw new InvalidOperationException(
                    $"A target verification lease already exists for '{relativePath}'.");
            }
        }
    }

    public bool Contains(string relativePath) => _entries.ContainsKey(relativePath);

    public bool Matches(string relativePath, PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _entries.TryGetValue(relativePath, out var observed)
            && observed.Observation.Matches(entry);
    }

    private PinnedDirectoryCreation.PinnedFileEntry OpenCurrent(string relativePath)
    {
        var root = _targetRoot ?? throw new IOException("The target root is unavailable.");
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                root.FullPath, Path.Join(root.FullPath, relativePath), _semantics, out var relative))
        {
            throw new IOException("The verification entry is outside the target root.");
        }
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root.Duplicate();
        try
        {
            foreach (var segment in segments[..^1])
            {
                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }
            if (!root.VisiblePathMatches() || !current.VisiblePathMatches())
                throw new IOException("The verification hierarchy changed.");
            return current.OpenExistingFile(segments[^1], requireDeleteAccess: false);
        }
        finally { current.Dispose(); }
    }

    public async Task<RegistrationPublicationMatchOutcome>
        ProbeCurrentPublicationsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sawUnavailable = false;
        if (_targetRoot != null)
        {
            var rootMatch = _targetRoot.ProbeVisiblePathMatch();
            if (rootMatch == RegistrationPublicationMatchOutcome.Mismatch)
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }
            if (rootMatch == RegistrationPublicationMatchOutcome.Unavailable)
            {
                sawUnavailable = true;
            }
        }

        foreach (var pair in _entries)
        {
            var leased = pair.Value;
            cancellationToken.ThrowIfCancellationRequested();
            if (!leased.ExpectedLength.HasValue
                || string.IsNullOrWhiteSpace(leased.ExpectedSha256))
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }

            try
            {
                using var entry = OpenCurrent(pair.Key);
                if (!leased.Observation.Matches(entry))
                    return RegistrationPublicationMatchOutcome.Mismatch;
                await using var stream = entry.OpenReadStream(
                    bufferSize: 128 * 1024,
                    asynchronous: false);
                if (stream.Length != leased.ExpectedLength.Value)
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }

                var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(
                        hash,
                        leased.ExpectedSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
                if (entry.ProbePublicPathMatch()
                    != RegistrationPublicationMatchOutcome.Match)
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
            }
            catch (FileNotFoundException) { return RegistrationPublicationMatchOutcome.Mismatch; }
            catch (DirectoryNotFoundException) { return RegistrationPublicationMatchOutcome.Mismatch; }
            catch (System.ComponentModel.Win32Exception exception) when (
                OperatingSystem.IsWindows() ? exception.NativeErrorCode is 2 or 3 : exception.NativeErrorCode == 2)
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception)
            {
                sawUnavailable = true;
            }
        }

        return sawUnavailable
            ? RegistrationPublicationMatchOutcome.Unavailable
            : RegistrationPublicationMatchOutcome.Match;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _targetRoot?.Dispose();
        _targetRoot = null;
        _entries.Clear();
    }

    private static void ValidateContentEvidence(
        long expectedLength,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "A target verification lease requires a valid SHA-256 digest.",
                nameof(expectedSha256));
        }
    }

    private sealed class TargetFileLease
    {
        public TargetFileLease(
            FilePublicationObservation observation,
            long? expectedLength,
            string? expectedSha256)
        {
            Observation = observation;
            ExpectedLength = expectedLength;
            ExpectedSha256 = expectedSha256;
        }

        public FilePublicationObservation Observation { get; }
        public long? ExpectedLength { get; set; }
        public string? ExpectedSha256 { get; set; }
    }
}
