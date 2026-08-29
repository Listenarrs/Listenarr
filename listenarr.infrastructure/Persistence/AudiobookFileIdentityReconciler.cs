using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed class AudiobookFileIdentityReconciler(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IAudiobookFilePathIdentityResolver identityResolver,
    ILogger<AudiobookFileIdentityReconciler> logger) : IAudiobookFileIdentityReconciler
{
    private const int BatchSize = 100;

    internal Action<string>? AfterPhysicalIdentityParentPinnedForTest { get; set; }

    public async Task<AudiobookFileIdentityReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await readContext.AudiobookFiles
            .AsNoTracking()
            .Include(file => file.Audiobook)
            .OrderBy(file => file.Id)
            .ToListAsync(cancellationToken);

        var plans = new List<ReconciliationPlan>(rows.Count);
        foreach (var file in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Audiobook == null)
            {
                plans.Add(ReconciliationPlan.Unavailable(
                    file,
                    "The owning audiobook could not be loaded."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                plans.Add(ReconciliationPlan.Unavailable(
                    file,
                    "The stored audiobook file path is missing."));
                continue;
            }

            try
            {
                var identity = await identityResolver.ResolveAsync(
                    file.Audiobook,
                    file.Path,
                    cancellationToken);
                var livePhysicalObjectIdentity = identity.State == PathIdentityState.Valid
                    ? TryResolvePhysicalObjectIdentity(
                        identity,
                        file.PhysicalObjectIdentity)
                    : null;
                var physicalDisposition = PhysicalGenerationDisposition.None;
                if (identity.State == PathIdentityState.Valid)
                {
                    if (string.IsNullOrWhiteSpace(livePhysicalObjectIdentity))
                    {
                        physicalDisposition = PhysicalGenerationDisposition.Unavailable;
                    }
                    else if (!string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)
                        && !string.Equals(
                            file.PhysicalObjectIdentity,
                            livePhysicalObjectIdentity,
                            StringComparison.Ordinal))
                    {
                        physicalDisposition = PhysicalGenerationDisposition.Mismatch;
                    }
                    else
                    {
                        physicalDisposition = PhysicalGenerationDisposition.Verified;
                    }
                }

                // A physical-generation mismatch must not erase pathname ownership.
                // The persisted physical token remains stale on purpose so destructive
                // workflows continue to fail closed when they compare it with the live
                // generation, while the path lookup/ownership keys still fence claims.
                plans.Add(new ReconciliationPlan(
                    file.Id,
                    file.Path,
                    file.PathOwnershipKey,
                    identity,
                    physicalObjectIdentity: string.IsNullOrWhiteSpace(
                            file.PhysicalObjectIdentity)
                        && physicalDisposition == PhysicalGenerationDisposition.Verified
                            ? livePhysicalObjectIdentity
                            : null,
                    physicalDisposition: physicalDisposition));
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                InvalidOperationException or
                NotSupportedException or
                PathTooLongException or
                System.Security.SecurityException)
            {
                logger.LogWarning(
                    exception,
                    "Audiobook file identity is unavailable for file {AudiobookFileId}",
                    file.Id);
                plans.Add(ReconciliationPlan.Unavailable(
                    file,
                    exception.Message));
            }
        }

        MarkDuplicateIdentities(plans);
        await ClearChangedOwnershipKeysAsync(plans, cancellationToken);
        await ApplyPlansAsync(plans, cancellationToken);

        var valid = plans.Count(plan => plan.Identity?.State == PathIdentityState.Valid);
        var conflicted = plans.Count(plan => plan.Identity?.State == PathIdentityState.Conflict);
        var unavailable = plans.Count - valid - conflicted;
        var physicalVerified = plans.Count(
            plan => plan.PhysicalDisposition == PhysicalGenerationDisposition.Verified);
        var physicalUnavailable = plans.Count(
            plan => plan.PhysicalDisposition == PhysicalGenerationDisposition.Unavailable);
        var physicalMismatch = plans.Count(
            plan => plan.PhysicalDisposition == PhysicalGenerationDisposition.Mismatch);
        logger.LogInformation(
            "Reconciled {Processed} audiobook file path identities: {Valid} valid, {Conflicted} conflicted, {Unavailable} unavailable; physical generations: {PhysicalVerified} verified, {PhysicalUnavailable} unavailable, {PhysicalMismatch} mismatched",
            plans.Count,
            valid,
            conflicted,
            unavailable,
            physicalVerified,
            physicalUnavailable,
            physicalMismatch);
        return new AudiobookFileIdentityReconciliationResult(
            plans.Count,
            valid,
            conflicted,
            unavailable);
    }

    private static void MarkDuplicateIdentities(List<ReconciliationPlan> plans)
    {
        foreach (var group in plans
            .Where(plan => plan.Identity is
            {
                State: PathIdentityState.Valid,
                OwnershipKey: not null
            })
            .GroupBy(plan => plan.Identity!.OwnershipKey!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            var fileIds = string.Join(", ", group.Select(plan => plan.FileId));
            foreach (var plan in group)
            {
                plan.Identity = plan.Identity! with
                {
                    OwnershipKey = null,
                    State = PathIdentityState.Conflict,
                    Reason = $"Multiple audiobook file rows claim the same filesystem identity: {fileIds}."
                };
            }
        }
    }

    private async Task ClearChangedOwnershipKeysAsync(
        IReadOnlyList<ReconciliationPlan> plans,
        CancellationToken cancellationToken)
    {
        var ids = plans
            .Where(plan => !string.IsNullOrWhiteSpace(plan.CurrentOwnershipKey)
                && !string.Equals(
                    plan.CurrentOwnershipKey,
                    plan.Identity?.OwnershipKey,
                    StringComparison.Ordinal))
            .Select(plan => plan.FileId)
            .ToArray();
        foreach (var batch in ids.Chunk(BatchSize))
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var files = await context.AudiobookFiles
                .Where(file => batch.Contains(file.Id))
                .ToListAsync(cancellationToken);
            foreach (var file in files)
            {
                file.PreparePathIdentityReconciliation(
                    "Audiobook file identity reconciliation was interrupted before the replacement identity was persisted.");
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ApplyPlansAsync(
        IReadOnlyList<ReconciliationPlan> plans,
        CancellationToken cancellationToken)
    {
        foreach (var batch in plans.Chunk(BatchSize))
        {
            var planById = batch.ToDictionary(plan => plan.FileId);
            var ids = planById.Keys.ToArray();
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var files = await context.AudiobookFiles
                .Where(file => ids.Contains(file.Id))
                .ToListAsync(cancellationToken);
            foreach (var file in files)
            {
                var plan = planById[file.Id];
                if (plan.Identity == null)
                {
                    file.MarkPathIdentityUnavailable(plan.StoredPath, plan.UnavailableReason!);
                }
                else
                {
                    file.ApplyPathIdentity(plan.StoredPath!, plan.Identity);
                    if (string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)
                        && !string.IsNullOrWhiteSpace(plan.PhysicalObjectIdentity))
                    {
                        file.ApplyPhysicalObjectIdentity(
                            plan.PhysicalObjectIdentity,
                            DateTime.UtcNow);
                    }
                }
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private string? TryResolvePhysicalObjectIdentity(
        AudiobookFilePathIdentity identity,
        string? expectedPhysicalObjectIdentity)
    {
        var pathIdentity = new PathIdentitySnapshot(
            identity.Syntax,
            identity.CaseSensitivity,
            identity.RequestedMode,
            identity.BoundaryPath);
        if (identity.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(identity.CanonicalPath)
            || !FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                identity.CanonicalPath,
                pathIdentity,
                out var canonicalPath,
                out _))
        {
            return null;
        }

        try
        {
            var parentPath = Path.GetDirectoryName(canonicalPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return null;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            AfterPhysicalIdentityParentPinnedForTest?.Invoke(parentPath);
            using var file = parent.OpenExistingFileForStableRead(
                Path.GetFileName(canonicalPath));
            if (!parent.VisiblePathMatches() || !file.VisiblePathMatches())
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity)
                && file.MatchesObjectIdentity(expectedPhysicalObjectIdentity))
            {
                return expectedPhysicalObjectIdentity;
            }

            return file.GetObjectIdentity();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(
                exception,
                "Physical identity could not be backfilled during audiobook file reconciliation");
            return null;
        }
    }

    private sealed class ReconciliationPlan(
        int fileId,
        string? storedPath,
        string? currentOwnershipKey,
        AudiobookFilePathIdentity? identity,
        string? unavailableReason = null,
        string? physicalObjectIdentity = null,
        PhysicalGenerationDisposition physicalDisposition = PhysicalGenerationDisposition.None)
    {
        public int FileId { get; } = fileId;
        public string? StoredPath { get; } = storedPath;
        public string? CurrentOwnershipKey { get; } = currentOwnershipKey;
        public AudiobookFilePathIdentity? Identity { get; set; } = identity;
        public string? UnavailableReason { get; } = unavailableReason;
        public string? PhysicalObjectIdentity { get; } = physicalObjectIdentity;
        public PhysicalGenerationDisposition PhysicalDisposition { get; } = physicalDisposition;

        public static ReconciliationPlan Unavailable(
            AudiobookFile file,
            string reason) =>
            new(
                file.Id,
                file.Path,
                file.PathOwnershipKey,
                null,
                reason);
    }

    private enum PhysicalGenerationDisposition
    {
        None,
        Verified,
        Unavailable,
        Mismatch
    }
}
