using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

public static class LibraryDirectoryOwnershipPlanning
{
    public static string? SelectMostSpecificBoundary(
        string destinationDirectory,
        IEnumerable<string?> candidateBoundaries,
        FileSystemPathSemantics semantics) =>
        candidateBoundaries
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Where(path => FileSystemPathIdentity.IsSameOrInside(
                destinationDirectory,
                path,
                semantics))
            .OrderByDescending(path =>
                FileSystemPathIdentity.Canonicalize(path, semantics.Syntax).Length)
            .FirstOrDefault();
}
