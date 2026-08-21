namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    public async Task<FilePublicationPreparationResult>
        PrepareActionForRegistrationDetailedAsync(
            FilePublicationPlan plan,
            string source,
            string destination,
            Guid operationId,
            string? expectedRegisteredPhysicalObjectIdentity,
            FilePublicationSourceProof expectedSourceProof,
            bool isCompanionFile = false,
            int? companionAudiobookId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        expectedSourceProof.Validate();
        if (isCompanionFile != companionAudiobookId.HasValue
            || companionAudiobookId <= 0)
        {
            throw new ArgumentException(
                "Untracked companion publication requires a positive audiobook owner, and non-companion publication must not provide one.",
                nameof(companionAudiobookId));
        }
        if (!plan.IsAllowed)
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                plan.RequestedAction,
                source,
                destination,
                plan.Message);
            return new FilePublicationPreparationResult(
                FilePublicationOutcome.Blocked,
                plan.RequestedAction,
                plan.EffectiveAction,
                plan.SourceDisposition,
                ReasonCode: plan.ReasonCode,
                Message: plan.Message);
        }

        if (plan.Mode == FilePublicationExecutionMode.AdditiveCopyRetainSource)
        {
            return await PrepareCompatibilityActionForRegistrationAsync(
                plan,
                source,
                destination,
                operationId,
                expectedRegisteredPhysicalObjectIdentity,
                expectedSourceProof,
                isCompanionFile);
        }

        if (!expectedSourceProof.HasDurablePhysicalObjectIdentity)
        {
            const string message =
                "Durable publication requires a durable source object identity.";
            LogMutation(
                FileMutationOutcome.Blocked,
                plan.RequestedAction,
                source,
                destination,
                message);
            return new FilePublicationPreparationResult(
                FilePublicationOutcome.Blocked,
                plan.RequestedAction,
                plan.EffectiveAction,
                plan.SourceDisposition,
                ReasonCode: "durable_source_identity_unavailable",
                Message: message);
        }

        var lease = await PrepareActionForRegistrationCoreAsync(
            plan.EffectiveAction,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity,
            expectedSourceProof,
            isCompanionFile,
            companionAudiobookId);
        return new FilePublicationPreparationResult(
            lease == null
                ? FilePublicationOutcome.Blocked
                : FilePublicationOutcome.Success,
            plan.RequestedAction,
            plan.EffectiveAction,
            plan.SourceDisposition,
            lease,
            lease == null ? "durable_publication_blocked" : null,
            lease == null
                ? "The durable publication could not be prepared safely."
                : plan.Message);
    }
}
