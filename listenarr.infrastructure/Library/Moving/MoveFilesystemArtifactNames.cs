namespace Listenarr.Infrastructure.Library.Moving;

internal static class MoveFilesystemArtifactNames
{
    public static bool IsReserved(string name) =>
        name.StartsWith(".listenarr-move-", StringComparison.Ordinal)
        || name.StartsWith(".listenarr-quarantine-", StringComparison.Ordinal)
        || name.StartsWith(".listenarr-temporary-directory-", StringComparison.Ordinal)
        || string.Equals(name, ".listenarr-temp-owner.json", StringComparison.Ordinal)
        || string.Equals(name, ".listenarr-quarantine-owner.json", StringComparison.Ordinal)
        || string.Equals(name, LibraryDirectoryOwnershipMarker.FileName, StringComparison.Ordinal)
        || string.Equals(name, ManagedDirectoryEnrollment.FileName, StringComparison.Ordinal)
        || name.StartsWith(".listenarr-directory-owner-", StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.Ordinal)
        || name.Contains(".listenarr-", StringComparison.Ordinal)
            && name.EndsWith(".partial", StringComparison.Ordinal);
}
