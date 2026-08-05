using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public sealed class RegistrationPublicationCleanupProcessor(
    IServiceScopeFactory scopeFactory,
    IFileSystemSemanticsResolver semanticsResolver,
    IFilesystemMutationCoordinator mutationCoordinator,
    IAudiobookOperationCoordinator audiobookOperationCoordinator,
    ILogger<RegistrationPublicationCleanupProcessor> logger) :
    IRegistrationPublicationCleanupProcessor
{
    private const int MaxCandidatesPerCycle = 100;

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots;
        using (var discoveryScope = scopeFactory.CreateScope())
        {
            var rootRepository = discoveryScope.ServiceProvider
                .GetRequiredService<IRootFolderRepository>();
            var configurationService = discoveryScope.ServiceProvider
                .GetRequiredService<IConfigurationService>();
            var candidates = (await rootRepository.GetAllAsync())
                .Select(root => root.Path)
                .ToList();
            var settings = await configurationService.GetApplicationSettingsAsync();
            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                candidates.Add(settings.OutputPath);
            }

            roots = await ResolveDistinctRootsAsync(
                candidates,
                cancellationToken);
        }

        var processed = 0;
        foreach (var root in roots)
        {
            foreach (var stateDirectory in EnumerateStateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (processed++ >= MaxCandidatesPerCycle)
                {
                    return;
                }

                RegistrationPublicationCleanupCandidate? candidate;
                using (var candidateScope = scopeFactory.CreateScope())
                {
                    candidate = candidateScope.ServiceProvider
                        .GetRequiredService<FileMover>()
                        .TryReadRegistrationPublicationCleanupCandidate(
                            stateDirectory);
                }
                if (candidate == null)
                {
                    continue;
                }

                await mutationCoordinator.ExecuteExclusiveAsync(
                    globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                        candidate.AudiobookId,
                        token => ProcessCandidateAsync(candidate, token),
                        globalToken),
                    cancellationToken);
            }
        }
    }

    private async Task ProcessCandidateAsync(
        RegistrationPublicationCleanupCandidate observedCandidate,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var fileMover = scope.ServiceProvider.GetRequiredService<FileMover>();
        var candidate = fileMover.TryReadRegistrationPublicationCleanupCandidate(
            observedCandidate.StateDirectoryPath);
        if (candidate == null
            || candidate.AudiobookId != observedCandidate.AudiobookId
            || !string.Equals(
                candidate.PhysicalObjectIdentity,
                observedCandidate.PhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return;
        }

        var audiobookRepository = scope.ServiceProvider
            .GetRequiredService<IAudiobookRepository>();
        var fileRepository = scope.ServiceProvider
            .GetRequiredService<IAudiobookFileRepository>();
        var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
            candidate.AudiobookId,
            cancellationToken);
        if (audiobook == null)
        {
            if (fileMover.TryRollbackUncommittedRegistrationPublication(
                    candidate))
            {
                logger.LogInformation(
                    "Rolled back abandoned registration publication because audiobook {AudiobookId} no longer exists at {Path}",
                    candidate.AudiobookId,
                    LogRedaction.SanitizeFilePath(candidate.DestinationPath));
            }
            else
            {
                logger.LogWarning(
                    "Preserved registration cleanup state {StatePath} because audiobook {AudiobookId} no longer exists and source-generation rollback could not be proven",
                    LogRedaction.SanitizeFilePath(candidate.StateDirectoryPath),
                    candidate.AudiobookId);
            }
            return;
        }

        var files = await fileRepository.GetByAudiobookIdAsync(
            candidate.AudiobookId,
            cancellationToken);
        var registrationState = await GetRegistrationStateAsync(
            candidate,
            audiobook,
            files,
            cancellationToken);
        if (registrationState == RegistrationGenerationState.Unavailable)
        {
            logger.LogWarning(
                "Preserved registration cleanup state {StatePath} because the committed registration state could not be resolved for audiobook {AudiobookId}",
                LogRedaction.SanitizeFilePath(candidate.StateDirectoryPath),
                candidate.AudiobookId);
            return;
        }
        if (registrationState == RegistrationGenerationState.Conflicting)
        {
            logger.LogWarning(
                "Preserved registration cleanup state {StatePath} because the destination is registered to a different physical generation for audiobook {AudiobookId}",
                LogRedaction.SanitizeFilePath(candidate.StateDirectoryPath),
                candidate.AudiobookId);
            return;
        }
        if (registrationState == RegistrationGenerationState.Absent)
        {
            if (fileMover.TryRollbackUncommittedRegistrationPublication(
                    candidate))
            {
                logger.LogInformation(
                    "Rolled back uncommitted registration publication for audiobook {AudiobookId} at {Path}",
                    candidate.AudiobookId,
                    LogRedaction.SanitizeFilePath(candidate.DestinationPath));
            }
            else
            {
                logger.LogWarning(
                    "Preserved uncommitted registration cleanup state {StatePath} because source-generation rollback could not be proven for audiobook {AudiobookId}",
                    LogRedaction.SanitizeFilePath(candidate.StateDirectoryPath),
                    candidate.AudiobookId);
            }
            return;
        }

        if (fileMover.TryCompleteRegistrationPublicationCleanup(candidate))
        {
            logger.LogInformation(
                "Retired committed registration-publication cleanup state for audiobook {AudiobookId} at {Path}",
                candidate.AudiobookId,
                LogRedaction.SanitizeFilePath(candidate.DestinationPath));
        }
        else
        {
            logger.LogWarning(
                "Registration-publication cleanup remains pending for audiobook {AudiobookId} at {Path}",
                candidate.AudiobookId,
                LogRedaction.SanitizeFilePath(candidate.DestinationPath));
        }
    }

    private async Task<IReadOnlyList<string>> ResolveDistinctRootsAsync(
        IEnumerable<string?> roots,
        CancellationToken cancellationToken)
    {
        var resolvedRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    candidate,
                    out var normalized,
                    out var reason))
            {
                logger.LogDebug(
                    "Skipped unavailable registration cleanup root {Path}: {Reason}",
                    LogRedaction.SanitizeFilePath(candidate),
                    reason);
                continue;
            }

            if (!Directory.Exists(normalized))
            {
                continue;
            }

            var resolution = await semanticsResolver.ResolveAsync(
                normalized,
                cancellationToken: cancellationToken);
            if (resolution.State != PathIdentityState.Valid)
            {
                logger.LogDebug(
                    "Skipped registration cleanup root {Path} because filesystem identity is unavailable: {Reason}",
                    LogRedaction.SanitizeFilePath(normalized),
                    resolution.Reason);
                continue;
            }

            var key = FileSystemPathIdentity.CreateKey(
                "registration-cleanup-root",
                normalized,
                resolution.Semantics);
            resolvedRoots.TryAdd(key, normalized);
        }

        return resolvedRoots.Values.ToArray();
    }

    private static IEnumerable<string> EnumerateStateDirectories(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
            MatchCasing = OperatingSystem.IsWindows()
                ? MatchCasing.CaseInsensitive
                : MatchCasing.CaseSensitive
        };

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(
                    root,
                    ".listenarr-registration-publication-*.state",
                    options)
                .GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }
                    current = enumerator.Current;
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private async Task<RegistrationGenerationState> GetRegistrationStateAsync(
        RegistrationPublicationCleanupCandidate candidate,
        Audiobook audiobook,
        IReadOnlyCollection<AudiobookFile> files,
        CancellationToken cancellationToken)
    {
        var parentPath = Path.GetDirectoryName(candidate.DestinationPath);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return RegistrationGenerationState.Absent;
        }

        var resolution = await semanticsResolver.ResolveAsync(
            parentPath,
            cancellationToken: cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            return RegistrationGenerationState.Unavailable;
        }

        var conflictingPath = false;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            string physicalPath;
            if (!TryResolveRegisteredFilePathForHost(
                    audiobook,
                    file.Path,
                    resolution.Semantics,
                    out physicalPath))
            {
                logger.LogDebug(
                    "Ignored registered audiobook file path that is unavailable on this host while evaluating cleanup candidate {Path}",
                    LogRedaction.SanitizeFilePath(candidate.DestinationPath));
                continue;
            }

            if (!FileSystemPathIdentity.AreEquivalent(
                    physicalPath,
                    candidate.DestinationPath,
                    resolution.Semantics))
            {
                continue;
            }

            if (string.Equals(
                    file.PhysicalObjectIdentity,
                    candidate.PhysicalObjectIdentity,
                    StringComparison.Ordinal))
            {
                return RegistrationGenerationState.Exact;
            }

            conflictingPath = true;
        }

        return conflictingPath
            ? RegistrationGenerationState.Conflicting
            : RegistrationGenerationState.Absent;
    }

    private static bool TryResolveRegisteredFilePathForHost(
        Audiobook audiobook,
        string storedPath,
        FileSystemPathSemantics semantics,
        out string physicalPath)
    {
        if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                storedPath,
                out physicalPath,
                out _))
        {
            return true;
        }

        if (Path.IsPathRooted(storedPath)
            || FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _)
            || string.IsNullOrWhiteSpace(audiobook.BasePath)
            || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                audiobook.BasePath,
                out var basePath,
                out _))
        {
            physicalPath = string.Empty;
            return false;
        }

        return FileSystemPathIdentity.TryResolveRelativePathWithinBase(
            basePath,
            storedPath,
            semantics,
            out physicalPath);
    }

    private enum RegistrationGenerationState
    {
        Absent,
        Exact,
        Conflicting,
        Unavailable
    }
}
