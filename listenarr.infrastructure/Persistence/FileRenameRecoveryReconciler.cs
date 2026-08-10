using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Reconciles generation-fenced organize/rename journals with their owning
/// audiobook metadata before ordinary file-identity startup reconciliation.
/// </summary>
public sealed class FileRenameRecoveryReconciler(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileMover fileMover,
    IAudiobookFilePathIdentityResolver identityResolver,
    IFileSystemSemanticsResolver semanticsResolver,
    TimeProvider timeProvider,
    ILogger<FileRenameRecoveryReconciler> logger) : IFileRenameRecoveryReconciler
{
    internal Func<Guid, Task>? AfterInitialOwnerBindingLoadedForTestAsync { get; set; }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var attentionOperationId = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.Action == FileAction.Move
                && journal.AudiobookId != null
                && journal.AudiobookFileId != null
                && journal.State == FileMutationJournalState.NeedsAttention)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => (Guid?)journal.OperationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (attentionOperationId.HasValue)
        {
            throw new InvalidOperationException(
                $"Owner-bound file organize journal {attentionOperationId.Value} requires operator repair before filesystem mutations can resume.");
        }

        var operationIds = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.Action == FileAction.Move
                && journal.AudiobookId != null
                && journal.AudiobookFileId != null
                && journal.State != FileMutationJournalState.OwnerMetadataReconciled)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => journal.OperationId)
            .ToListAsync(cancellationToken);

        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileOperationAsync(operationId, cancellationToken);
        }
    }

    private async Task ReconcileOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        FileMutationJournal journal;
        Audiobook audiobook;
        AudiobookFile? audiobookFile;
        int ownerAudiobookId;
        int ownerAudiobookFileId;
        await using (var context = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            journal = await context.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
            if (journal.State is FileMutationJournalState.OwnerMetadataReconciled
                or FileMutationJournalState.NeedsAttention)
            {
                return;
            }

            if (!journal.AudiobookId.HasValue || !journal.AudiobookFileId.HasValue)
            {
                return;
            }

            ownerAudiobookId = journal.AudiobookId.Value;
            ownerAudiobookFileId = journal.AudiobookFileId.Value;
            audiobook = await context.Audiobooks
                .AsNoTracking()
                .Include(candidate => candidate.Files)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == journal.AudiobookId.Value,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "An owned file-mutation journal references a missing audiobook before metadata reconciliation.");
            audiobookFile = journal.AudiobookFileId.Value == 0
                ? null
                : audiobook.Files?.SingleOrDefault(file => file.Id == journal.AudiobookFileId.Value)
                    ?? throw new InvalidOperationException(
                        "An owned file-mutation journal references a missing audiobook file before metadata reconciliation.");
        }

        if (AfterInitialOwnerBindingLoadedForTestAsync != null)
        {
            await AfterInitialOwnerBindingLoadedForTestAsync(operationId);
        }

        if (journal.State < FileMutationJournalState.Completed)
        {
            var resumed = audiobookFile == null
                ? await fileMover.PerformActionOn(
                    FileAction.Move,
                    journal.SourcePath,
                    journal.DestinationPath,
                    journal.OperationId,
                    journal.AudiobookId!.Value,
                    audiobookFileId: 0)
                : await fileMover.MoveFilePreservingPhysicalIdentityAsync(
                    journal.SourcePath,
                    journal.DestinationPath,
                    journal.SourcePhysicalObjectIdentity,
                    journal.OperationId,
                    journal.AudiobookId!.Value,
                    audiobookFile.Id);
            if (!resumed)
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The interrupted organize file mutation could not be resumed safely.",
                    cancellationToken);
                return;
            }
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        journal = await db.FileMutationJournals
            .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
        if (journal.State == FileMutationJournalState.OwnerMetadataReconciled)
        {
            return;
        }
        if (journal.State != FileMutationJournalState.Completed
            || string.IsNullOrWhiteSpace(journal.TargetPhysicalObjectIdentity))
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The interrupted organize journal did not reach a verified completed target.",
                cancellationToken);
            return;
        }
        if (journal.AudiobookId != ownerAudiobookId
            || journal.AudiobookFileId != ownerAudiobookFileId)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The interrupted organize journal owner binding changed during recovery.",
                cancellationToken);
            return;
        }

        var trackedAudiobook = await db.Audiobooks
            .Include(candidate => candidate.Files)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == ownerAudiobookId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The audiobook disappeared before its completed organize journal could be reconciled.");
        var trackedFile = ownerAudiobookFileId == 0
            ? null
            : trackedAudiobook.Files?.SingleOrDefault(file => file.Id == ownerAudiobookFileId)
                ?? throw new InvalidOperationException(
                    "The audiobook file disappeared before its completed organize journal could be reconciled.");

        if (!TargetGenerationMatches(journal))
        {
            if (SourceGenerationMatches(journal)
                && await OwnerMetadataPointsToSourceAsync(
                    trackedAudiobook,
                    trackedFile,
                    journal,
                    cancellationToken))
            {
                journal.State = FileMutationJournalState.OwnerMetadataReconciled;
                journal.Error = null;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Reconciled compensated organize journal {OperationId}; the original source generation and owner metadata were already restored",
                    operationId);
                return;
            }

            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize destination no longer identifies the journaled physical file generation.",
                cancellationToken);
            return;
        }

        if (trackedFile != null
            && !string.Equals(
                trackedFile.PhysicalObjectIdentity,
                journal.SourcePhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The tracked audiobook file no longer identifies the source generation owned by the organize journal.",
                cancellationToken);
            return;
        }

        if (trackedFile == null)
        {
            trackedAudiobook.FilePath = journal.DestinationPath;
        }
        else
        {
            var destinationIdentity = await identityResolver.ResolveAsync(
                trackedAudiobook,
                journal.DestinationPath,
                cancellationToken);
            if (destinationIdentity.State != PathIdentityState.Valid)
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The completed organize destination no longer has a valid filesystem path identity.",
                    cancellationToken);
                return;
            }

            trackedFile.ApplyPathIdentity(journal.DestinationPath, destinationIdentity);
            trackedFile.ApplyPhysicalObjectIdentity(
                journal.TargetPhysicalObjectIdentity,
                timeProvider.GetUtcNow().UtcDateTime);
        }

        await NormalizeAudiobookPathsAsync(trackedAudiobook, cancellationToken);
        journal.State = FileMutationJournalState.OwnerMetadataReconciled;
        journal.Error = null;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Reconciled interrupted organize journal {OperationId} for audiobook {AudiobookId}",
            operationId,
            trackedAudiobook.Id);
    }

    private static bool TargetGenerationMatches(FileMutationJournal journal) =>
        PathGenerationMatches(
            journal.DestinationPath,
            journal.TargetPhysicalObjectIdentity);

    private static bool SourceGenerationMatches(FileMutationJournal journal) =>
        PathGenerationMatches(
            journal.SourcePath,
            journal.SourcePhysicalObjectIdentity);

    private static bool PathGenerationMatches(
        string path,
        string? expectedPhysicalObjectIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var file = parent.OpenExistingFileForStableRead(Path.GetFileName(fullPath));
            return file.VisiblePathMatches()
                && parent.VisiblePathMatches()
                && string.Equals(
                    file.GetObjectIdentity(),
                    expectedPhysicalObjectIdentity,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<bool> OwnerMetadataPointsToSourceAsync(
        Audiobook audiobook,
        AudiobookFile? audiobookFile,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (audiobookFile != null)
        {
            if (string.IsNullOrWhiteSpace(audiobookFile.Path))
            {
                return false;
            }

            var identity = await identityResolver.ResolveAsync(
                audiobook,
                audiobookFile.Path,
                cancellationToken);
            return identity.State == PathIdentityState.Valid
                && FileSystemPathIdentity.AreEquivalent(
                    identity.CanonicalPath,
                    journal.SourcePath,
                    new FileSystemPathSemantics(
                        identity.Syntax,
                        identity.CaseSensitivity));
        }

        if (string.IsNullOrWhiteSpace(audiobook.FilePath))
        {
            return false;
        }
        var resolution = await semanticsResolver.ResolveAsync(
            journal.SourcePath,
            cancellationToken: cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            return false;
        }

        var storedSource = ResolveAbsoluteStoredPath(
            audiobook.FilePath,
            audiobook.BasePath);
        return FileSystemPathIdentity.AreEquivalent(
            storedSource,
            journal.SourcePath,
            resolution.Semantics);
    }

    private async Task NormalizeAudiobookPathsAsync(
        Audiobook audiobook,
        CancellationToken cancellationToken)
    {
        var oldBasePath = audiobook.BasePath;
        var resolvedFiles = new List<(AudiobookFile File, string Path, AudiobookFilePathIdentity Identity)>();
        foreach (var file in audiobook.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            var absolutePath = ResolveAbsoluteStoredPath(file.Path, oldBasePath);
            var identity = await identityResolver.ResolveAsync(
                audiobook,
                absolutePath,
                cancellationToken);
            if (identity.State != PathIdentityState.Valid)
            {
                throw new InvalidOperationException(
                    "An audiobook file path could not be normalized after organize recovery.");
            }
            resolvedFiles.Add((file, absolutePath, identity));
        }

        foreach (var resolved in resolvedFiles)
        {
            resolved.File.ApplyPathIdentity(resolved.Path, resolved.Identity);
        }

        if (resolvedFiles.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var absoluteLegacyPath = ResolveAbsoluteStoredPath(
                    audiobook.FilePath,
                    oldBasePath);
                audiobook.FilePath = absoluteLegacyPath;
                audiobook.BasePath = Path.GetDirectoryName(absoluteLegacyPath);
            }
            return;
        }

        var firstIdentity = resolvedFiles[0].Identity;
        var semantics = new FileSystemPathSemantics(
            firstIdentity.Syntax,
            firstIdentity.CaseSensitivity);
        if (resolvedFiles.Any(item =>
                item.Identity.Syntax != semantics.Syntax
                || item.Identity.CaseSensitivity != semantics.CaseSensitivity))
        {
            throw new InvalidOperationException(
                "Recovered organize files no longer share one filesystem semantics contract.");
        }

        var directories = resolvedFiles
            .Select(item => Path.GetDirectoryName(item.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        audiobook.BasePath = FileUtils.GetCommonPathForDirectories(directories, semantics)
            ?? throw new InvalidOperationException(
                "Recovered organize files no longer have a common directory boundary.");
        var primary = resolvedFiles
            .OrderBy(item => item.Path, semantics.Comparer)
            .First();
        audiobook.FilePath = primary.Path;
        if (primary.File.Size > 0)
        {
            audiobook.FileSize = primary.File.Size;
        }
    }

    private static string ResolveAbsoluteStoredPath(string path, string? basePath)
    {
        if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var absolutePath,
                out _))
        {
            return absolutePath;
        }
        if (string.IsNullOrWhiteSpace(basePath)
            || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                basePath,
                out var canonicalBase,
                out _)
            || Path.IsPathRooted(path))
        {
            throw new InvalidOperationException(
                "A recovered audiobook file path cannot be resolved against its stored base path.");
        }

        return Path.GetFullPath(Path.Combine(canonicalBase, path));
    }

    private async Task MarkNeedsAttentionAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var journal = await db.FileMutationJournals
            .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
        journal.State = FileMutationJournalState.NeedsAttention;
        journal.Error = error;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Organize journal {OperationId} requires attention: {Reason}",
            operationId,
            error);
    }
}
