using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    internal Action<string>? BeforeTargetReservationPlanForTest
    {
        get;
        set;
    }

    private async Task<DirectoryObjectIdentityResolution>
        ReserveRelocationTargetAsync(
            Guid relocationId,
            string targetPath,
            CancellationToken cancellationToken)
    {
        await using (var existingDb =
            await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var firstExisting = await existingDb
                .RootFolderRelocationCreatedDirectories
                .AsNoTracking()
                .Where(candidate =>
                    candidate.RelocationId == relocationId)
                .OrderBy(candidate => candidate.CanonicalPath.Length)
                .FirstOrDefaultAsync(cancellationToken);
            if (firstExisting != null)
            {
                var persistedAncestor = Path.GetDirectoryName(
                    firstExisting.CanonicalPath)
                    ?? throw new InvalidOperationException(
                        "A persisted relocation target reservation has no parent.");
                return await CreateOrReuseTargetReservationsAsync(
                    relocationId,
                    persistedAncestor,
                    cancellationToken);
            }
        }

        BeforeTargetReservationPlanForTest?.Invoke(targetPath);
        var plan = DiscoverTargetReservationPlan(targetPath);
        if (plan.Segments.Count == 0)
        {
            return await ResolveOrEnrollDirectoryObjectIdentityAsync(
                targetPath,
                cancellationToken);
        }

        await PersistTargetReservationPlanAsync(
            relocationId,
            plan,
            cancellationToken);
        return await CreateOrReuseTargetReservationsAsync(
            relocationId,
            plan.ExistingAncestor,
            cancellationToken);
    }

    private static TargetReservationPlan DiscoverTargetReservationPlan(
        string targetPath)
    {
        var canonicalTarget = Path.GetFullPath(targetPath);
        var missing = new Stack<string>();
        var current = canonicalTarget;
        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "A relocation target segment is occupied by a file or linked directory.");
                }

                break;
            }
            catch (Exception exception) when (
                FileSystemSafety.IsProvenMissingPathException(exception))
            {
                missing.Push(current);
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException(
                        "The relocation target has no existing directory ancestor.");
            }
        }

        using var ancestor =
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                current,
                createMissing: false);
        var ancestorVisibility = ancestor.ProbeVisiblePathMatch();
        if (ancestorVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The relocation target ancestor is temporarily unavailable during planning.");
        }
        if (ancestorVisibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw new InvalidOperationException(
                "The relocation target ancestor changed during planning.");
        }

        return new TargetReservationPlan(
            ancestor.FullPath,
            ancestor.GetDirectoryObjectIdentity(),
            missing.ToList());
    }
}
