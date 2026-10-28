using System.Security.Cryptography;
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    private enum RestartTargetProbe
    {
        Match,
        Mismatch,
        Unavailable
    }

    private enum RestartSourceProbe
    {
        Absent,
        Exists,
        Unknown
    }

    private async Task<FileRegistrationRecoveryReceipt?> ReconcileRestartedPublicationAsync(
        FileMutationJournal journal,
        int audiobookId,
        bool failWhenStillPending,
        CancellationToken cancellationToken)
    {
        var targetProbe = await ProbeRestartedPublicationTargetAsync(
            journal,
            cancellationToken);
        if (targetProbe == RestartTargetProbe.Unavailable)
        {
            logger.LogWarning(
                "File-registration recovery {OperationId} remains pending because its published destination cannot currently be verified",
                journal.OperationId);
            if (failWhenStillPending)
            {
                throw RecoveryPending(journal.OperationId);
            }

            return null;
        }

        if (targetProbe == RestartTargetProbe.Mismatch)
        {
            if (!await TryMarkNeedsAttentionAsync(
                    journal.OperationId,
                    journal.State,
                    "The committed file-registration destination no longer matches the published content proof.",
                    cancellationToken))
            {
                return null;
            }

            if (failWhenStillPending)
            {
                throw RepairRequired(journal.OperationId);
            }

            return null;
        }

        if (journal.Action != FileAction.Move)
        {
            if (!await TryMarkRestartPublicationTerminalAsync(
                    journal.OperationId,
                    journal.State,
                    FileMutationJournalState.Completed,
                    reason: null,
                    cancellationToken))
            {
                return null;
            }

            logger.LogInformation(
                "Recovered committed {Action} publication {OperationId} for audiobook {AudiobookId} from durable content evidence",
                journal.Action,
                journal.OperationId,
                audiobookId);
            return null;
        }

        var sourceProbe = ProbeRestartedMoveSource(journal.SourcePath);
        var terminalState = sourceProbe == RestartSourceProbe.Absent
            ? FileMutationJournalState.Completed
            : FileMutationJournalState.CompletedSourceRetained;
        var retainedReason = terminalState == FileMutationJournalState.CompletedSourceRetained
            ? sourceProbe == RestartSourceProbe.Exists
                ? "Source cleanup was not resumed after restart; the existing source was retained."
                : "Source cleanup was not resumed after restart because source absence could not be proven; the source path was retained conservatively."
            : null;

        if (!await TryMarkRestartPublicationTerminalAsync(
                journal.OperationId,
                journal.State,
                terminalState,
                retainedReason,
                cancellationToken))
        {
            return null;
        }

        if (terminalState == FileMutationJournalState.CompletedSourceRetained)
        {
            logger.LogWarning(
                "Recovered committed move publication {OperationId} for audiobook {AudiobookId} without source retirement; the source path was retained",
                journal.OperationId,
                audiobookId);
            return sourceProbe == RestartSourceProbe.Exists
                ? new FileRegistrationRecoveryReceipt(
                    journal.OperationId,
                    audiobookId,
                    journal.SourcePath,
                    journal.DestinationPath,
                    SourceRetained: true,
                    SourceLength: journal.SourceLength,
                    SourceSha256: journal.SourceSha256)
                : null;
        }

        logger.LogInformation(
            "Recovered committed move publication {OperationId} for audiobook {AudiobookId}; the source was already absent",
            journal.OperationId,
            audiobookId);
        return new FileRegistrationRecoveryReceipt(
            journal.OperationId,
            audiobookId,
            journal.SourcePath,
            journal.DestinationPath);
    }

    private async Task<RestartTargetProbe> ProbeRestartedPublicationTargetAsync(
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(journal.SourceSha256))
        {
            return RestartTargetProbe.Mismatch;
        }

        try
        {
            var parentPath = Path.GetDirectoryName(journal.DestinationPath);
            var fileName = Path.GetFileName(journal.DestinationPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return RestartTargetProbe.Mismatch;
            }

            // Only the current configured boundary may follow a link. Descendants
            // remain no-follow, and this read proof never grants source deletion.
            var configuredRoot = await ResolveRestartTargetRootAsync(
                journal.DestinationPath, cancellationToken);
            using var parent = configuredRoot == null
                ? PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath, createMissing: false)
                : PinnedDirectoryCreation.OpenPinnedConfiguredHierarchy(
                    configuredRoot, parentPath, createMissing: false);
            var outcome = parent.TryOpenExistingFileWithOutcome(
                fileName, requireDeleteAccess: false, out var openedTarget);
            using var target = openedTarget;
            if (outcome == PinnedFileOpenOutcome.Unavailable)
            {
                return RestartTargetProbe.Unavailable;
            }
            if (target == null
                || !target.IsRegularFile()
                || !parent.VisiblePathMatches()
                || !target.VisiblePathMatches())
            {
                return RestartTargetProbe.Mismatch;
            }

            await using var stream = target.OpenReadStream(
                bufferSize: 81920, asynchronous: false);
            if (stream.Length != journal.SourceLength)
            {
                return RestartTargetProbe.Mismatch;
            }

            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            return parent.VisiblePathMatches()
                && target.VisiblePathMatches()
                && string.Equals(
                    hash,
                    journal.SourceSha256,
                    StringComparison.OrdinalIgnoreCase)
                ? RestartTargetProbe.Match
                : RestartTargetProbe.Mismatch;
        }
        catch (Exception exception) when (
            FileSystemSafety.IsProvenMissingPathException(exception))
        {
            return RestartTargetProbe.Mismatch;
        }
        catch (System.ComponentModel.Win32Exception exception) when (
            !OperatingSystem.IsWindows() && exception.NativeErrorCode is 20 or 40)
        {
            return RestartTargetProbe.Mismatch;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return RestartTargetProbe.Mismatch;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or System.Security.SecurityException)
        {
            return RestartTargetProbe.Unavailable;
        }
    }

    private async Task<RootFolder?> ResolveRestartTargetRootAsync(
        string path, CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(path, out var syntax))
            throw new InvalidOperationException("The publication path is not an absolute host path.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var roots = await db.RootFolders.AsNoTracking().ToListAsync(cancellationToken);
        RootFolder? selected = null;
        FileSystemPathSemantics? selectedSemantics = null;
        var blockedLength = -1;
        foreach (var root in roots)
        {
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (!persisted.HasValue || persisted.Value.DetectAmbiguousCaseMatches
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path, out _, out _))
            {
                if (FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                    root.Path, path, syntax, root.CaseSensitivityMode)
                    || FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                        root.Path.Trim(), path, syntax, root.CaseSensitivityMode))
                    blockedLength = Math.Max(blockedLength, root.Path.Length);
                continue;
            }
            if (persisted.Value.Semantics.Syntax != syntax
                || !FileSystemPathIdentity.IsSameOrInside(path, root.Path, persisted.Value.Semantics))
                continue;
            if (selected != null && root.Path.Length == selected.Path.Length)
                blockedLength = Math.Max(blockedLength, root.Path.Length);
            if (selected == null || root.Path.Length > selected.Path.Length)
            {
                selected = root;
                selectedSemantics = persisted.Value.Semantics;
            }
        }
        if (blockedLength >= (selected?.Path.Length ?? -1) && blockedLength >= 0)
            throw new IOException("The deepest configured publication boundary is unavailable or ambiguous.");
        if (selected == null) return null;
        var current = await new FileSystemSemanticsResolver().ResolveAsync(
            selected.Path, selected.CaseSensitivityMode, cancellationToken);
        if (current.State == PathIdentityState.Unavailable)
            throw new IOException("The configured publication boundary is temporarily unavailable.");
        if (current.State != PathIdentityState.Valid || current.Semantics != selectedSemantics!.Value)
            throw new InvalidOperationException("The configured publication boundary semantics changed.");
        return selected;
    }

    private static RestartSourceProbe ProbeRestartedMoveSource(string sourcePath)
    {
        try
        {
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return RestartSourceProbe.Exists;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return RestartSourceProbe.Absent;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or System.Security.SecurityException)
        {
            return RestartSourceProbe.Unknown;
        }
    }

    private static bool IsTransientRecoveryFilesystemException(
        Exception exception)
    {
        if (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        if (exception is System.ComponentModel.Win32Exception native)
        {
            return native.NativeErrorCode is 5 or 13 or 16 or 30 or 32 or 33;
        }

        return exception is InvalidOperationException { InnerException: not null }
            && IsTransientRecoveryFilesystemException(exception.InnerException);
    }

    private async Task<bool> TryMarkRestartPublicationTerminalAsync(
        Guid operationId,
        FileMutationJournalState expectedState,
        FileMutationJournalState terminalState,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            var affected = await db.FileMutationJournals
                .Where(candidate => candidate.OperationId == operationId
                    && candidate.State == expectedState)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.State, terminalState)
                        .SetProperty(candidate => candidate.Error, reason)
                        .SetProperty(candidate => candidate.UpdatedAt, now),
                    cancellationToken);
            return affected == 1;
        }

        var tracked = await db.FileMutationJournals.SingleOrDefaultAsync(
            candidate => candidate.OperationId == operationId,
            cancellationToken);
        if (tracked == null || tracked.State != expectedState)
        {
            return false;
        }

        tracked.State = terminalState;
        tracked.Error = reason;
        tracked.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
