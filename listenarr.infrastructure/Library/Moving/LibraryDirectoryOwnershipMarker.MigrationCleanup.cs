namespace Listenarr.Infrastructure.Library.Moving;

internal static partial class LibraryDirectoryOwnershipMarker
{
    internal static bool TryRetireMigrationArtifacts(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(parent);
        try
        {
            var retiredInsideArtifact = false;
            foreach (var fileName in GetInsideMigrationArtifactNames())
            {
                retiredInsideArtifact |= RetireMigrationArtifactIfPresent(
                    source,
                    target,
                    directory,
                    fileName);
            }

            var retiredSiblingArtifact = false;
            var siblingName =
                $".listenarr-directory-owner-{source.OwnershipToken}.json";
            foreach (var fileName in GetSiblingMigrationArtifactNames(
                siblingName))
            {
                retiredSiblingArtifact |= RetireMigrationArtifactIfPresent(
                    source,
                    target,
                    parent,
                    fileName);
            }

            if (retiredInsideArtifact)
            {
                directory.FlushDirectoryEntry();
            }
            if (retiredSiblingArtifact)
            {
                parent.FlushDirectoryEntry();
            }
            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static IEnumerable<string> GetInsideMigrationArtifactNames()
    {
        yield return FileName;
        yield return FileName + ".v2.tmp";
        yield return FileName + ".migration.tmp";
        yield return PinnedDirectoryCreation
            .GetConditionalReplacementBackupName(FileName);
    }

    private static IEnumerable<string> GetSiblingMigrationArtifactNames(
        string siblingName)
    {
        yield return siblingName;
        yield return siblingName + ".v2.tmp";
        yield return siblingName + ".migration.tmp";
        yield return PinnedDirectoryCreation
            .GetConditionalReplacementBackupName(siblingName);
    }

    private static bool RetireMigrationArtifactIfPresent(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName)
    {
        using var marker = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: true);
        if (marker == null)
        {
            return false;
        }

        var payload = ReadPayload(marker);
        if (!MatchesMigrationPayload(source, target, payload)
            || !parent.VisiblePathMatches()
            || !marker.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "An ownership migration artifact does not match either persisted migration generation.");
        }

        var verifiedPayload = ReadPayload(marker);
        if (!MatchesMigrationPayload(source, target, verifiedPayload)
            || !parent.VisiblePathMatches()
            || !marker.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "An ownership migration artifact changed before retirement.");
        }

        marker.Delete();
        return true;
    }

    private static bool MatchesMigrationPayload(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        MarkerPayload payload) =>
        MatchesCurrentPayload(source, payload)
        || MatchesLegacyPayload(source, payload)
        || MatchesCurrentPayload(target, payload)
        || MatchesLegacyPayload(target, payload);
}
