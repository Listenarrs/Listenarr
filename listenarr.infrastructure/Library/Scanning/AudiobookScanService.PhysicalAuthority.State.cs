namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    private sealed class PinnedScanAuthority(
        IReadOnlyList<PinnedDirectoryState> directories) : IDisposable
    {
        private bool _disposed;

        internal PinnedDirectoryCreation.PinnedDirectoryAnchor Root =>
            directories[^1].Anchor;

        internal void Validate(AudiobookScanCommand command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var directory in directories)
            {
                if (!directory.Anchor.VisiblePathMatches()
                    || !string.Equals(
                        directory.Anchor.GetDirectoryObjectIdentity(),
                        directory.ObjectIdentity,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The physical scan hierarchy changed after authorization.");
                }
            }

            if (!string.Equals(
                    directories[0].Anchor.GetDirectoryObjectIdentity(),
                    command.ScanPhysicalIdentity.BoundaryObjectIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    Root.GetDirectoryObjectIdentity(),
                    command.ScanPhysicalIdentity.ScanRootObjectIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The physical scan-root generation changed after authorization.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            for (var index = directories.Count - 1; index >= 0; index--)
            {
                directories[index].Anchor.Dispose();
            }

            _disposed = true;
        }
    }

    private sealed record PinnedDirectoryState(
        PinnedDirectoryCreation.PinnedDirectoryAnchor Anchor,
        string ObjectIdentity)
    {
        internal static PinnedDirectoryState Capture(
            PinnedDirectoryCreation.PinnedDirectoryAnchor anchor) =>
            new(anchor, anchor.GetDirectoryObjectIdentity());
    }
}
