using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Listenarr.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    public async Task<IReadOnlyList<FileRegistrationReview>> ListReviewsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // List metadata only: hashing a large library is an explicit per-operation review.
        var journals = await db.FileMutationJournals.AsNoTracking()
            .Where(RegistrationPublicationPredicate)
            .Where(j => j.State == FileMutationJournalState.NeedsAttention
                || j.State == FileMutationJournalState.OperatorResolved)
            .OrderBy(j => j.State == FileMutationJournalState.OperatorResolved)
            .ThenByDescending(j => j.UpdatedAt).Take(200).ToListAsync(cancellationToken);
        return journals.Select(j => Describe(j, new("NotInspected", null, null),
            new("NotInspected", null, null), [], "Completeness cannot be verified", "")).ToList();
    }

    public async Task<FileRegistrationReview> InspectAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var journal = await LoadRegistrationPublicationAsync(operationId, cancellationToken);
        return await InspectJournalAsync(journal, cancellationToken);
    }

    private async Task<FileRegistrationReview> InspectJournalAsync(FileMutationJournal journal, CancellationToken cancellationToken)
    {
        var source = await ObserveFileAsync(journal.SourcePath, cancellationToken);
        var destination = await ObserveFileAsync(journal.DestinationPath, cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Include competing owners as well as the expected owner. These are diagnostic
        // exact-path matches, never authority to adopt or delete another owner's file.
        var files = await db.AudiobookFiles.AsNoTracking()
            .Where(f => f.AudiobookId == journal.AudiobookId
                || f.CanonicalPath == journal.DestinationPath || f.Path == journal.DestinationPath)
            .OrderBy(f => f.Id).ToListAsync(cancellationToken);
        var registrations = files.Select(f => new RegistrationTrackedFile(f.Id, f.AudiobookId,
            f.CanonicalPath ?? f.Path)).ToList();
        var evidence = string.IsNullOrWhiteSpace(journal.SourceSha256) || destination.Sha256 == null
            ? "Completeness cannot be verified"
            : destination.Length == journal.SourceLength
                && string.Equals(destination.Sha256, journal.SourceSha256, StringComparison.OrdinalIgnoreCase)
                ? "Destination matches this journal's file evidence; whole-book completeness cannot be verified"
                : "Destination does not match this journal's file evidence; completeness cannot be verified";
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { journal, source, destination, registrations }))));
        return Describe(journal, source, destination, registrations, evidence, token);
    }

    private static FileRegistrationReview Describe(FileMutationJournal journal,
        RegistrationFileObservation source, RegistrationFileObservation destination,
        IReadOnlyList<RegistrationTrackedFile> registrations, string evidence, string token) =>
        new(journal.OperationId, journal.AudiobookId, journal.Action.ToString(), journal.ProtocolVersion,
            journal.State.ToString(), journal.Error, journal.SourcePath, journal.DestinationPath,
            source, destination, registrations, evidence,
            journal.State == FileMutationJournalState.NeedsAttention, token,
            journal.ResolutionNote, journal.ResolvedBy, journal.ResolvedAtUtc);

    public async Task<FileRegistrationReview> ResolveAsync(Guid operationId, string reviewToken, string note,
        bool acknowledgeIncompleteContent, string actor, CancellationToken cancellationToken = default)
    {
        if (!acknowledgeIncompleteContent || string.IsNullOrWhiteSpace(note) || note.Trim().Length > 2000
            || string.IsNullOrWhiteSpace(reviewToken) || reviewToken.Length != 64
            || string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
            throw new ArgumentException("Confirm the incomplete-content warning and provide a review token and a note (1–2000 characters).");

        var journal = await LoadRegistrationPublicationAsync(operationId, cancellationToken);
        if (journal.State == FileMutationJournalState.OperatorResolved)
        {
            if (IsResolutionReplay(journal, reviewToken, note.Trim()))
                return await InspectJournalAsync(journal, cancellationToken);
            throw StaleReview();
        }
        if (journal.State != FileMutationJournalState.NeedsAttention)
            throw StaleReview();
        var review = await InspectJournalAsync(journal, cancellationToken);
        if (!string.Equals(review.ReviewToken, reviewToken, StringComparison.Ordinal))
            throw StaleReview();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var evidence = JsonSerializer.Serialize(review);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // One conditional write persists the audit and releases only this boundary.
        // No file mutation, registration, receipt, or successful import is produced.
        var affected = await db.FileMutationJournals.Where(j => j.OperationId == operationId
                && j.State == FileMutationJournalState.NeedsAttention && j.UpdatedAt == journal.UpdatedAt
                && j.AudiobookId == journal.AudiobookId && j.AudiobookFileId == null
                && j.ProtocolVersion == journal.ProtocolVersion && j.Action == journal.Action
                && j.SourcePath == journal.SourcePath && j.DestinationPath == journal.DestinationPath
                && j.SourceLength == journal.SourceLength && j.SourceSha256 == journal.SourceSha256
                && j.SourcePhysicalObjectIdentity == journal.SourcePhysicalObjectIdentity
                && j.TargetPhysicalObjectIdentity == journal.TargetPhysicalObjectIdentity
                && j.SourceParentDirectoryObjectIdentity == journal.SourceParentDirectoryObjectIdentity
                && j.DestinationParentDirectoryObjectIdentity == journal.DestinationParentDirectoryObjectIdentity
                && j.Error == journal.Error)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, FileMutationJournalState.OperatorResolved)
                .SetProperty(j => j.ResolutionNote, note.Trim()).SetProperty(j => j.ResolvedBy, actor)
                .SetProperty(j => j.ResolvedAtUtc, now).SetProperty(j => j.ResolutionReviewJson, evidence)
                .SetProperty(j => j.UpdatedAt, now), cancellationToken);
        if (affected != 1)
        {
            var current = await LoadRegistrationPublicationAsync(operationId, cancellationToken);
            if (!IsResolutionReplay(current, reviewToken, note.Trim())) throw StaleReview();
        }
        return await InspectAsync(operationId, cancellationToken);
    }

    private static bool IsResolutionReplay(FileMutationJournal journal, string token, string note) =>
        journal.State == FileMutationJournalState.OperatorResolved
        && journal.ResolutionNote == note
        && JsonSerializer.Deserialize<FileRegistrationReview>(journal.ResolutionReviewJson ?? "null")?.ReviewToken == token;

    private static ApplicationConflictException StaleReview() => new("registration_review_stale",
        "This operation or its observed files changed. Inspect it again before releasing the recovery block.");

    private static async Task<RegistrationFileObservation> ObserveFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || string.IsNullOrEmpty(Path.GetFileName(path)))
                return new("Unavailable", null, null);
            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                Path.GetDirectoryName(path)!, createMissing: false);
            var outcome = parent.TryOpenExistingFileWithOutcome(Path.GetFileName(path),
                requireDeleteAccess: false, out var opened);
            using var file = opened;
            if (outcome == PinnedFileOpenOutcome.Unavailable) return new("Unavailable", null, null);
            if (file == null) return new("Missing", null, null);
            if (!file.IsRegularFile() || !parent.VisiblePathMatches() || !file.VisiblePathMatches())
                return new("Unavailable", null, null);
            var observation = FilePublicationObservation.Capture(file);
            var modified = file.GetLastWriteTimeUtc();
            await using var stream = file.OpenReadStream(bufferSize: 81920, asynchronous: false);
            var length = stream.Length;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (length != stream.Length || file.GetLastWriteTimeUtc() != modified
                || !observation.Matches(file) || !parent.VisiblePathMatches() || !file.VisiblePathMatches())
                return new("ChangedDuringInspection", null, null);
            return new("Present", length, hash, observation.ObjectIdentity, modified);
        }
        catch (Exception e) when (FileSystemSafety.IsProvenMissingPathException(e))
        { return new("Missing", null, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception
            or System.Security.SecurityException)
        { return new("Unavailable", null, null); }
    }
}
