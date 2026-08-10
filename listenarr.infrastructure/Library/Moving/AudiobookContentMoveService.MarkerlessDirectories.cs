using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CreateMarkerlessTargetDirectoriesAsync(
        AudiobookContentMoveRequest request,
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        var desired = CollectMarkerlessTargetDirectories(
            target,
            manifest,
            request.TargetSemantics);
        var missing = desired
            .Where(path => !Directory.Exists(path) && !File.Exists(path))
            .ToArray();
        await PersistCreatedDirectoriesAsync(
            request.JobId,
            request.LeaseToken,
            missing,
            cancellationToken);

        var ledger = (await GetCreatedDirectoriesAsync(
                request.JobId,
                cancellationToken))
            .ToDictionary(directory => directory.Path, request.TargetSemantics.Comparer);
        var pathsToProcess = new HashSet<string>(
            desired,
            request.TargetSemantics.Comparer);
        foreach (var persisted in ledger.Values)
        {
            ValidateMarkerlessTargetDirectoryLedgerPath(
                persisted.Path,
                target,
                request.TargetSemantics);
            pathsToProcess.Add(persisted.Path);
        }

        foreach (var path in pathsToProcess.OrderBy(GetPathDepth))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless target directory path is occupied by a file: {path}");
            }

            if (!ledger.TryGetValue(path, out var planned))
            {
                if (!Directory.Exists(path))
                {
                    throw new MoveNeedsAttentionException(
                        "A required target directory was not durably planned before creation.");
                }
                ValidateExistingMoveDirectory(path, "markerless target directory");
                continue;
            }

            if (Directory.Exists(path))
            {
                if (planned.State == MoveCreatedDirectoryState.Planned
                    && string.IsNullOrWhiteSpace(planned.DirectoryObjectIdentity))
                {
                    await RetainUnexplainedMarkerlessDirectoryAsync(
                        request,
                        planned,
                        cancellationToken);
                    continue;
                }
                if (planned.State is not (
                        MoveCreatedDirectoryState.Created
                        or MoveCreatedDirectoryState.Retained)
                    || string.IsNullOrWhiteSpace(planned.DirectoryObjectIdentity))
                {
                    throw new MoveNeedsAttentionException(
                        $"A planned markerless target directory has inconsistent persisted state: {path}");
                }
                ValidateMarkerlessCreatedDirectory(planned);
                continue;
            }

            if (planned.State != MoveCreatedDirectoryState.Planned
                || !string.IsNullOrWhiteSpace(planned.DirectoryObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    $"A previously created markerless target directory disappeared: {path}");
            }

            var parentPath = Path.GetDirectoryName(path)
                ?? throw new MoveNeedsAttentionException(
                    "A markerless target directory has no parent.");
            using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            using var creation = TryCreateMarkerlessTargetDirectoryForPublication(
                parent,
                path);
            if (!creation.Created)
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless target directory was concurrently created: {path}");
            }

            using var directory = creation.OpenCreatedDirectoryAnchor();
            var identity = directory.GetDirectoryObjectIdentity();
            if (!directory.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    $"A newly created target directory changed before persistence: {path}");
            }

            var persistedState = creation.CreationGenerationIsProvable
                ? MoveCreatedDirectoryState.Created
                : MoveCreatedDirectoryState.Retained;
            faultInjector?.OnTargetScaffoldPreparation(
                request.JobId,
                TargetScaffoldPreparationFaultPoint
                    .AfterMarkerlessDirectoryCreationBeforeStateUpdate);
            try
            {
                await UpdateCreatedDirectoryPublicationAsync(
                    request.JobId,
                    request.LeaseToken,
                    path,
                    persistedState,
                    identity,
                    cancellationToken);
            }
            catch
            {
                TryRetireUncommittedMarkerlessDirectory(creation, directory, path);
                throw;
            }

            planned.State = persistedState;
            planned.DirectoryObjectIdentity = identity;
            faultInjector?.OnTargetScaffoldPreparation(
                request.JobId,
                TargetScaffoldPreparationFaultPoint
                    .AfterMarkerlessDirectoryStateUpdate);
        }
    }

    private async Task RetainUnexplainedMarkerlessDirectoryAsync(
        AudiobookContentMoveRequest request,
        MoveJobCreatedDirectory planned,
        CancellationToken cancellationToken)
    {
        ValidateExistingMoveDirectory(
            planned.Path,
            "unexplained markerless target directory");
        if (Directory.EnumerateFileSystemEntries(planned.Path).Any())
        {
            throw new MoveNeedsAttentionException(
                $"An unproven markerless target directory contains content: {planned.Path}");
        }

        var parentPath = Path.GetDirectoryName(planned.Path)
            ?? throw new MoveNeedsAttentionException(
                "An unproven markerless target directory has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var directory = parent.OpenExistingChild(Path.GetFileName(planned.Path));
        if (!directory.VisiblePathMatches()
            || !parent.VisiblePathMatches()
            || Directory.EnumerateFileSystemEntries(planned.Path).Any())
        {
            throw new MoveNeedsAttentionException(
                $"An unproven markerless target directory changed during recovery: {planned.Path}");
        }

        var identity = directory.GetDirectoryObjectIdentity();
        await UpdateCreatedDirectoryPublicationAsync(
            request.JobId,
            request.LeaseToken,
            planned.Path,
            MoveCreatedDirectoryState.Retained,
            identity,
            cancellationToken);
        planned.State = MoveCreatedDirectoryState.Retained;
        planned.DirectoryObjectIdentity = identity;
    }

    private static IReadOnlyCollection<string> CollectMarkerlessTargetDirectories(
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        FileSystemPathSemantics semantics)
    {
        var paths = new HashSet<string>(semantics.Comparer);
        AddTargetDirectoryChain(paths, target, target, semantics);
        foreach (var entry in manifest.Where(IsPhysicalManifestEntry))
        {
            var resolved = ResolveManifestPath(target, entry, semantics, "target");
            var directory = entry.EntryType == MoveJobEntryType.Directory
                ? resolved
                : Path.GetDirectoryName(resolved);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                AddTargetDirectoryChain(paths, target, directory, semantics);
            }
        }

        var targetParent = Path.GetDirectoryName(target)
            ?? throw new MoveNeedsAttentionException(
                "The markerless target has no parent directory.");
        foreach (var ancestor in FindMissingTargetAncestors(targetParent))
        {
            paths.Add(ancestor);
        }
        return paths;
    }

    private static void ValidateMarkerlessTargetDirectoryLedgerPath(
        string directory,
        string target,
        FileSystemPathSemantics semantics)
    {
        try
        {
            if (!FileSystemPathIdentity.AreEquivalent(
                    directory,
                    target,
                    semantics)
                && !FileSystemPathIdentity.IsSameOrInside(
                    directory,
                    target,
                    semantics)
                && !FileSystemPathIdentity.IsSameOrInside(
                    target,
                    directory,
                    semantics))
            {
                throw new MoveNeedsAttentionException(
                    "A persisted markerless target directory is unrelated to the requested target.");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                "A persisted markerless target directory has an invalid path identity.");
        }
    }

    private static void AddTargetDirectoryChain(
        ISet<string> paths,
        string target,
        string directory,
        FileSystemPathSemantics semantics)
    {
        var current = directory;
        while (true)
        {
            if (!FileSystemPathIdentity.IsSameOrInside(
                    current,
                    target,
                    semantics))
            {
                throw new MoveNeedsAttentionException(
                    "A markerless target directory escaped the requested target root.");
            }

            paths.Add(current);
            if (FileSystemPathIdentity.AreEquivalent(current, target, semantics))
            {
                return;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new MoveNeedsAttentionException(
                    "A markerless target directory chain has no parent.");
        }
    }

    private static PinnedDirectoryCreation TryCreateMarkerlessTargetDirectoryForPublication(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string path)
    {
        try
        {
            return parent.TryCreateChildForPublication(Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception)
        {
            throw new MoveNeedsAttentionException(
                $"The markerless target directory parent changed before creation: {path}. {exception.Message}");
        }
    }

    private static void ValidateMarkerlessCreatedDirectory(
        MoveJobCreatedDirectory planned)
    {
        var parentPath = Path.GetDirectoryName(planned.Path)
            ?? throw new MoveNeedsAttentionException(
                "A persisted target directory has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var directory = parent.OpenExistingChild(Path.GetFileName(planned.Path));
        if (!string.Equals(
                directory.GetDirectoryObjectIdentity(),
                planned.DirectoryObjectIdentity,
                StringComparison.Ordinal)
            || !directory.VisiblePathMatches()
            || !parent.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                $"A move-created target directory changed physical generation: {planned.Path}");
        }
    }

    private static void TryRetireUncommittedMarkerlessDirectory(
        PinnedDirectoryCreation creation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string path)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any()
                && directory.VisiblePathMatches())
            {
                creation.DeletePinnedEmptyDirectoryImmediately(Path.GetFileName(path));
            }
        }
        catch
        {
            // A crash or concurrent mutation can leave the final requested directory.
            // It has no DB identity and will be preserved for explicit attention.
        }
    }

    private async Task CaptureOrValidateMarkerlessTargetRootAsync(
        AudiobookContentMoveRequest request,
        string target,
        CancellationToken cancellationToken)
    {
        using var root = PinnedDirectoryCreation.OpenPinnedBoundary(target);
        var identity = root.GetDirectoryObjectIdentity();
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity)
            && !string.Equals(
                endpoints.TargetDirectoryObjectIdentity,
                identity,
                StringComparison.Ordinal))
        {
            throw new MoveNeedsAttentionException(
                "The markerless move target root changed physical generation.");
        }
        if (!root.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The markerless target root changed while pinned.");
        }
        if (string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity))
        {
            await UpdateEndpointObjectIdentitiesAsync(
                request.JobId,
                request.LeaseToken,
                sourceDirectoryObjectIdentity: null,
                identity,
                cancellationToken);
        }
    }
}
