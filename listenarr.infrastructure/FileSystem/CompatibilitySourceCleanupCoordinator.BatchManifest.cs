using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class CompatibilitySourceCleanupCoordinator
{
    private static bool HasPersistedBatchManifest(
        IReadOnlyCollection<CompatibilityFilePublicationJournal> journals) =>
        journals.Count > 0
        && journals.All(journal =>
            journal.ExpectedBatchMemberCount.HasValue
            && !string.IsNullOrWhiteSpace(
                journal.ExpectedBatchSourceManifestSha256));

    private static bool BatchManifestMatches(
        IReadOnlyCollection<CompatibilityFilePublicationJournal> journals)
    {
        var manifestJournals = journals
            .Where(journal =>
                journal.ExpectedBatchMemberCount.HasValue
                || !string.IsNullOrWhiteSpace(
                    journal.ExpectedBatchSourceManifestSha256))
            .ToList();
        if (manifestJournals.Count == 0)
        {
            // Released verified-cleanup journals predate persisted batch manifests.
            // Same-process completion remains compatible; startup recovery keeps
            // those older batches retain-only because it cannot prove completeness.
            return true;
        }
        if (manifestJournals.Count != journals.Count)
        {
            return false;
        }

        var first = manifestJournals[0];
        if (!first.ExpectedBatchMemberCount.HasValue
            || string.IsNullOrWhiteSpace(
                first.ExpectedBatchSourceManifestSha256))
        {
            return false;
        }

        var manifest = new CompatibilityBatchManifest(
            first.ExpectedBatchMemberCount.Value,
            first.ExpectedBatchSourceManifestSha256);
        try
        {
            manifest.Validate();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (manifestJournals.Any(journal =>
                journal.ExpectedBatchMemberCount != manifest.ExpectedMemberCount
                || !string.Equals(
                    journal.ExpectedBatchSourceManifestSha256,
                    manifest.SourceManifestSha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return manifest.Matches(journals.Select(journal => journal.SourcePath));
    }

    private async Task RetainBatchAsync(
        ListenArrDbContext context,
        IReadOnlyCollection<CompatibilityFilePublicationJournal> journals,
        CancellationToken cancellationToken)
    {
        foreach (var journal in journals.Where(journal =>
            journal.State == CompatibilityFilePublicationState.RegistrationCommitted))
        {
            journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
            journal.State = CompatibilityFilePublicationState.Completed;
            journal.Error = null;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
