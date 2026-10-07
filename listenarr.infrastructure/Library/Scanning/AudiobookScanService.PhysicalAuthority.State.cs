using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    private sealed class PinnedScanAuthority(
        IReadOnlyList<PinnedDirectoryState> directories,
        FileSystemPathSemantics semantics) : IDisposable
    {
        private readonly Dictionary<string, PinnedDirectoryCreation.PinnedDirectoryAnchor>
            _discoveredDirectories = new(semantics.Comparer);
        private readonly Dictionary<string, PinnedDirectoryCreation.PinnedFileEntry>
            _discoveredFiles = new(semantics.Comparer);

        internal void CaptureDirectory(PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = FileSystemPathIdentity.Canonicalize(directory.FullPath, semantics.Syntax);
            var retained = directory.Duplicate();
            if (_discoveredDirectories.Remove(key, out var previous)) previous.Dispose();
            _discoveredDirectories.Add(key, retained);
        }

        internal void CaptureFile(PinnedDirectoryCreation.PinnedFileEntry file)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = FileSystemPathIdentity.Canonicalize(file.FullPath, semantics.Syntax);
            var retained = file.DuplicateForOperation();
            if (_discoveredFiles.Remove(key, out var previous)) previous.Dispose();
            _discoveredFiles.Add(key, retained);
        }

        internal void ValidateDiscoveredDirectory(string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = FileSystemPathIdentity.Canonicalize(path, semantics.Syntax);
            if (!_discoveredDirectories.TryGetValue(key, out var original)
                || !original.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A scan directory changed or disappeared after discovery.");
            }
        }

        internal bool DiscoveredFileMatches(string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = FileSystemPathIdentity.Canonicalize(path, semantics.Syntax);
            return _discoveredFiles.TryGetValue(key, out var original)
                && original.VisiblePathMatches();
        }

        private bool _disposed;

        internal PinnedDirectoryCreation.PinnedDirectoryAnchor Root =>
            directories[^1].Anchor;

        internal void Validate(AudiobookScanCommand command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var directory in directories)
            {
                if (!directory.Anchor.VisiblePathMatches()
                    || (directory.ObjectIdentity != null
                        && !directory.Anchor.MatchesDirectoryObjectIdentity(
                            directory.ObjectIdentity)))
                {
                    throw new InvalidOperationException(
                        "The physical scan hierarchy changed after authorization.");
                }
            }

        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var file in _discoveredFiles.Values) file.Dispose();
            foreach (var directory in _discoveredDirectories.Values) directory.Dispose();
            _discoveredFiles.Clear();
            _discoveredDirectories.Clear();

            for (var index = directories.Count - 1; index >= 0; index--)
            {
                directories[index].Anchor.Dispose();
            }

            _disposed = true;
        }
    }

    private sealed record PinnedDirectoryState(
        PinnedDirectoryCreation.PinnedDirectoryAnchor Anchor,
        string? ObjectIdentity)
    {
        internal static PinnedDirectoryState Capture(
            PinnedDirectoryCreation.PinnedDirectoryAnchor anchor) =>
            new(anchor, null);
    }
}
