/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Moving;

public sealed partial class AudiobookDestinationRewriteService : IAudiobookDestinationRewriteService
{
    private readonly IAudiobookRepository _repo;
    private readonly IConfigurationService _configService;
    private readonly IRootFolderService _rootFolderService;
    private readonly IFileSystem _fileSystem;
    private readonly IFileSystemSemanticsResolver _semanticsResolver;
    private readonly IRootFolderRelocationService _relocationService;
    private readonly IFilesystemMutationCoordinator _mutationCoordinator;
    private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
    private readonly ILogger<AudiobookDestinationRewriteService> _logger;

    public AudiobookDestinationRewriteService(
        IAudiobookRepository repo,
        IConfigurationService configService,
        IRootFolderService rootFolderService,
        IFileSystem fileSystem,
        IFileSystemSemanticsResolver semanticsResolver,
        ILogger<AudiobookDestinationRewriteService> logger,
        IRootFolderRelocationService relocationService,
        IFilesystemMutationCoordinator mutationCoordinator,
        IAudiobookOperationCoordinator audiobookOperationCoordinator)
    {
        _repo = repo;
        _configService = configService;
        _rootFolderService = rootFolderService;
        _fileSystem = fileSystem;
        _semanticsResolver = semanticsResolver;
        _logger = logger;
        _relocationService = relocationService ?? throw new ArgumentNullException(nameof(relocationService));
        _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
        _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
    }

    public Task<AudiobookDestinationRewriteResult> RewriteDestinationAsync(
        int audiobookId,
        string destinationPath,
        string? expectedSourcePath,
        CancellationToken cancellationToken = default) =>
        _mutationCoordinator.ExecuteExclusiveAsync(
            lockedCancellationToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookId,
                token => RewriteDestinationCoreAsync(
                    audiobookId,
                    destinationPath,
                    expectedSourcePath,
                    token),
                lockedCancellationToken),
            cancellationToken);

    private async Task<AudiobookDestinationRewriteResult> RewriteDestinationCoreAsync(
        int audiobookId,
        string destinationPath,
        string? expectedSourcePath,
        CancellationToken cancellationToken)
    {
        var destination = await ResolveDestinationAsync(destinationPath, cancellationToken);
        var currentAudiobook = await _repo.GetByIdAsync(audiobookId);
        if (currentAudiobook == null)
        {
            throw new ApplicationNotFoundException("audiobook_not_found", "Audiobook not found");
        }

        var sourceBasePath = currentAudiobook.BasePath;
        MoveRootBoundary? sourceBoundary = null;
        var sourceSemantics = destination.TargetBoundary.Semantics;
        if (!string.IsNullOrWhiteSpace(sourceBasePath))
        {
            sourceBoundary = FindAllowedMoveRoot(
                sourceBasePath,
                destination.AllowedMoveRoots);
            sourceSemantics = sourceBoundary?.Semantics
                ?? ResolveConservativeStoredSourceSemantics(
                    sourceBasePath,
                    destination.TargetBoundary.Semantics);
        }

        if (!string.IsNullOrWhiteSpace(expectedSourcePath))
        {
            if (string.IsNullOrWhiteSpace(sourceBasePath)
                || !StoredSourcePathMatchesExpected(
                    expectedSourcePath,
                    sourceBasePath,
                    sourceBoundary?.Semantics))
            {
                throw new ApplicationConflictException(
                    "source_path_changed",
                    "The audiobook source path changed. Refresh and try again.");
            }
        }

        if (_relocationService != null
            && (await _relocationService.IsBoundaryProtectedAsync(
                    destination.Path,
                    destination.TargetBoundary.Semantics,
                    cancellationToken)
                || (!string.IsNullOrWhiteSpace(sourceBasePath)
                    && await TryIsBoundaryProtectedAsync(
                        sourceBasePath,
                        sourceSemantics,
                        cancellationToken))))
        {
            throw new ApplicationConflictException(
                "move_relocation_conflict",
                "Move source or target overlaps an active root folder relocation boundary.");
        }

        var rewritten = await _repo.RewritePathReferencesAsync(
            audiobookId,
            sourceBasePath,
            destination.Path,
            sourceSemantics,
            destination.TargetBoundary.Semantics,
            cancellationToken,
            destination.TargetBoundary.CaseSensitivityMode);
        if (!rewritten)
        {
            throw new ApplicationNotFoundException("audiobook_not_found", "Audiobook not found");
        }

        _logger.LogInformation(
            "Updated BasePath for audiobook {AudiobookId} without moving files: {BasePath}",
            audiobookId,
            destination.Path);

        return new AudiobookDestinationRewriteResult(audiobookId, destination.Path, sourceBasePath);
    }

    private async Task<ResolvedDestination> ResolveDestinationAsync(
        string? destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(destinationPath))
        {
            throw new ApplicationValidationException(
                "destination_path_required",
                "DestinationPath is required");
        }

        // User-entered destination paths must be validated as Listenarr-owned paths.
        // This deliberately does not apply to download-client-reported source paths,
        // where leading/trailing whitespace can be part of the external filesystem identity.
        if (FileUtils.HasLeadingWhitespaceBeforeRootedPath(destinationPath))
        {
            throw new ApplicationValidationException(
                "destination_path_invalid",
                "DestinationPath is invalid: leading whitespace before an absolute path is not allowed.");
        }

        var rootFolders = await _rootFolderService.GetAllAsync();
        var settings = rootFolders.Count == 0
            ? await _configService.GetApplicationSettingsAsync()
            : null;

        var allowedMoveRoots = new List<MoveRootBoundary>();
        var unavailableConfiguredRoots = new List<UnavailableMoveRoot>();
        // RootFolders are the authoritative managed-storage boundaries. OutputPath is
        // only a compatibility fallback before any root folders have been configured.
        var normalizedOutputPath = rootFolders.Count == 0
            ? TryNormalizeMoveRoot(settings!.OutputPath, "legacy configured output path")
            : null;
        _ = await AddAllowedMoveRootAsync(
            allowedMoveRoots,
            normalizedOutputPath,
            FileSystemCaseSensitivityMode.Auto,
            cancellationToken);

        string? defaultRootPath = null;
        foreach (var rootFolder in rootFolders)
        {
            var normalizedRootPath = TryNormalizeMoveRoot(rootFolder.Path, $"root folder {rootFolder.Id}");
            if (normalizedRootPath == null)
            {
                unavailableConfiguredRoots.Add(new UnavailableMoveRoot(
                    rootFolder,
                    CanonicalPath: null));
                continue;
            }

            var rootAvailable = await AddAllowedMoveRootAsync(
                allowedMoveRoots,
                normalizedRootPath,
                rootFolder.CaseSensitivityMode,
                cancellationToken);
            if (!rootAvailable)
            {
                unavailableConfiguredRoots.Add(new UnavailableMoveRoot(
                    rootFolder,
                    normalizedRootPath));
                continue;
            }
            if (rootFolder.IsDefault && defaultRootPath == null)
            {
                defaultRootPath = normalizedRootPath;
            }
        }

        if (allowedMoveRoots.Count == 0)
        {
            throw new ApplicationValidationException(
                "destination_path_outside_roots",
                "DestinationPath must be inside a configured root folder or output path");
        }

        var destinationIsRooted = Path.IsPathRooted(destinationPath);
        var relativeMoveBase = normalizedOutputPath ?? defaultRootPath ?? allowedMoveRoots.FirstOrDefault()?.Path;
        if (!destinationIsRooted && string.IsNullOrEmpty(relativeMoveBase))
        {
            throw new ApplicationValidationException(
                "destination_path_requires_root",
                "DestinationPath requires a configured root folder or output path");
        }

        var destinationCandidate = destinationIsRooted
            ? destinationPath
            : FileUtils.CombineWithOptionalBase(relativeMoveBase, destinationPath);
        if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
            destinationCandidate,
            out var final,
            out var validationReason,
            rejectParentTraversal: true))
        {
            throw new ApplicationValidationException(
                "destination_path_invalid",
                $"DestinationPath is invalid: {validationReason}");
        }

        if (!_fileSystem.TryValidateMutationTarget(final, allowedMoveRoots.Select(root => root.Path), out final, out var finalReason))
        {
            _logger.LogWarning(
                "Blocked metadata-only destination rewrite: {Destination}. Reason: {Reason}",
                LogRedaction.SanitizeFilePath(final),
                finalReason);
            throw new ApplicationValidationException(
                "destination_path_outside_roots",
                "DestinationPath must be inside a configured root folder or output path");
        }

        var targetBoundary = FindAllowedMoveRoot(final, allowedMoveRoots);
        if (targetBoundary == null
            || UnavailableConfiguredRootOutranksTargetBoundary(
                final,
                targetBoundary,
                unavailableConfiguredRoots))
        {
            throw new ApplicationValidationException(
                "destination_filesystem_identity_unavailable",
                "Destination filesystem identity is unavailable.");
        }

        return new ResolvedDestination(final, targetBoundary, allowedMoveRoots);
    }

    private string? TryNormalizeMoveRoot(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
            path,
            out var normalizedPath,
            out var validationReason))
        {
            return normalizedPath;
        }

        _logger.LogWarning(
            "Skipping invalid move boundary from {Description}: {Reason}",
            description,
            validationReason);
        return null;
    }

    private async Task<bool> AddAllowedMoveRootAsync(
        List<MoveRootBoundary> allowedRoots,
        string? normalizedRoot,
        FileSystemCaseSensitivityMode caseSensitivityMode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return false;
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            normalizedRoot,
            caseSensitivityMode,
            cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            _logger.LogWarning(
                "Skipping move boundary with unavailable filesystem identity: {Path}. Reason: {Reason}",
                LogRedaction.SanitizeFilePath(normalizedRoot),
                resolution.Reason);
            return false;
        }

        var semantics = resolution.Semantics;
        var existingIndex = allowedRoots.FindIndex(root => FileSystemPathIdentity.AreEquivalent(
            root.Path,
            normalizedRoot,
            semantics));
        if (existingIndex >= 0)
        {
            // A configured root-folder override is authoritative when the same path was
            // already contributed by the legacy output-path setting in Auto mode.
            if (caseSensitivityMode != FileSystemCaseSensitivityMode.Auto
                && allowedRoots[existingIndex].CaseSensitivityMode == FileSystemCaseSensitivityMode.Auto)
            {
                allowedRoots[existingIndex] = new MoveRootBoundary(
                    normalizedRoot,
                    semantics,
                    caseSensitivityMode);
            }

            return true;
        }

        allowedRoots.Add(new MoveRootBoundary(
            normalizedRoot,
            semantics,
            caseSensitivityMode));
        return true;
    }

    private async Task<bool> TryIsBoundaryProtectedAsync(
        string path,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _relocationService.IsBoundaryProtectedAsync(path, semantics, cancellationToken);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // Legacy stored source paths can be invalid for the current host. A metadata-only
            // rewrite must still be able to repair the BasePath without source filesystem access.
            return false;
        }
    }

    private static bool StoredSourcePathMatchesExpected(
        string expectedSourcePath,
        string sourceBasePath,
        FileSystemPathSemantics? authoritativeSourceSemantics)
    {
        // ExpectedSourcePath is an optimistic-concurrency token. If the persisted
        // source is no longer beneath a configured authority, no unrelated target
        // filesystem may weaken that token's comparison rules.
        if (!authoritativeSourceSemantics.HasValue)
        {
            return string.Equals(
                expectedSourcePath,
                sourceBasePath,
                StringComparison.Ordinal);
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                expectedSourcePath,
                sourceBasePath,
                authoritativeSourceSemantics.Value);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return string.Equals(
                expectedSourcePath,
                sourceBasePath,
                StringComparison.Ordinal);
        }
    }

    private static FileSystemPathSemantics ResolveConservativeStoredSourceSemantics(
        string sourceBasePath,
        FileSystemPathSemantics targetSemantics)
    {
        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                sourceBasePath,
                out var sourceSyntax))
        {
            // Without an authoritative source boundary, case-sensitive comparison is
            // the conservative choice: it can preserve an uncertain reference, but it
            // cannot broaden a rewrite to a case-distinct path.
            return new FileSystemPathSemantics(
                sourceSyntax,
                FileSystemCaseSensitivity.Sensitive);
        }

        return new FileSystemPathSemantics(
            targetSemantics.Syntax,
            FileSystemCaseSensitivity.Sensitive);
    }

    private static MoveRootBoundary? FindAllowedMoveRoot(
        string path,
        IReadOnlyCollection<MoveRootBoundary> allowedRoots) =>
        allowedRoots
            .Where(root => IsInsideAllowedMoveRoot(path, root))
            .OrderByDescending(root => FileSystemPathIdentity.Canonicalize(
                root.Path,
                root.Semantics.Syntax).Length)
            .FirstOrDefault();

    private static bool IsInsideAllowedMoveRoot(
        string path,
        MoveRootBoundary root)
    {
        try
        {
            return FileSystemPathIdentity.IsSameOrInside(
                path,
                root.Path,
                root.Semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // Invalid legacy audiobook paths are repaired by the rewrite path below rather
            // than being treated as a configured filesystem boundary.
            return false;
        }
    }

    private sealed record UnavailableMoveRoot(
        RootFolder Root,
        string? CanonicalPath);

    private sealed record MoveRootBoundary(
        string Path,
        FileSystemPathSemantics Semantics,
        FileSystemCaseSensitivityMode CaseSensitivityMode);

    private sealed record ResolvedDestination(
        string Path,
        MoveRootBoundary TargetBoundary,
        IReadOnlyCollection<MoveRootBoundary> AllowedMoveRoots);
}
