using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static void ValidateExpectedCurrentPath(
        RootFolderPathChangeCommand command,
        RootFolder root)
    {
        if (!string.IsNullOrWhiteSpace(command.ExpectedCurrentPath)
            && !string.Equals(
                root.Path,
                command.ExpectedCurrentPath,
                StringComparison.Ordinal))
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_changed_while_editing",
                "This root folder changed while you were editing it. Refresh the root folder and try the path change again.",
                "The root folder path changed after the relocation was confirmed.");
        }
    }

    private static string MapTargetPath(
        string sourceRoot,
        string targetRoot,
        string sourcePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
            sourceRoot,
            sourcePath,
            sourceSemantics,
            out var relativePath))
        {
            throw new InvalidOperationException("An audiobook path escaped its configured root.");
        }

        if (relativePath.Length == 0)
        {
            return targetRoot;
        }

        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
            targetRoot,
            FileSystemPathIdentity.ConvertRelativePathSyntax(
                relativePath,
                sourceSemantics.Syntax,
                targetSemantics.Syntax),
            targetSemantics,
            out var targetPath))
        {
            throw new InvalidOperationException("An audiobook path is invalid for the target root.");
        }

        return targetPath;
    }

    private sealed record AudiobookPathCandidate(Audiobook Audiobook, string StoredBasePath);

    private sealed record RelocationMovePlan(
        AudiobookPathCandidate Candidate,
        MoveSourceManifest Manifest,
        string RequestedPath,
        PathIdentitySnapshot TargetIdentity);

    private static (
        List<AudiobookPathCandidate> Affected,
        List<AudiobookPathCandidate> InvalidStoredBasePaths) DiscoverAffectedAudiobooks(
        IEnumerable<AudiobookPathCandidate> audiobooks,
        string sourceRootPath,
        FileSystemPathSemantics sourceSemantics,
        bool detectAmbiguousCaseMatches,
        bool allowContextualAmbiguousSyntax = false)
    {
        var affected = new List<AudiobookPathCandidate>();
        var invalidStoredBasePaths = new List<AudiobookPathCandidate>();

        foreach (var audiobook in audiobooks)
        {
            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    audiobook.StoredBasePath,
                    out var storedSyntax))
            {
                if (!allowContextualAmbiguousSyntax
                    || !audiobook.StoredBasePath.StartsWith("//", StringComparison.Ordinal)
                    || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                        audiobook.StoredBasePath,
                        sourceSemantics.Syntax,
                        out storedSyntax))
                {
                    // A syntactically unresolvable BasePath cannot be attributed to
                    // this root safely. Do not claim unrelated broken metadata as a
                    // skipped item for this relocation.
                    continue;
                }
            }

            if (storedSyntax != sourceSemantics.Syntax)
            {
                continue;
            }

            string canonicalStoredBasePath;
            try
            {
                canonicalStoredBasePath = FileSystemPathIdentity.Canonicalize(
                    audiobook.StoredBasePath,
                    storedSyntax);
            }
            catch (ArgumentException)
            {
                // Canonicalization failure leaves root ownership unknown. Preserve
                // the audiobook unchanged without claiming it for this relocation.
                continue;
            }

            try
            {
                if (FileSystemPathIdentity.IsSameOrInside(
                    canonicalStoredBasePath,
                    sourceRootPath,
                    sourceSemantics))
                {
                    affected.Add(audiobook);
                    continue;
                }

                if (detectAmbiguousCaseMatches
                    && FileSystemPathIdentity.IsSameOrInside(
                        canonicalStoredBasePath,
                        sourceRootPath,
                        new FileSystemPathSemantics(
                            sourceSemantics.Syntax,
                            FileSystemCaseSensitivity.Insensitive)))
                {
                    invalidStoredBasePaths.Add(audiobook);
                }
            }
            catch (ArgumentException)
            {
                // Boundary comparison failure is not evidence that this audiobook
                // belongs to the relocating root.
            }
        }

        return (affected, invalidStoredBasePaths);
    }

    private static bool PathTouchesBoundary(
        string? path,
        string boundaryPath,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.IsSameOrInside(path, boundaryPath, semantics)
                || FileSystemPathIdentity.IsSameOrInside(boundaryPath, path, semantics);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task FinalizeCompletedRelocationAsync(
        ListenArrDbContext db,
        RootFolderRelocation relocation,
        RootFolder root,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                relocation.TargetPath,
                out var canonicalTargetPath,
                out _))
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Target filesystem identity became unavailable during finalization.";
            return;
        }

        var resolution = await semanticsResolver.ResolveAsync(
            canonicalTargetPath,
            relocation.TargetCaseSensitivityMode,
            cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Target filesystem identity became unavailable during finalization.";
            return;
        }

        var targetSemanticsError = await ValidateRelocationTargetSemanticsAsync(
            db,
            relocation,
            resolution.Semantics,
            cancellationToken);
        if (targetSemanticsError != null)
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = targetSemanticsError;
            return;
        }

        if (!relocation.TargetDirectoryObjectIdentityVersion.HasValue
            || string.IsNullOrWhiteSpace(relocation.TargetDirectoryObjectIdentity))
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error =
                "The target directory no longer has persisted physical identity authorization.";
            return;
        }

        var currentObjectIdentity =
            await ResolveExistingDirectoryObjectIdentityAsync(
                canonicalTargetPath,
                relocation.TargetDirectoryObjectIdentityVersion.Value,
                relocation.TargetDirectoryObjectIdentity,
                cancellationToken);
        if (!currentObjectIdentity.IsAvailable
                || currentObjectIdentity.Version
                    != relocation.TargetDirectoryObjectIdentityVersion
                || !string.Equals(
                    currentObjectIdentity.Value,
                    relocation.TargetDirectoryObjectIdentity,
                    StringComparison.Ordinal))
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error =
                "The target directory changed after the path change was authorized.";
            return;
        }

        var command = new RootFolderPathChangeCommand(
            canonicalTargetPath,
            relocation.Mode,
            relocation.DeleteEmptySource,
            relocation.DesiredName,
            relocation.DesiredIsDefault,
            relocation.TargetCaseSensitivityMode);
        ApplyRootMetadata(
            root,
            command,
            canonicalTargetPath,
            resolution,
            FileSystemPathIdentity.CreateKey("root", canonicalTargetPath, resolution.Semantics));
        root.DirectoryObjectIdentityVersion = currentObjectIdentity.Version;
        root.DirectoryObjectIdentity = currentObjectIdentity.Value;
        root.DirectoryObjectIdentityUnavailableReason =
            currentObjectIdentity.UnavailableReason;
        if (relocation.DesiredIsDefault)
        {
            await ClearOtherDefaultsAsync(db, root.Id, cancellationToken);
        }

        await FinalizeRelocationTargetReservationsAsync(
            db,
            relocation.Id,
            cancellationToken);
        relocation.TargetIdentityEnrollmentState =
            TargetIdentityEnrollmentState.NotRequired;
        relocation.Status = RootFolderRelocationStatus.Completed;
        relocation.ActiveRootFolderId = null;
        relocation.CompletedAt = now;
        relocation.Error = null;
    }

    private static void ApplyRootMetadata(
        RootFolder root,
        RootFolderPathChangeCommand command,
        string targetPath,
        FileSystemSemanticsResolution resolution,
        string identityKey)
    {
        root.Path = targetPath;
        root.Name = command.DesiredName.Trim();
        root.IsDefault = command.DesiredIsDefault;
        root.CaseSensitivityMode = command.TargetCaseSensitivityMode;
        root.ResolvedCaseSensitivity = resolution.Semantics.CaseSensitivity;
        root.PathIdentityState = resolution.State;
        root.PathIdentityKey = identityKey;
        root.UpdatedAt = DateTime.UtcNow;
    }

    private static Task ClearOtherDefaultsAsync(
        ListenArrDbContext db,
        int rootFolderId,
        CancellationToken cancellationToken) =>
        db.RootFolders
            .Where(root => root.Id != rootFolderId && root.IsDefault)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(root => root.IsDefault, false),
                cancellationToken);

    private async Task RetrySkippedMetadataReferencesAsync(
        ListenArrDbContext db,
        RootFolderRelocation relocation,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var skippedItems = relocation.SkippedItems.ToList();
        var audiobookIds = skippedItems.Select(item => item.AudiobookId).ToList();
        var audiobooks = await db.Audiobooks
            .Include(audiobook => audiobook.Files)
            .Where(audiobook => audiobookIds.Contains(audiobook.Id))
            .ToDictionaryAsync(audiobook => audiobook.Id, cancellationToken);
        await db.AudiobookFiles.LoadAsync(cancellationToken);

        if (!TryResolvePersistedRelocationSourceSemantics(
                relocation,
                out var sourceSemantics,
                out var sourceReason))
        {
            foreach (var skippedItem in skippedItems)
            {
                skippedItem.Reason = EncodeMetadataSkipReason(
                    RootFolderRelocationSkipReasonCode.SourceSemanticsUnavailable,
                    sourceReason);
            }

            return;
        }

        var retryCandidates = new List<AudiobookPathCandidate>();
        var resolvedCount = 0;
        foreach (var skippedItem in skippedItems)
        {
            if (!audiobooks.TryGetValue(skippedItem.AudiobookId, out var audiobook))
            {
                relocation.SkippedItems.Remove(skippedItem);
                db.RootFolderRelocationSkippedItems.Remove(skippedItem);
                resolvedCount++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                skippedItem.Reason = EncodeMetadataSkipReason(
                    RootFolderRelocationSkipReasonCode.InvalidStoredPath,
                    "Audiobook no longer has a base path to rewrite.");
                continue;
            }

            retryCandidates.Add(new AudiobookPathCandidate(
                audiobook,
                audiobook.BasePath!));
        }

        var now = timeProvider.GetUtcNow();
        var planning = PlanMetadataPathRewrites(
            db,
            retryCandidates,
            relocation.SourcePath,
            relocation.TargetPath,
            sourceSemantics,
            targetSemantics,
            relocation.TargetCaseSensitivityMode,
            now);
        var remainingReasons = planning.SkippedItems.ToDictionary(
            item => item.AudiobookId);
        foreach (var plan in planning.SafePlans)
        {
            AudiobookPathReferenceRewriter.Rewrite(
                plan.Candidate.Audiobook,
                plan.Candidate.StoredBasePath,
                plan.Destination,
                sourceSemantics,
                targetSemantics,
                relocation.TargetCaseSensitivityMode);
            var skippedItem = skippedItems.Single(item =>
                item.AudiobookId == plan.Candidate.Audiobook.Id);
            relocation.SkippedItems.Remove(skippedItem);
            db.RootFolderRelocationSkippedItems.Remove(skippedItem);
            resolvedCount++;
        }

        RejectDuplicateAudiobookFileOwnership(db);
        foreach (var skippedItem in relocation.SkippedItems)
        {
            if (remainingReasons.TryGetValue(
                    skippedItem.AudiobookId,
                    out var plannedSkip))
            {
                skippedItem.Reason = plannedSkip.Reason;
            }
        }

        relocation.CompletedJobs = Math.Min(
            relocation.TotalJobs,
            relocation.CompletedJobs + resolvedCount);
    }

    private static string BuildSkippedMetadataError(int skippedCount) =>
        $"{skippedCount} audiobook(s) could not have stored paths rewritten automatically.";

    private static string BuildRetryAttentionError(int skippedCount, int supersededJobCount)
    {
        var messages = new List<string>();
        if (skippedCount > 0)
        {
            messages.Add(BuildSkippedMetadataError(skippedCount));
        }

        if (supersededJobCount > 0)
        {
            messages.Add($"{supersededJobCount} job(s) were superseded by a newer move and were not retried.");
        }

        return string.Join(" ", messages);
    }

    private static string ResolveCurrentPathFallback(RootFolderRelocation relocation) =>
        relocation.Status == RootFolderRelocationStatus.Completed
            ? relocation.TargetPath
            : relocation.SourcePath;

    private static RootFolderPathChangeResult Map(
        RootFolderRelocation relocation,
        string currentPath,
        bool canAbandon = false) => new(
        relocation.Id,
        relocation.RootFolderId,
        currentPath,
        relocation.TargetPath,
        relocation.Status,
        relocation.TotalJobs,
        relocation.CompletedJobs,
        relocation.Error,
        relocation.TargetIdentityEnrollmentState,
        relocation.SkippedItems
            .Select(item => item.AudiobookId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray(),
        relocation.Mode,
        relocation.SkippedItems
            .OrderBy(item => item.AudiobookId)
            .Select(item => new RootFolderRelocationSkippedItemResult(
                item.AudiobookId,
                ClassifyMetadataSkipReason(item.Reason)))
            .ToArray(),
        canAbandon);

    private async Task BroadcastAsync(
        RootFolderPathChangeResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await hubBroadcaster.BroadcastAsync(
                "RootFolderRelocationUpdate",
                RootFolderRelocationPublicProjection.Sanitize(result),
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            // The relocation state is already committed. Request or transport
            // cancellation may suppress this best-effort publication, but it must
            // not make the durable operation appear to have failed.
            System.Diagnostics.Trace.TraceWarning(
                "Canceled broadcasting root relocation {0}: {1}",
                result.RelocationId,
                exception.Message);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            System.Diagnostics.Trace.TraceWarning(
                "Failed to broadcast root relocation {0}: {1}",
                result.RelocationId,
                exception.Message);
        }
    }

}
