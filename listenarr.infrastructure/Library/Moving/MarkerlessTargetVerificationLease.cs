using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class MarkerlessTargetVerificationLease : IDisposable
{
    private readonly Dictionary<string, PinnedDirectoryCreation.PinnedFileEntry> _entries;
    private bool _disposed;

    public MarkerlessTargetVerificationLease(FileSystemPathSemantics semantics)
    {
        _entries = new Dictionary<string, PinnedDirectoryCreation.PinnedFileEntry>(
            semantics.Comparer);
    }

    public bool IsEmpty => _entries.Count == 0;

    public void Add(
        string relativePath,
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(entry);
        if (!_entries.TryAdd(relativePath, entry))
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
        return _entries.TryGetValue(relativePath, out entry);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }
        _entries.Clear();
    }
}
