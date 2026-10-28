using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Catalog;

public partial class LibraryAddService
{
    private static bool RepresentsSameDestinationAudiobook(
        Audiobook existing,
        Audiobook candidate)
    {
        var existingHasStrongIdentifier = HasStrongIdentifier(existing);
        var candidateHasStrongIdentifier = HasStrongIdentifier(candidate);
        if (StrongIdentifiersConflict(existing, candidate))
        {
            return false;
        }
        if (StrongIdentifierMatches(existing, candidate))
        {
            return true;
        }
        if (existingHasStrongIdentifier || candidateHasStrongIdentifier)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(existing.Title)
            || string.IsNullOrWhiteSpace(candidate.Title)
            || !EquivalentText(existing.Title, candidate.Title))
        {
            return false;
        }

        return EquivalentText(existing.Subtitle, candidate.Subtitle)
            && EquivalentValues(existing.Authors, candidate.Authors)
            && EquivalentText(existing.Series, candidate.Series)
            && EquivalentText(existing.SeriesNumber, candidate.SeriesNumber)
            && EquivalentText(existing.PublishYear, candidate.PublishYear)
            && EquivalentText(existing.PublishedDate, candidate.PublishedDate)
            && EquivalentText(existing.Edition, candidate.Edition)
            && EquivalentText(existing.Version, candidate.Version);
    }

    private static bool HasStrongIdentifier(Audiobook audiobook) =>
        !string.IsNullOrWhiteSpace(audiobook.Asin)
        || !string.IsNullOrWhiteSpace(audiobook.OpenLibraryId)
        || audiobook.Isbn?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;

    private static bool StrongIdentifiersConflict(Audiobook left, Audiobook right)
    {
        if (!string.IsNullOrWhiteSpace(left.Asin)
            && !string.IsNullOrWhiteSpace(right.Asin)
            && !EquivalentText(left.Asin, right.Asin))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(left.OpenLibraryId)
            && !string.IsNullOrWhiteSpace(right.OpenLibraryId)
            && !EquivalentText(left.OpenLibraryId, right.OpenLibraryId))
        {
            return true;
        }

        var leftIsbns = NormalizeValues(left.Isbn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightIsbns = NormalizeValues(right.Isbn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftIsbns.Count > 0
            && rightIsbns.Count > 0
            && !leftIsbns.Overlaps(rightIsbns);
    }

    private static bool StrongIdentifierMatches(Audiobook left, Audiobook right)
    {
        if (!string.IsNullOrWhiteSpace(left.Asin)
            && !string.IsNullOrWhiteSpace(right.Asin)
            && EquivalentText(left.Asin, right.Asin))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(left.OpenLibraryId)
            && !string.IsNullOrWhiteSpace(right.OpenLibraryId)
            && EquivalentText(left.OpenLibraryId, right.OpenLibraryId))
        {
            return true;
        }

        var leftIsbns = NormalizeValues(left.Isbn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NormalizeValues(right.Isbn).Any(leftIsbns.Contains);
    }

    private static bool EquivalentText(string? left, string? right) =>
        string.Equals(
            left?.Trim() ?? string.Empty,
            right?.Trim() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

    private static bool EquivalentValues(
        IEnumerable<string>? left,
        IEnumerable<string>? right) =>
        NormalizeValues(left).SequenceEqual(
            NormalizeValues(right),
            StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> NormalizeValues(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

    private async Task<Audiobook?> FindExistingDestinationOwnerAsync(
        string destinationPath,
        IReadOnlyCollection<RootFolder> configuredRoots,
        CancellationToken cancellationToken)
    {
        var existingAudiobooks = await _repo.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();

        // Exact canonical spelling is safe to deduplicate without consulting live
        // case semantics. This keeps an already-committed add idempotent even while
        // its storage is temporarily unavailable or under relocation.
        var canonicalDestination = FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
            destinationPath,
            out var normalizedDestination,
            out _)
            ? normalizedDestination
            : destinationPath;
        foreach (var existing in existingAudiobooks)
        {
            if (string.IsNullOrWhiteSpace(existing.BasePath))
            {
                continue;
            }

            var canonicalExisting = FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                existing.BasePath,
                out var normalizedExisting,
                out _)
                ? normalizedExisting
                : existing.BasePath;
            if (string.Equals(
                    canonicalExisting,
                    canonicalDestination,
                    StringComparison.Ordinal))
            {
                return existing;
            }
        }

        // Case-alias equivalence is only trustworthy when current filesystem
        // semantics can be resolved safely. Persisted Auto semantics can be stale
        // after a storage/mount change and must not manufacture a false duplicate.
        var semantics = await ResolveLiveDestinationSemanticsAsync(
            destinationPath,
            configuredRoots,
            cancellationToken);
        if (!semantics.HasValue)
        {
            return null;
        }

        foreach (var existing in existingAudiobooks)
        {
            if (string.IsNullOrWhiteSpace(existing.BasePath))
            {
                continue;
            }

            try
            {
                if (FileSystemPathIdentity.AreEquivalent(
                        existing.BasePath,
                        destinationPath,
                        semantics.Value))
                {
                    return existing;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                // Broken legacy path metadata is not evidence that this destination
                // is already owned. Mutation validation remains fail-closed below.
            }
        }

        return null;
    }

    private async Task<FileSystemPathSemantics?> ResolveLiveDestinationSemanticsAsync(
        string destinationPath,
        IReadOnlyCollection<RootFolder> configuredRoots,
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                destinationPath,
                out var destinationSyntax))
        {
            return null;
        }

        FileSystemPathSemantics? bestSemantics = null;
        var bestRootLength = -1;
        var unavailableRootLength = -1;
        foreach (var root in configuredRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        root.Path,
                        destinationPath,
                        destinationSyntax,
                        root.CaseSensitivityMode))
                {
                    unavailableRootLength = Math.Max(
                        unavailableRootLength,
                        root.Path.Length);
                }

                continue;
            }

            if (!FileSystemPathIdentity.StoredBoundaryMayContainPath(
                    canonicalRoot,
                    destinationPath,
                    destinationSyntax,
                    root.CaseSensitivityMode))
            {
                continue;
            }

            FileSystemSemanticsResolution resolution;
            try
            {
                resolution = await _semanticsResolver.ResolveAsync(
                    canonicalRoot,
                    root.CaseSensitivityMode,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException
                    or InvalidOperationException or NotSupportedException or PathTooLongException
                    or System.ComponentModel.Win32Exception
                    or System.Security.SecurityException)
            {
                unavailableRootLength = Math.Max(
                    unavailableRootLength,
                    canonicalRoot.Length);
                continue;
            }

            if (resolution.State != PathIdentityState.Valid
                || resolution.Semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
            {
                unavailableRootLength = Math.Max(
                    unavailableRootLength,
                    canonicalRoot.Length);
                continue;
            }

            try
            {
                if (!FileSystemPathIdentity.IsSameOrInside(
                        destinationPath,
                        canonicalRoot,
                        resolution.Semantics))
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                unavailableRootLength = Math.Max(
                    unavailableRootLength,
                    canonicalRoot.Length);
                continue;
            }

            if (canonicalRoot.Length > bestRootLength)
            {
                bestSemantics = resolution.Semantics;
                bestRootLength = canonicalRoot.Length;
            }
        }

        if (unavailableRootLength >= bestRootLength
            && unavailableRootLength >= 0)
        {
            return null;
        }
        if (bestSemantics.HasValue)
        {
            return bestSemantics.Value;
        }

        try
        {
            var direct = await _semanticsResolver.ResolveAsync(
                destinationPath,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
            return direct.State == PathIdentityState.Valid
                && direct.Semantics.CaseSensitivity != FileSystemCaseSensitivity.Unknown
                ? direct.Semantics
                : null;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or NotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception
                or System.Security.SecurityException)
        {
            return null;
        }
    }
}
