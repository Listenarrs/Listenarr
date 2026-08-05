using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    internal Action? BeforeOwnershipMigrationSourceRetirementForTest
    {
        get;
        set;
    }

    private async Task RetireOwnershipMigrationSourcesAsync(
        IReadOnlyList<OwnershipMigrationPlan> plans,
        string sourceBoundary,
        string targetBoundary,
        int? targetIdentityVersion,
        string? targetIdentityValue,
        string? targetIdentityUnavailableReason,
        CancellationToken cancellationToken)
    {
        BeforeOwnershipMigrationSourceRetirementForTest?.Invoke();
        if (plans.Count > 0)
        {
            using var targetBoundaryAnchor = await OpenVerifiedMarkerParentWithinBoundaryAsync(
                targetBoundary,
                targetBoundary,
                plans[0].Target.GetIdentity().Semantics,
                targetIdentityVersion,
                targetIdentityValue,
                targetIdentityUnavailableReason,
                cancellationToken);
        }

        foreach (var plan in plans)
        {
            var sourceSiblingMarker =
                LibraryDirectoryOwnershipMarker.GetMarkerPaths(
                    plan.Source)[1];
            var targetSiblingMarker =
                LibraryDirectoryOwnershipMarker.GetMarkerPaths(
                    plan.Target)[1];
            if (FileSystemPathIdentity.AreEquivalentEndpoints(
                    sourceSiblingMarker,
                    plan.Source.GetIdentity().Semantics,
                    targetSiblingMarker,
                    plan.Target.GetIdentity().Semantics))
            {
                continue;
            }

            var sourceParentPath = Path.GetDirectoryName(sourceSiblingMarker)
                ?? throw new InvalidOperationException(
                    "The retired ownership marker has no source parent.");
            var targetParentPath = Path.GetDirectoryName(targetSiblingMarker)
                ?? throw new InvalidOperationException(
                    "The active ownership marker has no target parent.");
            using var sourceParent = OpenMarkerParentWithinBoundary(
                sourceBoundary,
                sourceParentPath,
                plan.Source.GetIdentity().Semantics);
            using var targetParent = await OpenVerifiedMarkerParentWithinBoundaryAsync(
                targetBoundary,
                targetParentPath,
                plan.Target.GetIdentity().Semantics,
                targetIdentityVersion,
                targetIdentityValue,
                targetIdentityUnavailableReason,
                cancellationToken);
            using var targetMarker = targetParent.OpenExistingFileForStableRead(
                Path.GetFileName(targetSiblingMarker));
            ValidateRetirementTarget(plan, targetParent, targetMarker);

            if (!sourceParent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The retired ownership marker parent changed before source retirement.");
            }

            if (string.Equals(
                    Path.GetFileName(sourceSiblingMarker),
                    Path.GetFileName(targetSiblingMarker),
                    StringComparison.Ordinal)
                && string.Equals(
                    sourceParent.GetDirectoryObjectIdentity(),
                    targetParent.GetDirectoryObjectIdentity(),
                    StringComparison.Ordinal))
            {
                // The source and target are lexical aliases for the same physical
                // namespace entry. Opening the same Windows file for deletion while
                // its stable-read handle is active would create a false sharing
                // violation; there is no obsolete source name to retire.
                continue;
            }

            var sourceOpen = sourceParent.TryOpenExistingFileWithOutcome(
                Path.GetFileName(sourceSiblingMarker),
                requireDeleteAccess: true,
                out var openedSourceMarker);
            if (sourceOpen == PinnedFileOpenOutcome.NotFound)
            {
                ValidateRetirementTarget(plan, targetParent, targetMarker);
                continue;
            }
            if (sourceOpen == PinnedFileOpenOutcome.Unavailable
                || openedSourceMarker == null)
            {
                throw new IOException(
                    "The retired ownership marker is temporarily unavailable; its migration journal was preserved for retry.");
            }

            using var sourceMarker = openedSourceMarker;
            if (sourceMarker.IdentifiesSameEntry(targetMarker))
            {
                if (!sourceParent.VisiblePathMatches()
                    || !targetParent.VisiblePathMatches()
                    || !sourceMarker.VisiblePathMatches()
                    || !targetMarker.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "A shared ownership marker generation changed before source-name retirement.");
                }

                sourceMarker.Delete();
                sourceParent.FlushDirectoryEntry();
                ValidateRetirementTarget(plan, targetParent, targetMarker);
                continue;
            }

            LibraryDirectoryOwnershipMarker.ValidateMarkerFile(
                plan.Source,
                sourceMarker);

            if (string.Equals(
                    sourceParent.GetDirectoryObjectIdentity(),
                    targetParent.GetDirectoryObjectIdentity(),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Equivalent ownership marker parents exposed different marker generations.");
            }

            if (!sourceParent.VisiblePathMatches()
                || !targetParent.VisiblePathMatches()
                || !sourceMarker.VisiblePathMatches()
                || !targetMarker.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "An ownership migration marker changed before source retirement.");
            }

            LibraryDirectoryOwnershipMarker.ValidateMarkerFile(
                plan.Source,
                sourceMarker);
            LibraryDirectoryOwnershipMarker.ValidateMarkerFile(
                plan.Target,
                targetMarker);
            sourceMarker.Delete();
            sourceParent.FlushDirectoryEntry();
            ValidateRetirementTarget(plan, targetParent, targetMarker);
        }
    }

    private static void ValidateRetirementTarget(
        OwnershipMigrationPlan plan,
        PinnedDirectoryCreation.PinnedDirectoryAnchor targetParent,
        PinnedDirectoryCreation.PinnedFileEntry targetMarker)
    {
        if (!targetParent.VisiblePathMatches()
            || !targetMarker.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The active ownership marker changed during source retirement.");
        }

        LibraryDirectoryOwnershipMarker.ValidateMarkerFile(
            plan.Target,
            targetMarker);
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenMarkerParentWithinBoundary(
            string boundaryPath,
            string parentPath,
            FileSystemPathSemantics semantics)
    {
        var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
            boundaryPath,
            semantics.Syntax);
        var canonicalParent = FileSystemPathIdentity.Canonicalize(
            parentPath,
            semantics.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                canonicalParent,
                canonicalBoundary,
                semantics))
        {
            throw new InvalidOperationException(
                "An ownership migration marker escaped its authorized root boundary.");
        }

        var current = PinnedDirectoryCreation.OpenPinnedBoundary(
            canonicalBoundary);
        try
        {
            if (FileSystemPathIdentity.AreEquivalent(
                    canonicalParent,
                    canonicalBoundary,
                    semantics))
            {
                return current;
            }

            var relative = Path.GetRelativePath(
                canonicalBoundary,
                canonicalParent);
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException(
                        "An ownership marker parent contains navigation segments.");
                }

                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static async Task<PinnedDirectoryCreation.PinnedDirectoryAnchor>
        OpenVerifiedMarkerParentWithinBoundaryAsync(
            string boundaryPath,
            string parentPath,
            FileSystemPathSemantics semantics,
            int? expectedBoundaryIdentityVersion = null,
            string? expectedBoundaryIdentityValue = null,
            string? boundaryIdentityUnavailableReason = null,
            CancellationToken cancellationToken = default)
    {
        var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
            boundaryPath,
            semantics.Syntax);
        var canonicalParent = FileSystemPathIdentity.Canonicalize(
            parentPath,
            semantics.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                canonicalParent,
                canonicalBoundary,
                semantics))
        {
            throw new InvalidOperationException(
                "An ownership migration marker escaped its authorized root boundary.");
        }

        var current = PinnedDirectoryCreation.OpenPinnedBoundary(
            canonicalBoundary);
        try
        {
            if (expectedBoundaryIdentityVersion.HasValue
                || !string.IsNullOrWhiteSpace(expectedBoundaryIdentityValue)
                || !string.IsNullOrWhiteSpace(boundaryIdentityUnavailableReason))
            {
                await ManagedDirectoryEnrollment.RequireMatchingEnrollmentAsync(
                    current,
                    expectedBoundaryIdentityVersion,
                    expectedBoundaryIdentityValue,
                    boundaryIdentityUnavailableReason,
                    cancellationToken);
            }

            if (FileSystemPathIdentity.AreEquivalent(
                    canonicalParent,
                    canonicalBoundary,
                    semantics))
            {
                return current;
            }

            var relative = Path.GetRelativePath(
                canonicalBoundary,
                canonicalParent);
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException(
                        "An ownership marker parent contains navigation segments.");
                }

                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }
}
