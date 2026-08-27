using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed class WeakStorageScanCandidateStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider) : IWeakStorageScanCandidateStore
{
    private static readonly TimeSpan CandidateLifetime = TimeSpan.FromHours(24);

    internal Action? BeforeConfirmationCommitForTest { get; set; }

    public async Task<Guid> ReplaceAsync(
        int audiobookId,
        IReadOnlyCollection<WeakStorageMissingFileCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var scanToken = Guid.NewGuid();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        // Scan candidates are ephemeral confirmation state, not audit records. A new
        // authoritative scan supersedes every older token for this audiobook, including
        // candidates that were already confirmed, so the table cannot grow without bound.
        var previousQuery = context.WeakStorageScanCandidates
            .Where(candidate => candidate.AudiobookId == audiobookId);
        if (context.Database.IsRelational())
        {
            await previousQuery.ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            context.WeakStorageScanCandidates.RemoveRange(
                await previousQuery.ToListAsync(cancellationToken));
        }
        context.WeakStorageScanCandidates.AddRange(candidates.Select(candidate =>
            new WeakStorageScanCandidate
            {
                Id = Guid.NewGuid(),
                ScanToken = scanToken,
                AudiobookId = audiobookId,
                AudiobookFileId = candidate.AudiobookFileId,
                ExpectedStoredPath = candidate.ExpectedStoredPath,
                ExpectedResolvedPath = candidate.ExpectedResolvedPath,
                ExpectedPhysicalObjectIdentity = candidate.ExpectedPhysicalObjectIdentity,
                CreatedAt = now,
                ExpiresAt = now + CandidateLifetime
            }));
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return scanToken;
    }

    public async Task<IReadOnlyList<WeakStorageScanCandidate>> GetPendingAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        return await context.WeakStorageScanCandidates
            .AsNoTracking()
            .Where(candidate => candidate.AudiobookId == audiobookId
                && candidate.ConfirmedAt == null
                && candidate.ExpiresAt > now)
            .OrderBy(candidate => candidate.ExpectedResolvedPath)
            .ToListAsync(cancellationToken);
    }

    public async Task<WeakStorageScanConfirmationResult> ConfirmAsync(
        int audiobookId,
        Guid scanToken,
        IReadOnlyCollection<Guid> candidateIds,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var candidates = await context.WeakStorageScanCandidates
            .Where(candidate => candidate.AudiobookId == audiobookId
                && candidate.ScanToken == scanToken
                && candidateIds.Contains(candidate.Id)
                && candidate.ConfirmedAt == null
                && candidate.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        if (candidates.Count != candidateIds.Distinct().Count())
        {
            throw new DbUpdateConcurrencyException(
                "One or more missing-file candidates are stale.");
        }

        var removed = 0;
        var preserved = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentFile = await context.AudiobookFiles
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    file => file.Id == candidate.AudiobookFileId
                        && file.AudiobookId == audiobookId
                        && file.Path == candidate.ExpectedStoredPath
                        && EF.Property<string?>(file, "PhysicalObjectIdentity")
                            == candidate.ExpectedPhysicalObjectIdentity,
                    cancellationToken);
            var currentBasePath = await context.Audiobooks
                .AsNoTracking()
                .Where(audiobook => audiobook.Id == audiobookId)
                .Select(audiobook => audiobook.BasePath)
                .SingleOrDefaultAsync(cancellationToken);
            if (currentFile == null
                || !TryResolveCurrentPath(
                    currentFile,
                    currentBasePath,
                    out var currentResolvedPath,
                    out var dependsOnBasePath)
                || !SameResolvedPath(
                    currentResolvedPath,
                    candidate.ExpectedResolvedPath)
                || !IsProvenMissing(candidate.ExpectedResolvedPath))
            {
                preserved.Add(candidate.ExpectedResolvedPath);
                continue;
            }

            var currentCanonicalPath = currentFile.CanonicalPath;
            var currentPathSyntax = currentFile.PathSyntax;
            var currentPathIdentityState = currentFile.PathIdentityState;
            var matchingFileQuery = context.AudiobookFiles
                .Where(file => file.Id == candidate.AudiobookFileId
                    && file.AudiobookId == audiobookId
                    && file.Path == candidate.ExpectedStoredPath
                    && file.CanonicalPath == currentCanonicalPath
                    && file.PathSyntax == currentPathSyntax
                    && file.PathIdentityState == currentPathIdentityState
                    && EF.Property<string?>(file, "PhysicalObjectIdentity")
                        == candidate.ExpectedPhysicalObjectIdentity);
            if (dependsOnBasePath)
            {
                matchingFileQuery = matchingFileQuery.Where(_ =>
                    context.Audiobooks.Any(audiobook =>
                        audiobook.Id == audiobookId
                        && audiobook.BasePath == currentBasePath));
            }
            int deleted;
            if (context.Database.IsRelational())
            {
                deleted = await matchingFileQuery.ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                var matchingFile = await matchingFileQuery.SingleOrDefaultAsync(
                    cancellationToken);
                deleted = matchingFile == null ? 0 : 1;
                if (matchingFile != null)
                {
                    context.AudiobookFiles.Remove(matchingFile);
                }
            }
            if (deleted != 1)
            {
                preserved.Add(candidate.ExpectedResolvedPath);
                continue;
            }

            candidate.ConfirmedAt = now;
            removed++;
        }

        BeforeConfirmationCommitForTest?.Invoke();
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return new WeakStorageScanConfirmationResult(
            removed,
            preserved.Count,
            preserved);
    }

    private static bool TryResolveCurrentPath(
        AudiobookFile file,
        string? audiobookBasePath,
        out string resolvedPath,
        out bool dependsOnBasePath)
    {
        resolvedPath = string.Empty;
        dependsOnBasePath = false;
        try
        {
            if (file.PathIdentityState == PathIdentityState.Valid
                && file.PathSyntax.HasValue
                && !string.IsNullOrWhiteSpace(file.CanonicalPath))
            {
                resolvedPath = FileSystemPathIdentity.Canonicalize(
                    file.CanonicalPath,
                    file.PathSyntax.Value);
                return true;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return false;
            }

            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    file.Path,
                    out var absoluteSyntax))
            {
                resolvedPath = FileSystemPathIdentity.Canonicalize(
                    file.Path,
                    absoluteSyntax);
                return true;
            }

            if (string.IsNullOrWhiteSpace(audiobookBasePath))
            {
                return false;
            }

            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    audiobookBasePath,
                    out var baseSyntax))
            {
                return false;
            }

            var caseSensitivity = file.PathCaseSensitivity
                == FileSystemCaseSensitivity.Unknown
                    ? FileSystemCaseSensitivity.Sensitive
                    : file.PathCaseSensitivity;
            dependsOnBasePath = true;
            return FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                audiobookBasePath,
                file.Path,
                new FileSystemPathSemantics(baseSyntax, caseSensitivity),
                out resolvedPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool SameResolvedPath(string left, string right)
    {
        try
        {
            return string.Equals(
                FileSystemPathIdentity.ResolveNativeAbsolutePath(left),
                FileSystemPathIdentity.ResolveNativeAbsolutePath(right),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsProvenMissing(string path)
    {
        try
        {
            var fullPath = FileSystemPathIdentity.ResolveNativeAbsolutePath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            var outcome = parent.TryOpenExistingFileForStableDeleteWithOutcome(
                fileName,
                out var openedFile);
            using var file = openedFile;
            return outcome == PinnedFileOpenOutcome.NotFound
                && parent.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}
