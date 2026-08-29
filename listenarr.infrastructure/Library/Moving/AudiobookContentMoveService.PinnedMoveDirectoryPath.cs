using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private sealed class PinnedMoveDirectoryPath : IDisposable
    {
        private readonly List<PinnedDirectoryCreation.PinnedDirectoryAnchor> _anchors;
        private bool _disposed;

        private PinnedMoveDirectoryPath(
            List<PinnedDirectoryCreation.PinnedDirectoryAnchor> anchors)
        {
            _anchors = anchors;
        }

        internal PinnedDirectoryCreation.PinnedDirectoryAnchor Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _anchors[^1];
            }
        }

        internal static PinnedMoveDirectoryPath OpenExisting(
            string boundaryPath,
            string root,
            FileSystemPathSemantics semantics,
            int boundaryIdentityVersion,
            string boundaryIdentity,
            IReadOnlyList<string> segments)
        {
            var anchors = new List<PinnedDirectoryCreation.PinnedDirectoryAnchor>();
            try
            {
                var current = OpenPinnedMoveBoundaryDescendant(
                    boundaryPath,
                    root,
                    semantics,
                    boundaryIdentityVersion,
                    boundaryIdentity,
                    "target boundary");
                anchors.Add(current);
                foreach (var segment in segments)
                {
                    current = current.OpenExistingChild(segment);
                    anchors.Add(current);
                }

                var path = new PinnedMoveDirectoryPath(anchors);
                path.EnsureVisibleHierarchy();
                return path;
            }
            catch
            {
                DisposeAnchors(anchors);
                throw;
            }
        }

        internal void EnsureVisibleHierarchy()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var anchor in _anchors)
            {
                var visibility = anchor.ProbeVisiblePathMatch();
                if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
                {
                    throw new IOException(
                        "A pinned move directory hierarchy is temporarily unavailable during mutation.");
                }
                if (visibility != RegistrationPublicationMatchOutcome.Match)
                {
                    throw new MoveNeedsAttentionException(
                        "A pinned move directory hierarchy changed during mutation.");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            DisposeAnchors(_anchors);
            _disposed = true;
        }

        private static void DisposeAnchors(
            IReadOnlyList<PinnedDirectoryCreation.PinnedDirectoryAnchor> anchors)
        {
            for (var index = anchors.Count - 1; index >= 0; index--)
            {
                anchors[index].Dispose();
            }
        }
    }
}
