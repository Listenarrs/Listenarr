using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

// Only the live copy invocation owns these original source handles. Durable journals
// and restarted operations cannot construct this retirement authority.
internal sealed class MarkerlessSourceRetirementLease(
    FileSystemPathSemantics semantics,
    bool allowDirectoryRetirement) : IDisposable
{
    private readonly Dictionary<string, PinnedDirectoryCreation.PinnedFileEntry> _entries =
        new(semantics.Comparer);

    private readonly Dictionary<string, PinnedDirectoryCreation.PinnedDirectoryAnchor> _directories =
        new(semantics.Comparer);

    public void AddDirectory(string path, PinnedDirectoryCreation.PinnedDirectoryAnchor original)
    {
        var duplicate = original.Duplicate();
        try { _directories.Add(path, duplicate); }
        catch { duplicate.Dispose(); throw; }
    }

    public bool TryGetDirectory(string path, out PinnedDirectoryCreation.PinnedDirectoryAnchor? original)
    {
        original = null;
        return allowDirectoryRetirement && _directories.TryGetValue(path, out original);
    }

    public PinnedDirectoryCreation.PinnedDirectoryAnchor PromoteDirectory(
        string path,
        PinnedDirectoryCreation.PinnedDirectoryAnchor publication)
    {
        if (!TryGetDirectory(path, out var original)
            || original == null
            || !original.VisiblePathMatches()
            || !publication.VisiblePathMatches()
            || !original.IdentifiesSameDirectory(publication))
        {
            throw new MoveNeedsAttentionException("The original live directory publication changed.");
        }

        // Transfer the continuous proof to the publication handle before releasing
        // the original observation handle, whose Windows share mode can deny removal.
        var promoted = publication.Duplicate();
        _directories[path] = promoted;
        original.Dispose();
        return promoted;
    }

    public MarkerlessSourceRetirementLease? DuplicateAncestors(string source)
    {
        if (!allowDirectoryRetirement)
        {
            return null;
        }

        var ancestors = new MarkerlessSourceRetirementLease(semantics, true);
        try
        {
            foreach (var pair in _directories.Where(pair =>
                FileSystemPathIdentity.IsSameOrInside(source, pair.Key, semantics)
                && !FileSystemPathIdentity.AreEquivalent(source, pair.Key, semantics)))
            {
                ancestors.AddDirectory(pair.Key, pair.Value);
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
        if (_directories.Remove(path, out var original))
        {
            original.Dispose();
        }
    }

    public void Add(string relativePath, PinnedDirectoryCreation.PinnedFileEntry original)
    {
        var duplicate = original.DuplicateForOperation();
        try
        {
            _entries.Add(relativePath, duplicate);
        }
        catch
        {
            duplicate.Dispose();
            throw;
        }
    }

    public bool TryGet(string relativePath, out PinnedDirectoryCreation.PinnedFileEntry? original) =>
        _entries.TryGetValue(relativePath, out original);

    public void Release(string relativePath)
    {
        if (_entries.Remove(relativePath, out var original))
        {
            original.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var original in _entries.Values)
        {
            original.Dispose();
        }
        _entries.Clear();
        foreach (var original in _directories.Values)
        {
            original.Dispose();
        }
        _directories.Clear();
    }
}
