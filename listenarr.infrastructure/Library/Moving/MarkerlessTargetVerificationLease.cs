using System.Security.Cryptography;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class MarkerlessTargetVerificationLease : IDisposable
{
    private readonly Dictionary<string, TargetFileLease> _entries;
    private PinnedDirectoryCreation.PinnedDirectoryAnchor? _targetRoot;
    private bool _disposed;

    public MarkerlessTargetVerificationLease(FileSystemPathSemantics semantics)
    {
        _entries = new Dictionary<string, TargetFileLease>(semantics.Comparer);
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
        if (!_entries.TryAdd(
                relativePath,
                new TargetFileLease(
                    entry,
                    expectedLength,
                    expectedSha256?.ToUpperInvariant())))
        {
            throw new InvalidOperationException(
                $"A target verification lease already exists for '{relativePath}'.");
        }
    }

    public bool TryGet(
        string relativePath,
        out PinnedDirectoryCreation.PinnedFileEntry? entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_entries.TryGetValue(relativePath, out var leased))
        {
            entry = leased.Entry;
            return true;
        }

        entry = null;
        return false;
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

        foreach (var leased in _entries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = leased.Entry.ProbePublicPathMatch();
            if (match == RegistrationPublicationMatchOutcome.Mismatch)
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }
            if (match == RegistrationPublicationMatchOutcome.Unavailable)
            {
                sawUnavailable = true;
                continue;
            }

            if (!leased.ExpectedLength.HasValue
                || string.IsNullOrWhiteSpace(leased.ExpectedSha256))
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }

            try
            {
                await using var stream = leased.Entry.OpenReadStream(
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
                if (leased.Entry.ProbePublicPathMatch()
                    != RegistrationPublicationMatchOutcome.Match)
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
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
        foreach (var leased in _entries.Values)
        {
            leased.Entry.Dispose();
        }
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
            PinnedDirectoryCreation.PinnedFileEntry entry,
            long? expectedLength,
            string? expectedSha256)
        {
            Entry = entry;
            ExpectedLength = expectedLength;
            ExpectedSha256 = expectedSha256;
        }

        public PinnedDirectoryCreation.PinnedFileEntry Entry { get; }
        public long? ExpectedLength { get; set; }
        public string? ExpectedSha256 { get; set; }
    }
}
