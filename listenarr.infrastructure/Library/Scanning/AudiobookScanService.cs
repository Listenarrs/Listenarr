using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService(
    IAudiobookRepository audiobookRepository,
    IAudiobookFileRepository fileRepository,
    IAudiobookFileService fileService,
    IHistoryRepository historyRepository,
    IMetadataService metadataService,
    IFileSystem fileSystem,
    IFileSystemSemanticsResolver semanticsResolver,
    IScanPathAuthorizationService pathAuthorizationService,
    ILogger<AudiobookScanService> logger,
    IWeakStorageScanCandidateStore? weakStorageScanCandidateStore = null)
    : IAudiobookScanService
{
    public async Task<AudiobookScanResult> ScanAsync(
        AudiobookScanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command.CorrelationId))
        {
            command = command with
            {
                CorrelationId = $"scan:{command.AudiobookId}:{Guid.NewGuid():N}"
            };
        }

        var semantics = await ValidateCommandAsync(command, cancellationToken);
        using var pinnedAuthority = OpenPinnedScanAuthority(command);
        var audiobook = await audiobookRepository.GetForScanAsync(
            command.AudiobookId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Audiobook {command.AudiobookId} no longer exists.");
        var existingFiles = await fileRepository.GetByAudiobookIdAsync(
            audiobook.Id,
            cancellationToken);
        var diagnostics = new List<AudiobookScanDiagnostic>();
        var resolvedExistingPaths = ResolveExistingPaths(
            audiobook,
            existingFiles,
            semantics,
            diagnostics);
        var ownershipMap = await BuildOwnershipMapAsync(
            semantics,
            diagnostics,
            cancellationToken);

        var discovery = ScanFileDiscovery.Discover(
            fileSystem,
            command.ScanRoot,
            audiobook,
            Guid.NewGuid(),
            logger,
            semantics,
            resolvedExistingPaths.Values,
            ownershipMap,
            pinnedAuthority.Root,
            command.ScanPhysicalIdentity.HasDurableGenerationProof);
        discovery = await EnrichWithMetadataAsync(
            command,
            pinnedAuthority,
            discovery,
            audiobook,
            command.ScanRoot,
            semantics,
            resolvedExistingPaths.Values,
            diagnostics,
            cancellationToken);
        diagnostics.AddRange(discovery.Issues.Select(ToDiagnostic));

        // Discovery can take long enough for an external mount, link, or root
        // identity to change. Re-prove the authorized root before any durable
        // mutation is attempted.
        await ValidateCommandAsync(command, cancellationToken);
        ValidateDiscoverySnapshot(command, pinnedAuthority, discovery);

        var previousBasePath = audiobook.BasePath;
        var effectiveBasePath = await ApplyBasePathPlanAsync(
            command,
            audiobook,
            existingFiles,
            discovery,
            semantics,
            diagnostics,
            cancellationToken);
        var trackedPaths = resolvedExistingPaths.Values.ToHashSet(
            semantics.Comparer);
        var attributedPaths = discovery.AttributedFiles.ToHashSet(
            semantics.Comparer);
        var createdCount = await ClaimAttributedFilesAsync(
            command,
            pinnedAuthority,
            audiobook,
            discovery,
            discovery.AttributedFiles
                .Where(path => !trackedPaths.Contains(path))
                .ToList(),
            command.Source,
            cancellationToken);
        var hasDurableAttributedOwnership = createdCount > 0
            || trackedPaths.Any(attributedPaths.Contains);
        if (!hasDurableAttributedOwnership
            && discovery.AttributedFiles.Count > 0)
        {
            var currentFiles = await fileRepository.GetByAudiobookIdAsync(
                audiobook.Id,
                cancellationToken);
            var currentResolvedPaths = ResolveExistingPaths(
                audiobook,
                currentFiles,
                semantics,
                []);
            hasDurableAttributedOwnership = currentResolvedPaths.Values
                .Any(attributedPaths.Contains);
        }

        if (!hasDurableAttributedOwnership
            && discovery.AttributedFiles.Count > 0
            && !PathsEquivalent(previousBasePath, effectiveBasePath, semantics))
        {
            audiobook.BasePath = previousBasePath;
            if (!await audiobookRepository.UpdateAsync(audiobook))
            {
                throw new InvalidOperationException(
                    "The scan BasePath rollback could not be persisted.");
            }

            effectiveBasePath = previousBasePath;
            diagnostics.Add(new AudiobookScanDiagnostic(
                "BasePathRolledBack",
                previousBasePath,
                "No attributed file ownership claim succeeded; the planned BasePath change was rolled back."));
        }

        await ValidateCommandAsync(command, cancellationToken);
        ValidateDiscoverySnapshot(command, pinnedAuthority, discovery);
        var removedFiles = await ReconcileMissingFilesAsync(
            command,
            pinnedAuthority,
            audiobook,
            existingFiles,
            resolvedExistingPaths,
            discovery,
            semantics,
            diagnostics,
            cancellationToken);
        createdCount += await ReconcileLegacyFilePathAsync(
            command,
            pinnedAuthority,
            audiobook,
            discovery,
            semantics,
            diagnostics,
            cancellationToken);

        var refreshed = await audiobookRepository.GetForScanSnapshotAsync(
            audiobook.Id,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Audiobook {audiobook.Id} disappeared before scan completion.");
        return new AudiobookScanResult(
            refreshed,
            discovery.AttributedFiles,
            createdCount,
            removedFiles,
            effectiveBasePath,
            discovery.IsComplete,
            command.AllowReconciliation
                && command.IsAuthoritativeScope
                && command.ScanPhysicalIdentity.HasDurableGenerationProof
                && discovery.CanReconcile,
            diagnostics);
    }

    private async Task<FileSystemPathSemantics> ValidateCommandAsync(
        AudiobookScanCommand command,
        CancellationToken cancellationToken)
    {
        command.ScanIdentity.ValidateForPath(command.ScanRoot);
        var currentAuthorization = await pathAuthorizationService.AuthorizeAsync(
            command.ScanRoot,
            cancellationToken);
        if (!currentAuthorization.IsAuthorized
            || !currentAuthorization.Identity.HasValue
            || !currentAuthorization.PhysicalIdentity.HasValue)
        {
            throw new InvalidOperationException(
                currentAuthorization.Error
                    ?? "The scan path is no longer authorized.");
        }

        if (!HasSameAuthority(
                command.ScanIdentity,
                currentAuthorization.Identity.Value))
        {
            throw new InvalidOperationException(
                "The configured scan-root authority changed after authorization.");
        }

        if (command.ScanPhysicalIdentity
            != currentAuthorization.PhysicalIdentity.Value)
        {
            throw new InvalidOperationException(
                "The physical scan-root generation changed after authorization.");
        }

        if (!fileSystem.DirectoryExists(command.ScanRoot))
        {
            throw new DirectoryNotFoundException(
                "The authoritative scan root no longer exists.");
        }

        if (fileSystem.IsReparsePoint(command.ScanRoot))
        {
            throw new InvalidOperationException(
                "The authoritative scan root became a symbolic link or reparse point.");
        }

        if (command.ScanIdentity.RequestedMode == FileSystemCaseSensitivityMode.Auto)
        {
            var current = await semanticsResolver.ResolveAsync(
                command.ScanIdentity.BoundaryPath,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
            if (current.State != PathIdentityState.Valid
                || current.Semantics != command.ScanIdentity.Semantics)
            {
                throw new InvalidOperationException(
                    "The scan filesystem identity changed after authorization.");
            }
        }

        return command.ScanIdentity.Semantics;
    }

    private static bool HasSameAuthority(
        PathIdentitySnapshot persisted,
        PathIdentitySnapshot current) =>
        persisted.Syntax == current.Syntax
        && persisted.CaseSensitivity == current.CaseSensitivity
        && persisted.RequestedMode == current.RequestedMode
        && FileSystemPathIdentity.AreEquivalent(
            persisted.BoundaryPath,
            current.BoundaryPath,
            current.Semantics);

    private async Task<int> ClaimAttributedFilesAsync(
        AudiobookScanCommand command,
        PinnedScanAuthority pinnedAuthority,
        Audiobook audiobook,
        ScanDiscoveryResult discovery,
        IReadOnlyCollection<string> attributedFiles,
        string source,
        CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var filePath in attributedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateCommandAsync(command, cancellationToken);
            ValidateDiscoveredPathParent(
                command,
                pinnedAuthority,
                discovery,
                filePath);
            try
            {
                using var registrationLease = OpenPinnedMetadataFile(
                    command,
                    pinnedAuthority,
                    discovery,
                    filePath);
                var createdFile = await fileService.EnsureAudiobookFileAsync(
                    audiobook,
                    registrationLease,
                    source,
                    cancellationToken);

                if (createdFile)
                {
                    created++;
                }
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                logger.LogWarning(
                    exception,
                    "Failed to claim attributed audiobook file {Path} for audiobook {AudiobookId}",
                    LogRedaction.SanitizeFilePath(filePath),
                    audiobook.Id);
            }
        }

        return created;
    }

    private static bool PathsEquivalent(
        string? left,
        string? right,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left)
                && string.IsNullOrWhiteSpace(right);
        }

        return FileSystemPathIdentity.AreEquivalent(left, right, semantics);
    }

    private static AudiobookScanDiagnostic ToDiagnostic(ScanDiscoveryIssue issue) =>
        new(
            issue.Kind.ToString(),
            issue.Path,
            issue.Message);
}
