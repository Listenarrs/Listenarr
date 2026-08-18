/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed class LibraryPreviewPathWorkflow(
    IConfigurationService configurationService,
    IRootFolderService rootFolderService,
    IFileNamingService fileNamingService,
    ILogger<LibraryPreviewPathWorkflow> logger)
{
    public async Task<IActionResult> PreviewAsync(LibraryController.PreviewPathRequest request)
    {
        try
        {
            var settings = await configurationService.GetApplicationSettingsAsync();
            var explicitRoot = !string.IsNullOrEmpty(request.DestinationRoot);
            var defaultRoot = explicitRoot
                ? null
                : await rootFolderService.GetDefaultAsync();
            var root = explicitRoot
                ? request.DestinationRoot
                : defaultRoot?.Path ?? settings.OutputPath;
            var audiobook = request.Metadata.ToAudiobook();

            AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                audiobook,
                request.Metadata.SeriesMemberships,
                request.Metadata.Series,
                request.Metadata.SeriesNumber);

            var namingPattern = !string.IsNullOrWhiteSpace(settings.FolderNamingPattern)
                ? settings.FolderNamingPattern
                : settings.FileNamingPattern;
            // Preview owns the naming calculation, so preserve the generated relative
            // directory directly instead of re-deriving it from the full path through
            // live filesystem semantics. Read-only preview must not depend on CIFS/NFS/
            // FUSE case-sensitivity probes merely to recover a string it just produced.
            var relativePath = LibraryPathPlanner.ComputeAudiobookRelativeDirectoryFromPattern(
                audiobook,
                namingPattern,
                fileNamingService);
            var fullPath = FileUtils.CombineWithOptionalBase(
                root ?? string.Empty,
                relativePath);

            return new OkObjectResult(new { fullPath, relativePath, root });
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogWarning(exception, "Failed to compute preview path");
            return new ObjectResult(new { message = "Failed to compute preview path" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }
}
