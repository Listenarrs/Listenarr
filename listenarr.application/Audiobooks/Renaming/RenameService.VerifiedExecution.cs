using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private sealed record RenameExecutionPlan(
        bool UseVerifiedProtocol,
        Guid BatchId,
        VerifiedFileRenameBatchManifest? BatchManifest,
        IReadOnlyDictionary<int, FilePublicationSourceProof> SourceProofs)
    {
        public static RenameExecutionPlan Durable(
            IReadOnlyDictionary<int, FilePublicationSourceProof> sourceProofs) =>
            new(false, Guid.Empty, null, sourceProofs);
    }

    private sealed record RenameExecutionPlanningResult(
        RenameExecutionPlan? Plan,
        string? Error = null);

    private async Task<RenameExecutionPlanningResult> BuildRenameExecutionPlanAsync(
        Audiobook audiobook,
        RenameOperation operation,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var changed = (operation.FileRenames ?? [])
            .Where(file => !PathsEqual(
                NormalizePath(file.CurrentPath),
                NormalizePath(file.NewPath),
                semantics))
            .ToArray();
        if (changed.Length == 0)
        {
            return new RenameExecutionPlanningResult(
                RenameExecutionPlan.Durable(
                    new Dictionary<int, FilePublicationSourceProof>()));
        }

        var proofs = new Dictionary<int, FilePublicationSourceProof>();
        var members = new List<VerifiedFileRenameBatchMember>(changed.Length);
        var allMembersHaveDurableAuthority = true;
        foreach (var fileOperation in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveTrackedSourcePath(
                audiobook,
                fileOperation,
                semantics,
                out var databaseFile,
                out var trackedPathError);
            if (trackedPathError != null)
            {
                return new RenameExecutionPlanningResult(null, trackedPathError);
            }

            var destination = NormalizePath(fileOperation.NewPath);
            var capability = await _filePublicationSourceCapability.CheckAsync(
                source,
                cancellationToken);
            if (!capability.IsSupported || !capability.SourceProof.HasValue)
            {
                return new RenameExecutionPlanningResult(
                    null,
                    capability.Reason
                        ?? "The organize source cannot be verified safely.");
            }

            var proof = capability.SourceProof.Value;
            proof.Validate();
            proofs.Add(fileOperation.FileId, proof);
            members.Add(new VerifiedFileRenameBatchMember(
                fileOperation.FileId,
                source,
                destination));
            allMembersHaveDurableAuthority &=
                proof.HasDurablePhysicalObjectIdentity
                && (databaseFile == null
                    || !string.IsNullOrWhiteSpace(
                        databaseFile.PhysicalObjectIdentity));
        }

        if (allMembersHaveDurableAuthority)
        {
            return new RenameExecutionPlanningResult(
                RenameExecutionPlan.Durable(proofs));
        }

        var manifest = VerifiedFileRenameBatchManifest.Create(members);
        manifest.Validate();
        return new RenameExecutionPlanningResult(
            new RenameExecutionPlan(
                true,
                Guid.NewGuid(),
                manifest,
                proofs));
    }

    private static FilePublicationSourceProof? FindSourceProof(
        RenameExecutionPlan executionPlan,
        int fileId) =>
        executionPlan.SourceProofs.TryGetValue(fileId, out var proof)
            ? proof
            : null;

    private static async Task DisposeVerifiedRenameLeasesAsync(
        IEnumerable<FileRenameResultItem> items)
    {
        foreach (var item in items)
        {
            if (item.VerifiedRenameLease == null)
            {
                continue;
            }

            await item.VerifiedRenameLease.DisposeAsync();
            item.VerifiedRenameLease = null;
        }
    }

    private async Task<bool> CompleteVerifiedRenameSourceRetirementAsync(
        IEnumerable<FileRenameResultItem> items,
        CancellationToken cancellationToken)
    {
        var itemList = items.ToList();
        var requiresAttention = false;
        try
        {
            foreach (var item in itemList)
            {
                var lease = item.VerifiedRenameLease;
                if (lease == null)
                {
                    continue;
                }

                try
                {
                    var outcome = await lease.CompleteSourceRetirementAsync(
                        cancellationToken);
                    switch (outcome)
                    {
                        case VerifiedFileRenameRetirementOutcome.Completed:
                            break;
                        case VerifiedFileRenameRetirementOutcome.SourceRetained:
                            _logger.LogWarning(
                                "Verified organize operation {OperationId} committed owner metadata but retained the old source for file {FileId}",
                                lease.OperationId,
                                item.FileId);
                            break;
                        case VerifiedFileRenameRetirementOutcome.NeedsAttention:
                            requiresAttention = true;
                            item.Success = false;
                            item.Error =
                                "The organized destination changed after owner metadata committed and requires repair.";
                            _logger.LogError(
                                "Verified organize operation {OperationId} requires repair after owner metadata committed for file {FileId}",
                                lease.OperationId,
                                item.FileId);
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unknown verified organize retirement outcome '{outcome}'.");
                    }
                }
                finally
                {
                    await lease.DisposeAsync();
                    item.VerifiedRenameLease = null;
                }

                if (requiresAttention)
                {
                    break;
                }
            }

            return !requiresAttention;
        }
        finally
        {
            // The batch may stop on NeedsAttention or an unexpected lease failure.
            // Never leave later pinned handles alive after ExecuteRenameAsync returns.
            await DisposeVerifiedRenameLeasesAsync(itemList);
        }
    }
}
