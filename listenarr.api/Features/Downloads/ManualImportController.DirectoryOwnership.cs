using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private async Task<FilePublicationPreparationResult> PrepareOwnedManualImportActionForRegistrationAsync(
        FilePublicationPlan publicationPlan,
        string source,
        string destination,
        Audiobook audiobook,
        IReadOnlyCollection<RootFolder> rootFolders,
        FileSystemPathSemantics semantics,
        string fallbackBoundary,
        Guid operationId,
        string? expectedRegisteredPhysicalObjectIdentity,
        FilePublicationSourceProof expectedSourceProof,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "The manual import destination has no parent directory.");
        var boundary = LibraryDirectoryOwnershipPlanning.SelectMostSpecificBoundary(
            destinationDirectory,
            rootFolders.Select(root => root.Path),
            semantics);
        boundary ??= fallbackBoundary;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidOperationException(
                "The manual import destination has no managed ownership boundary.");
        }

        expectedSourceProof.Validate();

        if (publicationPlan.Mode
            == FilePublicationExecutionMode.AdditiveCopyRetainSource)
        {
            await _directoryOwnershipStore.EnsureAdditiveHierarchyAsync(
                destinationDirectory,
                boundary,
                semantics,
                cancellationToken);
        }
        else
        {
            await _directoryOwnershipStore.EnsureCreatedHierarchyAsync(
                destinationDirectory,
                boundary,
                semantics,
                "manual-import",
                operationId,
                audiobook.Id,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (publicationPlan.Mode == FilePublicationExecutionMode.Durable)
        {
            var lease = publicationPlan.EffectiveAction == FileAction.HardlinkCopy
                && !string.IsNullOrWhiteSpace(
                    expectedRegisteredPhysicalObjectIdentity)
                    ? await _fileMover.PrepareActionForRegistrationAsync(
                        publicationPlan.EffectiveAction,
                        source,
                        destination,
                        operationId,
                        expectedRegisteredPhysicalObjectIdentity,
                        expectedSourceProof)
                    : await _fileMover.PrepareActionForRegistrationAsync(
                        publicationPlan.EffectiveAction,
                        source,
                        destination,
                        operationId,
                        expectedRegisteredPhysicalObjectIdentity: null,
                        expectedSourceProof);
            return new FilePublicationPreparationResult(
                lease == null
                    ? FilePublicationOutcome.Blocked
                    : FilePublicationOutcome.Success,
                publicationPlan.RequestedAction,
                publicationPlan.EffectiveAction,
                publicationPlan.SourceDisposition,
                lease);
        }

        return await _fileMover.PrepareActionForRegistrationDetailedAsync(
            publicationPlan,
            source,
            destination,
            operationId,
            publicationPlan.EffectiveAction == FileAction.HardlinkCopy
                ? expectedRegisteredPhysicalObjectIdentity
                : null,
            expectedSourceProof);
    }
}
