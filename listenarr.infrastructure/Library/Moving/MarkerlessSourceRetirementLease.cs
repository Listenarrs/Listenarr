using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

// Only the live copy invocation owns these process-local observations. Files and
// directories are reopened at use; restart cannot reconstruct deletion permission.
internal sealed class MarkerlessSourceRetirementLease(
    FileSystemPathSemantics semantics,
    bool allowDirectoryRetirement,
    Func<string, PinnedDirectoryCreation.PinnedDirectoryAnchor> openDirectory) : IDisposable
{
    private readonly Dictionary<string, FilePublicationObservation> _entries =
        new(semantics.Comparer);

    private readonly Dictionary<string, string> _directories =
        new(semantics.Comparer);

    // Only the directory currently being retired stays open. Sibling count does
    // not affect handle use; restart never reconstructs these observations.
    private PinnedDirectoryCreation.PinnedDirectoryAnchor? _activeDirectory;

    public void AddDirectory(string path, PinnedDirectoryCreation.PinnedDirectoryAnchor original)
    {
        if (allowDirectoryRetirement)
            _directories.Add(path, PinnedDirectoryCreation.CaptureDiagnosticIdentity(original.GetDirectoryObjectIdentity));
    }

    public bool HasDirectory(string path) => _directories.ContainsKey(path);

    public bool TryGetDirectory(string path, out PinnedDirectoryCreation.PinnedDirectoryAnchor? original)
    {
        _activeDirectory?.Dispose();
        _activeDirectory = null;
        original = null;
        if (!allowDirectoryRetirement || !_directories.TryGetValue(path, out var identity))
            return false;
        var current = openDirectory(path);
        try
        {
            if (!current.VisiblePathMatches()
                || (!string.IsNullOrEmpty(identity) && !current.MatchesDirectoryObjectIdentity(identity)))
            {
                current.Dispose();
                return false;
            }
            _activeDirectory = original = current;
            return true;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    public PinnedDirectoryCreation.PinnedDirectoryAnchor PromoteDirectory(
        string path, PinnedDirectoryCreation.PinnedDirectoryAnchor publication)
    {
        var original = _activeDirectory;
        if (original == null || !semantics.Comparer.Equals(original.FullPath, path)
            || !original.VisiblePathMatches() || !publication.VisiblePathMatches()
            || !original.IdentifiesSameDirectory(publication))
            throw new MoveNeedsAttentionException("The current directory publication changed.");
        var promoted = publication.Duplicate();
        _activeDirectory = promoted;
        original.Dispose();
        return promoted;
    }

    public MarkerlessSourceRetirementLease? DuplicateAncestors(string source)
    {
        if (!allowDirectoryRetirement)
        {
            return null;
        }

        var ancestors = new MarkerlessSourceRetirementLease(semantics, true, openDirectory);
        try
        {
            foreach (var pair in _directories.Where(pair =>
                FileSystemPathIdentity.IsSameOrInside(source, pair.Key, semantics)
                && !FileSystemPathIdentity.AreEquivalent(source, pair.Key, semantics)))
            {
                ancestors._directories.Add(pair.Key, pair.Value);
            }
            return ancestors;
        }
        catch
        {
            ancestors.Dispose();
            throw;
        }
    }

    public void ReleaseDirectory(string path)
    {
        _directories.Remove(path);
        if (_activeDirectory != null && semantics.Comparer.Equals(_activeDirectory.FullPath, path))
        {
            _activeDirectory.Dispose();
            _activeDirectory = null;
        }
    }

    public void Add(string relativePath, PinnedDirectoryCreation.PinnedFileEntry original) =>
        _entries.Add(relativePath, FilePublicationObservation.Capture(original));

    public bool Matches(string relativePath, PinnedDirectoryCreation.PinnedFileEntry current) =>
        _entries.TryGetValue(relativePath, out var observation) && observation.Matches(current);

    public void Release(string relativePath) => _entries.Remove(relativePath);

    public void Dispose()
    {
        _activeDirectory?.Dispose();
        _activeDirectory = null;
        _entries.Clear();
        _directories.Clear();
    }
}
