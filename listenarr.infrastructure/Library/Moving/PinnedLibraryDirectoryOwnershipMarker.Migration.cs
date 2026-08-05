namespace Listenarr.Infrastructure.Library.Moving;

internal static partial class PinnedLibraryDirectoryOwnershipMarker
{
    public static async Task PublishMigrationTargetAsync(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        CancellationToken cancellationToken,
        bool allowPublication = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parent);
        using var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(target.CanonicalPath));
        using var directory = publication.OpenCreatedDirectoryAnchor();
        if (!allowPublication)
        {
            ValidatePublishedMigrationTarget(
                target,
                directory,
                parent);
            return;
        }

        var targetNativeIdentity = directory.GetDirectoryObjectIdentity();
        if (!ManagedDirectoryIdentity.Matches(
                source.DirectoryObjectIdentityVersion,
                source.DirectoryObjectIdentity,
                source.OwnershipToken,
                targetNativeIdentity))
        {
            throw new InvalidOperationException(
                "Metadata-only relocation cannot transfer destructive directory ownership to a different physical directory generation.");
        }

        target.DirectoryObjectIdentityVersion =
            ManagedDirectoryIdentity.CurrentVersion;
        target.DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
            target.OwnershipToken,
            targetNativeIdentity);
        target.DirectoryObjectIdentityUnavailableReason = null;
        await PublishIdentityMigrationAsync(
            source,
            target,
            directory,
            parent,
            cancellationToken);
    }

    private static void ValidatePublishedMigrationTarget(
        LibraryDirectoryOwnership target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        using var insideMarker = directory.OpenExistingFile(
            LibraryDirectoryOwnershipMarker.FileName,
            requireDeleteAccess: false);
        using var siblingMarker = parent.OpenExistingFile(
            $".listenarr-directory-owner-{target.OwnershipToken}.json",
            requireDeleteAccess: false);
        var insidePayload =
            LibraryDirectoryOwnershipMarker.ReadPayload(insideMarker);
        var siblingPayload =
            LibraryDirectoryOwnershipMarker.ReadPayload(siblingMarker);
        if (insidePayload.DirectoryObjectIdentityVersion
                != ManagedDirectoryIdentity.CurrentVersion
            || string.IsNullOrWhiteSpace(
                insidePayload.DirectoryObjectIdentity)
            || siblingPayload != insidePayload)
        {
            throw new InvalidOperationException(
                "The published ownership migration markers do not identify one target generation.");
        }

        target.DirectoryObjectIdentityVersion =
            insidePayload.DirectoryObjectIdentityVersion;
        target.DirectoryObjectIdentity =
            insidePayload.DirectoryObjectIdentity;
        target.DirectoryObjectIdentityUnavailableReason = null;
        if (!LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                target,
                insidePayload)
            || !ManagedDirectoryIdentity.Matches(
                target.DirectoryObjectIdentityVersion,
                target.DirectoryObjectIdentity,
                target.OwnershipToken,
                directory.GetDirectoryObjectIdentity())
            || !insideMarker.VisiblePathMatches()
            || !siblingMarker.VisiblePathMatches()
            || !directory.VisiblePathMatches()
            || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The published ownership migration target no longer matches its enrolled directory generation.");
        }
    }

    internal static async Task PublishIdentityMigrationAsync(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(parent);
        if (!ManagedDirectoryIdentity.Matches(
                target.DirectoryObjectIdentityVersion,
                target.DirectoryObjectIdentity,
                target.OwnershipToken,
                directory.GetDirectoryObjectIdentity())
            || !parent.VisiblePathMatches()
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The migrated ownership target does not match the persisted physical directory generation.");
        }

        await PublishMigratedMarkerAsync(
            source,
            target,
            directory,
            LibraryDirectoryOwnershipMarker.FileName,
            cancellationToken);
        await PublishMigratedMarkerAsync(
            source,
            target,
            parent,
            $".listenarr-directory-owner-{target.OwnershipToken}.json",
            cancellationToken);
        directory.FlushDirectoryEntry();
        parent.FlushDirectoryEntry();
        if (!parent.VisiblePathMatches() || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The migrated ownership target changed during marker publication.");
        }
    }

    private static async Task PublishMigratedMarkerAsync(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName,
        CancellationToken cancellationToken)
    {
        RecoverConditionalReplacement(
            parent,
            fileName,
            payload => MatchesSourcePayload(source, payload),
            payload => LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                target,
                payload));
        using var existing = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: false);
        if (existing != null)
        {
            var payload = LibraryDirectoryOwnershipMarker.ReadPayload(existing);
            if (LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    target,
                    payload))
            {
                RetireCompletedReplacementTemporary(
                    parent,
                    fileName + ".migration.tmp",
                    temporaryPayload =>
                        LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                            target,
                            temporaryPayload)
                        || MatchesSourcePayload(source, temporaryPayload));
                return;
            }
            if (!MatchesSourcePayload(source, payload))
            {
                throw new InvalidOperationException(
                    "The ownership migration target contains an unrelated marker.");
            }
        }

        var temporaryName = fileName + ".migration.tmp";
        using var interrupted = parent.TryOpenExistingFile(
            temporaryName,
            requireDeleteAccess: true);
        if (interrupted != null)
        {
            var interruptedPayload =
                LibraryDirectoryOwnershipMarker.ReadPayload(interrupted);
            if (!LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    target,
                    interruptedPayload))
            {
                throw new InvalidOperationException(
                    "The ownership migration temporary marker is unrelated.");
            }

            if (existing != null)
            {
                interrupted.ReplaceWithinParent(fileName, existing);
            }
            else
            {
                interrupted.MoveWithinParent(fileName);
            }
            return;
        }

        using var temporary = parent.CreateNewFile(
            temporaryName,
            hiddenFile: true);
        await using (var stream = temporary.OpenWriteStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                LibraryDirectoryOwnershipMarker.SerializePayload(target));
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        if (existing != null)
        {
            temporary.ReplaceWithinParent(fileName, existing);
        }
        else
        {
            temporary.MoveWithinParent(fileName);
        }
    }

    private static bool MatchesSourcePayload(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnershipMarker.MarkerPayload payload) =>
        LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(source, payload)
        || LibraryDirectoryOwnershipMarker.MatchesLegacyPayload(source, payload);
}
