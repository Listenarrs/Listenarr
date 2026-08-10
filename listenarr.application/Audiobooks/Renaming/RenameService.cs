/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Renaming
{
    public partial class RenameService : IRenameService
    {
        private const int MaxAudiobookIds = 500;

        private readonly IConfigurationService _configService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IFileMover _fileMover;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookFileRepository _audiobookFileRepository;
        private readonly IAudiobookFilePathIdentityResolver _filePathIdentityResolver;
        private readonly IFilesystemMutationCoordinator _filesystemMutationCoordinator;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<RenameService> _logger;
        private readonly IRootFolderService? _rootFolderService;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IHistoryRepository? _historyRepository;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly IMoveQueueService _moveQueueService;
        private readonly ILibraryDirectoryOwnershipStore _directoryOwnershipStore;
        private readonly IFileRenameCommitStore _fileRenameCommitStore;

        public RenameService(
            IConfigurationService configService,
            IFileNamingService fileNamingService,
            IFileMover fileMover,
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audiobookFileRepository,
            IAudiobookFilePathIdentityResolver filePathIdentityResolver,
            IFilesystemMutationCoordinator filesystemMutationCoordinator,
            IFileSystem fileSystem,
            ILogger<RenameService> logger,
            IFileSystemSemanticsResolver semanticsResolver,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            IMoveQueueService moveQueueService,
            ILibraryDirectoryOwnershipStore directoryOwnershipStore,
            IFileRenameCommitStore fileRenameCommitStore,
            IRootFolderService? rootFolderService = null,
            IHistoryRepository? historyRepository = null)
        {
            _configService = configService;
            _fileNamingService = fileNamingService;
            _fileMover = fileMover;
            _audiobookRepository = audiobookRepository;
            _audiobookFileRepository = audiobookFileRepository;
            _filePathIdentityResolver = filePathIdentityResolver;
            _filesystemMutationCoordinator = filesystemMutationCoordinator;
            _fileSystem = fileSystem;
            _logger = logger;
            _semanticsResolver = semanticsResolver;
            _rootFolderService = rootFolderService;
            _historyRepository = historyRepository;
            _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _moveQueueService = moveQueueService ?? throw new ArgumentNullException(nameof(moveQueueService));
            _directoryOwnershipStore = directoryOwnershipStore ?? throw new ArgumentNullException(nameof(directoryOwnershipStore));
            _fileRenameCommitStore = fileRenameCommitStore ?? throw new ArgumentNullException(nameof(fileRenameCommitStore));
        }

        public async Task<List<RenamePreview>> PreviewRenameAsync(int[] audiobookIds, CancellationToken ct = default)
        {
            if (audiobookIds == null || audiobookIds.Length == 0) return new();
            if (audiobookIds.Length > MaxAudiobookIds) throw new ArgumentException($"Cannot preview more than {MaxAudiobookIds} audiobooks at once.");

            var settings = await _configService.GetApplicationSettingsAsync();
            var rootFolders = await LoadRootFoldersAsync();

            var audiobooks = await _audiobookRepository.GetByIdsWithFilesAsync(audiobookIds, ct);

            var previews = new List<RenamePreview>();
            foreach (var audiobook in audiobooks)
            {
                previews.Add(await BuildPreviewAsync(audiobook, settings, rootFolders, ct));
            }

            return previews;
        }

        public async Task<List<RenameResult>> ExecuteRenameAsync(List<RenameOperation> operations, CancellationToken ct = default)
        {
            if (operations == null || operations.Count == 0) return new();
            if (operations.Count > MaxAudiobookIds) throw new ArgumentException($"Cannot execute more than {MaxAudiobookIds} rename operations at once.");

            return await _filesystemMutationCoordinator.ExecuteExclusiveAsync(
                globalToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    operations.Select(operation => operation.AudiobookId),
                    async keyedToken =>
                    {
                        foreach (var audiobookId in operations
                            .Select(operation => operation.AudiobookId)
                            .Distinct())
                        {
                            await _moveQueueService.EnsureFilesystemMutationAllowedAsync(
                                audiobookId,
                                keyedToken);
                        }

                        var settings = await _configService.GetApplicationSettingsAsync();
                        var rootFolders = await LoadRootFoldersAsync();
                        var results = new List<RenameResult>(operations.Count);
                        foreach (var operation in operations)
                        {
                            keyedToken.ThrowIfCancellationRequested();
                            results.Add(await ExecuteSingleAsync(
                                operation,
                                settings,
                                rootFolders,
                                keyedToken));
                        }

                        return results;
                    },
                    globalToken),
                ct);
        }

        private async Task<RenamePreview> BuildPreviewAsync(Audiobook audiobook, ApplicationSettings settings, List<RootFolder> rootFolders, CancellationToken ct)
        {
            var currentPathSeed = ComputeCurrentBasePathSeed(audiobook);
            var pathResolution = await ResolveRenamePathResolutionAsync(
                currentPathSeed,
                rootFolders,
                ct);
            var semantics = pathResolution.Semantics;
            var preview = new RenamePreview
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title,
                CurrentFolderPath = ComputeCurrentBasePath(audiobook, semantics),
                CurrentFolderSemantics = ToSnapshot(pathResolution)
            };

            var files = GetFileEntries(audiobook, semantics);
            if (files.Count == 0)
            {
                preview.NewFolderPath = preview.CurrentFolderPath;
                return preview;
            }

            var namingBase = ResolveNamingBasePath(preview.CurrentFolderPath, settings, rootFolders, semantics);
            var isMultiFile = files.Count > 1;
            var expectedPaths = new List<string>();

            foreach (var file in files)
            {
                var expectedPath = BuildExpectedPath(audiobook, file, settings, namingBase.BasePath, namingBase.IsCustomBasePath, isMultiFile);
                expectedPaths.Add(expectedPath);
                preview.FileRenames.Add(new FileRenamePreview
                {
                    FileId = file.FileId,
                    CurrentPath = file.CurrentPath,
                    NewPath = expectedPath,
                    CurrentFilename = Path.GetFileName(file.CurrentPath),
                    NewFilename = Path.GetFileName(expectedPath),
                    Changed = !PathsEqual(file.CurrentPath, expectedPath, semantics)
                });
            }

            preview.NewFolderPath = ComputeCommonBasePath(expectedPaths, semantics);
            preview.FolderChanged = !PathsEqual(preview.CurrentFolderPath, preview.NewFolderPath, semantics);
            preview.HasChanges = preview.FolderChanged || preview.FileRenames.Any(f => f.Changed);
            return preview;
        }

        private async Task<RenameResult> ExecuteSingleAsync(
            RenameOperation operation,
            ApplicationSettings settings,
            List<RootFolder> rootFolders,
            CancellationToken ct)
        {
            try
            {
                var audiobook = await _audiobookRepository.GetByIdAsync(operation.AudiobookId);
                if (audiobook == null)
                {
                    return new RenameResult
                    {
                        AudiobookId = operation.AudiobookId,
                        Error = "Audiobook not found."
                    };
                }

                var currentPathSeed = ComputeCurrentBasePathSeed(audiobook);
                var pathResolution = await ResolveRenamePathResolutionAsync(
                    currentPathSeed,
                    rootFolders,
                    ct);
                var semantics = pathResolution.Semantics;
                var currentBasePath = ComputeCurrentBasePath(audiobook, semantics);
                var folderRequested = !string.IsNullOrWhiteSpace(operation.NewFolderPath);
                var hasFileOperations = operation.FileRenames is { Count: > 0 };
                if ((folderRequested || hasFileOperations)
                    && operation.CurrentFolderSemantics == null)
                {
                    return StalePreviewResult(
                        operation.AudiobookId,
                        "The organize request is missing its expected filesystem semantics.");
                }

                if (operation.CurrentFolderSemantics != null
                    && !SemanticsMatch(
                        operation.CurrentFolderSemantics,
                        pathResolution))
                {
                    return StalePreviewResult(
                        operation.AudiobookId,
                        "The filesystem semantics changed after the organize preview was generated.");
                }
                if (folderRequested && string.IsNullOrWhiteSpace(operation.CurrentFolderPath))
                {
                    return StalePreviewResult(
                        operation.AudiobookId,
                        "The organize request is missing its expected current folder path.");
                }

                if (folderRequested
                    && !PathsEqual(currentBasePath, operation.CurrentFolderPath, semantics))
                {
                    return StalePreviewResult(
                        operation.AudiobookId,
                        "The audiobook folder changed after the organize preview was generated.");
                }

                var allowedRoots = BuildAllowedRoots(
                    settings,
                    rootFolders,
                    currentBasePath,
                    semantics);
                if (string.IsNullOrWhiteSpace(currentBasePath)
                    || !IsPathWithinAllowedRoots(currentBasePath, allowedRoots, semantics))
                {
                    return StalePreviewResult(
                        operation.AudiobookId,
                        "The audiobook is no longer under a currently configured library root.");
                }

                var validationFailure = await ValidateOperationPlanAsync(
                    audiobook,
                    operation,
                    allowedRoots,
                    semantics,
                    ct);
                if (validationFailure != null)
                {
                    return validationFailure;
                }

                // Honor cancellation through complete preflight. Once filesystem mutation
                // can begin, complete or roll back to a stable persisted state.
                var mutationToken = RequestCancellationBoundary.EnterNonCancelablePhase(ct);
                var result = new RenameResult { AudiobookId = operation.AudiobookId };
                var audiobookRollbackState = CaptureAudiobookPathRollbackState(audiobook);
                foreach (var fileOperation in operation.FileRenames ?? [])
                {
                    var fileResult = await ExecuteFileRenameAsync(
                        audiobook,
                        fileOperation,
                        allowedRoots,
                        semantics,
                        mutationToken);
                    result.RenamedFiles.Add(fileResult);
                    if (!fileResult.Success)
                    {
                        _ = await RollBackFileRenamesAsync(
                            audiobook,
                            result.RenamedFiles.Where(item => item.Success).ToList(),
                            audiobookRollbackState,
                            allowedRoots,
                            semantics,
                            mutationToken);
                        try
                        {
                            await CommitRollbackStateAsync(
                                audiobook,
                                result.RenamedFiles,
                                CancellationToken.None);
                        }
                        catch (Exception recoveryException) when (recoveryException is not (
                            OutOfMemoryException or StackOverflowException))
                        {
                            _logger.LogCritical(
                                recoveryException,
                                "Failed to persist organize rollback journal state for audiobook {AudiobookId}",
                                audiobook.Id);
                        }
                        result.Success = false;
                        result.Error = fileResult.Error ?? "One or more file organize operations failed.";
                        return result;
                    }
                }

                result.Success = true;

                if (hasFileOperations || folderRequested)
                {
                    UpdateAudiobookPathSummary(
                        audiobook,
                        folderRequested ? operation.NewFolderPath : null,
                        semantics);
                    try
                    {
                        await CommitSuccessfulRenameStateAsync(
                            audiobook,
                            result.RenamedFiles,
                            mutationToken);
                    }
                    catch (Exception persistenceException) when (persistenceException is not OutOfMemoryException
                        && persistenceException is not StackOverflowException)
                    {
                        var rollbackSucceeded = await RollBackFileRenamesAsync(
                            audiobook,
                            result.RenamedFiles,
                            audiobookRollbackState,
                            allowedRoots,
                            semantics,
                            CancellationToken.None);

                        try
                        {
                            await CommitRollbackStateAsync(
                                audiobook,
                                result.RenamedFiles,
                                CancellationToken.None);
                        }
                        catch (Exception recoveryException) when (recoveryException is not (
                            OutOfMemoryException or StackOverflowException))
                        {
                            _logger.LogCritical(
                                recoveryException,
                                "Failed to persist actual filesystem and rollback journal state after organize persistence failure for audiobook {AudiobookId}",
                                audiobook.Id);
                        }

                        if (persistenceException is OperationCanceledException)
                        {
                            throw;
                        }

                        _logger.LogError(
                            persistenceException,
                            "Failed to persist organize operation for audiobook {AudiobookId}; rollback succeeded={RollbackSucceeded}",
                            audiobook.Id,
                            rollbackSucceeded);
                        result.Success = false;
                        result.Error = rollbackSucceeded
                            ? "The organize operation was rolled back because its database update failed."
                            : "The organize operation partially completed and its actual filesystem state could not be fully persisted.";
                        return result;
                    }

                    await AddHistoryAsync(audiobook, result);
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to execute organize operation for audiobook {AudiobookId}", operation.AudiobookId);
                return new RenameResult
                {
                    AudiobookId = operation.AudiobookId,
                    Success = false,
                    Error = "The organize operation failed. Review the server logs for details."
                };
            }
        }

    }
}
