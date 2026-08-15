using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private const char MetadataSkipReasonSeparator = '|';
    private const string InvalidStoredMetadataPathReason =
        "Stored audiobook base path is invalid or case-ambiguous and could not be compared safely with the source root.";
    private const string SourceSemanticsUnavailableReason =
        "Stored source path semantics are unavailable for this audiobook.";
    private const string TargetIdentityCollisionReason =
        "Stored file paths for this audiobook would collapse to the same filesystem identity on the selected destination.";
    private const string TargetIdentityUnresolvedConflictReason =
        "A stored file identity at the selected destination is unresolved, so this audiobook cannot claim the target path safely.";

    private sealed record MetadataRewritePlan(
        AudiobookPathCandidate Candidate,
        string Destination);

    private sealed record MetadataSkipReason(
        RootFolderRelocationSkipReasonCode Code,
        string Reason);

    private sealed record MetadataRewritePlanningResult(
        IReadOnlyList<MetadataRewritePlan> SafePlans,
        IReadOnlyList<RootFolderRelocationSkippedItem> SkippedItems);

    private sealed record MetadataRewriteSnapshot(
        Audiobook Audiobook,
        string? BasePath,
        string? FilePath,
        string? ImageUrl,
        IReadOnlyList<(
            AudiobookFile File,
            AudiobookFilePathState State,
            string? PhysicalObjectIdentity,
            DateTime? PhysicalIdentityObservedAtUtc)> Files);

    private static MetadataRewritePlanningResult PlanMetadataPathRewrites(
        ListenArrDbContext db,
        IReadOnlyList<AudiobookPathCandidate> affected,
        string sourceRootPath,
        string targetRootPath,
        FileSystemPathSemantics? sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemCaseSensitivityMode targetMode,
        DateTimeOffset now)
    {
        var skippedReasons = new Dictionary<int, MetadataSkipReason>();
        var mappedPlans = new List<MetadataRewritePlan>(affected.Count);
        if (!sourceSemantics.HasValue)
        {
            foreach (var candidate in affected)
            {
                skippedReasons[candidate.Audiobook.Id] = new MetadataSkipReason(
                    RootFolderRelocationSkipReasonCode.SourceSemanticsUnavailable,
                    SourceSemanticsUnavailableReason);
            }

            return new MetadataRewritePlanningResult(
                [],
                CreateSkippedMetadataItems(skippedReasons, now));
        }

        foreach (var candidate in affected)
        {
            try
            {
                mappedPlans.Add(new MetadataRewritePlan(
                    candidate,
                    MapTargetPath(
                        sourceRootPath,
                        targetRootPath,
                        candidate.StoredBasePath,
                        sourceSemantics.Value,
                        targetSemantics)));
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                skippedReasons[candidate.Audiobook.Id] = new MetadataSkipReason(
                    RootFolderRelocationSkipReasonCode.TargetPathInvalid,
                    exception.Message);
            }
        }

        var snapshots = mappedPlans.ToDictionary(
            plan => plan.Candidate.Audiobook.Id,
            plan => CaptureMetadataRewriteSnapshot(plan.Candidate.Audiobook));
        var provisionallySafe = new List<MetadataRewritePlan>(mappedPlans.Count);
        try
        {
            foreach (var plan in mappedPlans)
            {
                try
                {
                    AudiobookPathReferenceRewriter.Rewrite(
                        plan.Candidate.Audiobook,
                        plan.Candidate.StoredBasePath,
                        plan.Destination,
                        sourceSemantics.Value,
                        targetSemantics,
                        targetMode);
                    provisionallySafe.Add(plan);
                }
                catch (AudiobookPathRewriteException exception)
                {
                    skippedReasons[plan.Candidate.Audiobook.Id] = new MetadataSkipReason(
                        RootFolderRelocationSkipReasonCode.TargetPathInvalid,
                        exception.Message);
                    RestoreMetadataRewriteSnapshot(
                        snapshots[plan.Candidate.Audiobook.Id]);
                }
            }

            var provisionalAudiobookIds = provisionallySafe
                .Select(plan => plan.Candidate.Audiobook.Id)
                .ToHashSet();
            var collisionAudiobookIds = db.ChangeTracker
                .Entries<AudiobookFile>()
                .Select(entry => entry.Entity)
                .Where(file =>
                    file.PathIdentityState == PathIdentityState.Valid
                    && !string.IsNullOrWhiteSpace(file.PathOwnershipKey))
                .GroupBy(file => file.PathOwnershipKey!, StringComparer.Ordinal)
                .Where(group =>
                    group.Select(file => file.Id).Distinct().Count() > 1)
                .SelectMany(group => group.Select(file => file.AudiobookId))
                .Where(provisionalAudiobookIds.Contains)
                .ToHashSet();

            foreach (var audiobookId in collisionAudiobookIds)
            {
                skippedReasons[audiobookId] = new MetadataSkipReason(
                    RootFolderRelocationSkipReasonCode.TargetIdentityCollision,
                    TargetIdentityCollisionReason);
            }

            var allTrackedFiles = db.ChangeTracker
                .Entries<AudiobookFile>()
                .Select(entry => entry.Entity)
                .ToArray();
            var unresolvedByLookupKey = allTrackedFiles
                .Where(file =>
                    file.PathIdentityState != PathIdentityState.Valid
                    && !string.IsNullOrWhiteSpace(file.PathIdentityLookupKey))
                .GroupBy(file => file.PathIdentityLookupKey!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var file in allTrackedFiles.Where(file =>
                provisionalAudiobookIds.Contains(file.AudiobookId)
                && file.PathIdentityState == PathIdentityState.Valid
                && !string.IsNullOrWhiteSpace(file.PathIdentityLookupKey)))
            {
                if (!unresolvedByLookupKey.TryGetValue(
                        file.PathIdentityLookupKey!,
                        out var unresolvedCandidates)
                    || !unresolvedCandidates.Any(candidate =>
                        candidate.Id != file.Id
                        && AudiobookFileOwnershipValidator.UnresolvedIdentityOverlaps(
                            candidate,
                            file.PathSyntax!.Value,
                            file.PathCaseSensitivity,
                            file.CanonicalPath!)))
                {
                    continue;
                }

                skippedReasons[file.AudiobookId] = new MetadataSkipReason(
                    RootFolderRelocationSkipReasonCode.TargetIdentityUnresolvedConflict,
                    TargetIdentityUnresolvedConflictReason);
            }

            var safePlans = provisionallySafe
                .Where(plan => !skippedReasons.ContainsKey(plan.Candidate.Audiobook.Id))
                .ToArray();
            return new MetadataRewritePlanningResult(
                safePlans,
                CreateSkippedMetadataItems(skippedReasons, now));
        }
        finally
        {
            foreach (var snapshot in snapshots.Values)
            {
                RestoreMetadataRewriteSnapshot(snapshot);
            }
        }
    }

    private static MetadataRewriteSnapshot CaptureMetadataRewriteSnapshot(
        Audiobook audiobook) =>
        new(
            audiobook,
            audiobook.BasePath,
            audiobook.FilePath,
            audiobook.ImageUrl,
            (audiobook.Files ?? [])
                .Select(file => (
                    file,
                    file.CapturePathState(),
                    file.PhysicalObjectIdentity,
                    file.PhysicalIdentityObservedAtUtc))
                .ToArray());

    private static void RestoreMetadataRewriteSnapshot(
        MetadataRewriteSnapshot snapshot)
    {
        snapshot.Audiobook.BasePath = snapshot.BasePath;
        snapshot.Audiobook.FilePath = snapshot.FilePath;
        snapshot.Audiobook.ImageUrl = snapshot.ImageUrl;
        foreach (var (
            file,
            state,
            physicalObjectIdentity,
            physicalIdentityObservedAtUtc) in snapshot.Files)
        {
            file.RestorePathState(state);
            if (physicalObjectIdentity != null
                && physicalIdentityObservedAtUtc.HasValue)
            {
                var observedAtUtc = physicalIdentityObservedAtUtc.Value.Kind == DateTimeKind.Utc
                    ? physicalIdentityObservedAtUtc.Value
                    : DateTime.SpecifyKind(
                        physicalIdentityObservedAtUtc.Value,
                        DateTimeKind.Utc);
                file.ApplyPhysicalObjectIdentity(
                    physicalObjectIdentity,
                    observedAtUtc);
            }
            else
            {
                file.ClearPhysicalObjectIdentity();
            }
        }
    }

    private static IReadOnlyList<RootFolderRelocationSkippedItem>
        CreateSkippedMetadataItems(
            IReadOnlyDictionary<int, MetadataSkipReason> skippedReasons,
            DateTimeOffset now) =>
        skippedReasons
            .OrderBy(pair => pair.Key)
            .Select(pair => new RootFolderRelocationSkippedItem
            {
                AudiobookId = pair.Key,
                Reason = EncodeMetadataSkipReason(pair.Value.Code, pair.Value.Reason),
                CreatedAt = now
            })
            .ToArray();

    private static string EncodeMetadataSkipReason(
        RootFolderRelocationSkipReasonCode code,
        string reason) =>
        $"{code}{MetadataSkipReasonSeparator}{reason}";

    private static bool IsRepairableMetadataSkipReason(
        RootFolderRelocationSkipReasonCode code) =>
        code is RootFolderRelocationSkipReasonCode.TargetIdentityCollision
            or RootFolderRelocationSkipReasonCode.TargetIdentityUnresolvedConflict;

    private static RootFolderRelocationSkipReasonCode ClassifyMetadataSkipReason(
        string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var separator = reason.IndexOf(MetadataSkipReasonSeparator);
            if (separator > 0
                && Enum.TryParse<RootFolderRelocationSkipReasonCode>(
                    reason[..separator],
                    ignoreCase: false,
                    out var persistedCode)
                && Enum.IsDefined(persistedCode))
            {
                return persistedCode;
            }
        }

        return RootFolderRelocationSkipReasonCode.Unknown;
    }

    private static bool TryResolvePersistedRelocationSourceSemantics(
        RootFolderRelocation relocation,
        out FileSystemPathSemantics semantics,
        out string reason)
    {
        FileSystemPathSyntax syntax;
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                relocation.SourcePath,
                out syntax))
        {
            if (relocation.Mode != RootFolderRelocationMode.MetadataOnly
                || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    relocation.TargetPath,
                    out var targetSyntax)
                || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    relocation.SourcePath,
                    targetSyntax,
                    out syntax))
            {
                semantics = default;
                reason = "The persisted relocation source path syntax is unavailable.";
                return false;
            }
        }

        var sensitivity = relocation.SourceCaseSensitivityMode switch
        {
            FileSystemCaseSensitivityMode.Sensitive =>
                FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Insensitive =>
                FileSystemCaseSensitivity.Insensitive,
            _ => FileSystemCaseSensitivity.Unknown
        };
        if (sensitivity == FileSystemCaseSensitivity.Unknown)
        {
            semantics = default;
            reason = "The persisted relocation source case sensitivity is unavailable.";
            return false;
        }

        semantics = new FileSystemPathSemantics(syntax, sensitivity);
        reason = string.Empty;
        return true;
    }
}
