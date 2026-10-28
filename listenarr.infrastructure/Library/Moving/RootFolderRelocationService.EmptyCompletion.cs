using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task<PinnedDirectoryCreation.PinnedDirectoryAnchor>
        CompleteEmptyRelocationAsync(
            ListenArrDbContext db,
            RootFolder root,
            RootFolderRelocation relocation,
            RootFolderPathChangeCommand command,
            string targetPath,
            FileSystemSemanticsResolution targetResolution,
            DirectoryObjectIdentityResolution targetObjectIdentity,
            string targetIdentityKey,
            int rootFolderId,
            DateTime nowUtc,
            CancellationToken cancellationToken)
    {
        var targetLease = PinTargetDirectoryGeneration(
            targetPath,
            targetObjectIdentity.Version,
            targetObjectIdentity.Value,
            targetObjectIdentity.UnavailableReason,
            cancellationToken);
        try
        {
            ApplyRootMetadata(
                root,
                command,
                targetPath,
                targetResolution,
                targetIdentityKey);
            ApplyRootDirectoryObjectIdentity(root, targetObjectIdentity);
            if (command.DesiredIsDefault)
            {
                await ClearOtherDefaultsAsync(
                    db,
                    rootFolderId,
                    cancellationToken);
            }

            relocation.Status = RootFolderRelocationStatus.Completed;
            relocation.ActiveRootFolderId = null;
            relocation.CompletedAt = nowUtc;
            relocation.TargetIdentityEnrollmentState =
                TargetIdentityEnrollmentState.NotRequired;
            await FinalizeRelocationTargetReservationsAsync(
                db,
                relocation.Id,
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            BeforeEmptyRelocationAtomicCommitForTest?.Invoke();
            return targetLease;
        }
        catch
        {
            targetLease.Dispose();
            throw;
        }
    }
}
