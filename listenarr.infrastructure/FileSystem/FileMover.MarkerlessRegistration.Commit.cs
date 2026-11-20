using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private bool CommitMarkerlessRegistration(
        Guid operationId,
        FileAction action,
        string? targetPhysicalObjectIdentity,
        int audiobookId,
        bool requirePhysicalIdentity)
    {
        var journal = _fileMutationJournalStore!.Get(operationId)
            ?? throw new InvalidOperationException(
                "The markerless registration journal no longer exists.");
        if (journal.ProtocolVersion != FileMutationProtocol.Current
            || journal.Action != action
            || (requirePhysicalIdentity
                && !string.Equals(
                    journal.TargetPhysicalObjectIdentity,
                    targetPhysicalObjectIdentity,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The markerless registration identity changed before commit.");
        }
        if (journal.State == FileMutationJournalState.NeedsAttention
            || journal.State == FileMutationJournalState.RollbackAuthorized
            || journal.State == FileMutationJournalState.RolledBack)
        {
            throw new InvalidOperationException(
                "A markerless registration requiring attention cannot be committed.");
        }
        var publicationMatch = ProbeMarkerlessJournalTarget(
            journal,
            targetPhysicalObjectIdentity,
            requirePhysicalIdentity);
        if (publicationMatch == RegistrationPublicationMatchOutcome.Mismatch)
        {
            _ = _fileMutationJournalStore.Advance(
                operationId,
                FileMutationJournalState.NeedsAttention,
                targetPhysicalObjectIdentity,
                audiobookId,
                "The registration destination changed before its journal commit.");
            return false;
        }
        if (journal.State != FileMutationJournalState.TargetVerified
            && journal.State != FileMutationJournalState.RegistrationCommitted
            && journal.State != FileMutationJournalState.SourceDeletionAuthorized
            && journal.State != FileMutationJournalState.SourceDeleted
            && journal.State != FileMutationJournalState.Completed
            && journal.State != FileMutationJournalState.CompletedSourceRetained)
        {
            throw new InvalidOperationException(
                "The markerless registration destination is not verified.");
        }

        RegistrationPublicationMatchOutcome ValidateCommitPublication()
        {
            var validation = ProbeMarkerlessJournalTarget(
                journal,
                targetPhysicalObjectIdentity,
                requirePhysicalIdentity);
            return validation == RegistrationPublicationMatchOutcome.Unavailable
                ? RegistrationPublicationMatchOutcome.Match
                : validation;
        }

        if (journal.State == FileMutationJournalState.TargetVerified)
        {
            var commitValidation =
                _fileMutationJournalStore.AdvanceWithCommitValidation(
                    operationId,
                    FileMutationJournalState.RegistrationCommitted,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    error: null,
                    ValidateCommitPublication);
            if (commitValidation == RegistrationPublicationMatchOutcome.Mismatch)
            {
                _ = _fileMutationJournalStore.Advance(
                    operationId,
                    FileMutationJournalState.NeedsAttention,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    "The registration destination changed while its durable owner commit was being recorded.");
                return false;
            }
            journal = _fileMutationJournalStore.Get(operationId)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared after owner commit.");
        }
        else if (!journal.AudiobookId.HasValue)
        {
            var ownerBindingValidation =
                _fileMutationJournalStore.AdvanceWithCommitValidation(
                    operationId,
                    journal.State,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    error: null,
                    ValidateCommitPublication);
            if (ownerBindingValidation == RegistrationPublicationMatchOutcome.Mismatch)
            {
                _ = _fileMutationJournalStore.Advance(
                    operationId,
                    FileMutationJournalState.NeedsAttention,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    "The registration destination changed while its durable owner binding was being recorded.");
                return false;
            }
            journal = _fileMutationJournalStore.Get(operationId)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared after owner binding.");
        }
        else if (journal.AudiobookId.Value != audiobookId)
        {
            throw new InvalidOperationException(
                "The markerless registration journal is committed to another audiobook.");
        }

        if (action != FileAction.Move
            && journal.State != FileMutationJournalState.Completed)
        {
            var completionValidation =
                _fileMutationJournalStore.AdvanceWithCommitValidation(
                    operationId,
                    FileMutationJournalState.Completed,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    error: null,
                    ValidateCommitPublication);
            if (completionValidation == RegistrationPublicationMatchOutcome.Mismatch)
            {
                _ = _fileMutationJournalStore.Advance(
                    operationId,
                    FileMutationJournalState.NeedsAttention,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    "The registration destination changed before publication completion could be committed.");
                return false;
            }
            journal = _fileMutationJournalStore.Get(operationId)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared after publication completion.");
        }

        return action == FileAction.Move
            ? FileMutationJournalLifecycle.MayRetireSource(journal.State)
                || journal.State == FileMutationJournalState.CompletedSourceRetained
            : journal.State == FileMutationJournalState.Completed;
    }
}
