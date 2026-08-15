using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Files;

internal static class AudiobookFileOwnershipValidator
{
    public static bool UnresolvedIdentityOverlaps(
        AudiobookFile candidate,
        FileSystemPathSyntax targetSyntax,
        FileSystemCaseSensitivity targetSensitivity,
        string targetCanonicalPath)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCanonicalPath);
        if (string.IsNullOrWhiteSpace(candidate.CanonicalPath)
            || candidate.PathSyntax == null)
        {
            return true;
        }
        if (candidate.PathSyntax != targetSyntax)
        {
            return false;
        }
        if (candidate.PathCaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            return true;
        }

        var comparison = candidate.PathCaseSensitivity == FileSystemCaseSensitivity.Insensitive
            || targetSensitivity == FileSystemCaseSensitivity.Insensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        return string.Equals(
            candidate.CanonicalPath,
            targetCanonicalPath,
            comparison);
    }

    public static void RejectDuplicateValidOwnership(
        IEnumerable<AudiobookFile> files,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        var duplicate = files
            .Where(file => file.PathIdentityState == PathIdentityState.Valid)
            .Where(file => !string.IsNullOrWhiteSpace(file.PathOwnershipKey))
            .GroupBy(file => file.PathOwnershipKey!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(file => file.Id).Distinct().Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }
}
