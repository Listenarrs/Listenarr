/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed class LibraryPreviewPathWorkflow(
    IConfigurationService configurationService,
    IFileNamingService fileNamingService,
    ILogger<LibraryPreviewPathWorkflow> logger)
{
    public async Task<IActionResult> PreviewAsync(LibraryController.PreviewPathRequest request)
    {
        try
        {
            var settings = await configurationService.GetApplicationSettingsAsync();
            var root = !string.IsNullOrEmpty(request.DestinationRoot)
                ? request.DestinationRoot
                : settings.OutputPath;
            var audiobook = request.Metadata.ToAudiobook();

            AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                audiobook,
                request.Metadata.SeriesMemberships,
                request.Metadata.Series,
                request.Metadata.SeriesNumber);

            var namingPattern = !string.IsNullOrWhiteSpace(settings.FolderNamingPattern)
                ? settings.FolderNamingPattern
                : settings.FileNamingPattern;
            var fullPath = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(
                audiobook,
                root ?? string.Empty,
                namingPattern,
                fileNamingService);
            var relativePath = fullPath;
            if (!string.IsNullOrEmpty(root) &&
                fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = fullPath[root.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

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
