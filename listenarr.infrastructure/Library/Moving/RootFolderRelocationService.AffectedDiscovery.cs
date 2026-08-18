using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private sealed record TrackedPathDiscoveryResult(
        List<AudiobookPathCandidate> Affected,
        List<AudiobookPathCandidate> Invalid);

    private static async Task<(
        List<AudiobookPathCandidate> Affected,
        List<AudiobookPathCandidate> InvalidStoredBasePaths)> LoadAffectedAudiobooksAsync(
        ListenArrDbContext db,
        string sourceRootPath,
        PersistedRootFolderPathSemantics? metadataSourcePathSemantics,
        bool allowContextualAmbiguousMetadataSyntax,
        CancellationToken cancellationToken)
    {
        if (metadataSourcePathSemantics == null)
        {
            return ([], []);
        }

        var audiobookRows = await db.Audiobooks
            .Select(audiobook => new
            {
                Audiobook = audiobook,
                StoredBasePath = EF.Property<string?>(
                    audiobook,
                    nameof(Audiobook.BasePath))
            })
            .ToListAsync(cancellationToken);
        await db.AudiobookFiles.LoadAsync(cancellationToken);

        var audiobooks = audiobookRows
            .Where(row => !string.IsNullOrWhiteSpace(row.StoredBasePath))
            .Select(row => new AudiobookPathCandidate(
                row.Audiobook,
                row.StoredBasePath!))
            .ToList();
        var (affected, invalidStoredBasePaths) = DiscoverAffectedAudiobooks(
            audiobooks,
            sourceRootPath,
            metadataSourcePathSemantics.Value.Semantics,
            metadataSourcePathSemantics.Value.DetectAmbiguousCaseMatches,
            allowContextualAmbiguousMetadataSyntax);

        var classifiedAudiobookIds = affected
            .Concat(invalidStoredBasePaths)
            .Select(candidate => candidate.Audiobook.Id)
            .ToHashSet();
        var trackedEvidence = DiscoverAffectedAudiobooksFromTrackedPaths(
            audiobookRows.Select(row => row.Audiobook),
            classifiedAudiobookIds,
            sourceRootPath,
            metadataSourcePathSemantics.Value.Semantics);
        affected.AddRange(trackedEvidence.Affected);
        invalidStoredBasePaths.AddRange(trackedEvidence.Invalid);
        return (affected, invalidStoredBasePaths);
    }

    private static TrackedPathDiscoveryResult DiscoverAffectedAudiobooksFromTrackedPaths(
        IEnumerable<Audiobook> audiobooks,
        IReadOnlySet<int> alreadyClassifiedAudiobookIds,
        string sourceRootPath,
        FileSystemPathSemantics sourceSemantics)
    {
        var affected = new List<AudiobookPathCandidate>();
        var invalid = new List<AudiobookPathCandidate>();

        foreach (var audiobook in audiobooks)
        {
            if (alreadyClassifiedAudiobookIds.Contains(audiobook.Id))
            {
                continue;
            }

            var storedPaths = (audiobook.Files ?? [])
                .Select(file => file.PathIdentityState == PathIdentityState.Valid
                    && file.PathSyntax == sourceSemantics.Syntax
                    && !string.IsNullOrWhiteSpace(file.CanonicalPath)
                        ? file.CanonicalPath
                        : file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToList();
            if (storedPaths.Count == 0
                && !string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                storedPaths.Add(audiobook.FilePath);
            }
            if (storedPaths.Count == 0)
            {
                continue;
            }

            var pathsUnderRoot = new List<string>();
            var hasUnresolvedOrExternalPath = false;
            var hasUnresolvedPathThatMayTouchRoot = false;
            foreach (var storedPath in storedPaths)
            {
                if (!TryCanonicalizeTrackedEvidencePath(
                        storedPath,
                        sourceSemantics,
                        out var canonicalPath))
                {
                    if (FileSystemPathIdentity.StoredPathMayTouchBoundary(
                            storedPath,
                            sourceRootPath,
                            sourceSemantics))
                    {
                        hasUnresolvedPathThatMayTouchRoot = true;
                    }
                    else
                    {
                        hasUnresolvedOrExternalPath = true;
                    }

                    continue;
                }

                if (!FileSystemPathIdentity.IsSameOrInside(
                        canonicalPath,
                        sourceRootPath,
                        sourceSemantics))
                {
                    hasUnresolvedOrExternalPath = true;
                    continue;
                }

                pathsUnderRoot.Add(canonicalPath);
            }

            if (pathsUnderRoot.Count == 0)
            {
                if (hasUnresolvedPathThatMayTouchRoot)
                {
                    invalid.Add(new AudiobookPathCandidate(audiobook, sourceRootPath));
                }

                continue;
            }

            if (hasUnresolvedOrExternalPath
                || hasUnresolvedPathThatMayTouchRoot
                || !string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                // Tracked-file evidence proves that this audiobook touches the root,
                // but its complete source coordinate cannot be inferred safely.
                invalid.Add(new AudiobookPathCandidate(audiobook, sourceRootPath));
                continue;
            }

            var inferredBasePath = InferTrackedSourceBasePath(
                sourceRootPath,
                pathsUnderRoot,
                sourceSemantics);
            if (inferredBasePath == null)
            {
                invalid.Add(new AudiobookPathCandidate(audiobook, sourceRootPath));
                continue;
            }

            affected.Add(new AudiobookPathCandidate(audiobook, inferredBasePath));
        }

        return new TrackedPathDiscoveryResult(affected, invalid);
    }

    private static bool StoredBasePathMayBelongToRoot(
        string storedBasePath,
        FileSystemPathSyntax storedSyntax,
        string sourceRootPath,
        FileSystemPathSemantics sourceSemantics,
        bool detectAmbiguousCaseMatches)
    {
        try
        {
            var canonicalStoredBasePath = FileSystemPathIdentity.Canonicalize(
                storedBasePath,
                storedSyntax);
            if (FileSystemPathIdentity.IsSameOrInside(
                    canonicalStoredBasePath,
                    sourceRootPath,
                    sourceSemantics))
            {
                return true;
            }

            return detectAmbiguousCaseMatches
                && FileSystemPathIdentity.IsSameOrInside(
                    canonicalStoredBasePath,
                    sourceRootPath,
                    new FileSystemPathSemantics(
                        sourceSemantics.Syntax,
                        FileSystemCaseSensitivity.Insensitive));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool TryCanonicalizeTrackedEvidencePath(
        string storedPath,
        FileSystemPathSemantics sourceSemantics,
        out string canonicalPath)
    {
        return FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
            storedPath,
            out canonicalPath,
            out _,
            sourceSemantics.Syntax);
    }

    private static string? InferTrackedSourceBasePath(
        string sourceRootPath,
        IReadOnlyList<string> trackedPaths,
        FileSystemPathSemantics sourceSemantics)
    {
        var relativeDirectories = new List<string[]>(trackedPaths.Count);
        foreach (var trackedPath in trackedPaths)
        {
            if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    sourceRootPath,
                    trackedPath,
                    sourceSemantics,
                    out var relativePath)
                || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            var normalized = sourceSemantics.Syntax == FileSystemPathSyntax.Windows
                ? relativePath.Replace('\\', '/')
                : relativePath;
            var separatorIndex = normalized.LastIndexOf('/');
            var relativeDirectory = separatorIndex < 0
                ? string.Empty
                : normalized[..separatorIndex];
            relativeDirectories.Add(relativeDirectory.Length == 0
                ? []
                : relativeDirectory.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries));
        }

        var commonLength = relativeDirectories.Min(parts => parts.Length);
        for (var index = 0; index < commonLength; index++)
        {
            var expected = relativeDirectories[0][index];
            if (relativeDirectories.Skip(1).Any(parts =>
                    !sourceSemantics.Comparer.Equals(expected, parts[index])))
            {
                commonLength = index;
                break;
            }
        }

        if (commonLength == 0)
        {
            return FileSystemPathIdentity.Canonicalize(
                sourceRootPath,
                sourceSemantics.Syntax);
        }

        var relativeBase = string.Join(
            sourceSemantics.Syntax == FileSystemPathSyntax.Windows ? '\\' : '/',
            relativeDirectories[0].Take(commonLength));
        return FileSystemPathIdentity.TryResolveRelativePathWithinBase(
            sourceRootPath,
            relativeBase,
            sourceSemantics,
            out var inferredBasePath)
                ? inferredBasePath
                : null;
    }
}
