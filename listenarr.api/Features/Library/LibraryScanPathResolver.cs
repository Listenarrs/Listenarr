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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryScanPathResolver
    {
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<LibraryScanPathResolver> _logger;
        private readonly IRootFolderService? _rootFolderService;

        public LibraryScanPathResolver(
            IConfigurationService configurationService,
            ILogger<LibraryScanPathResolver> logger,
            IRootFolderService? rootFolderService = null)
        {
            _configurationService = configurationService;
            _logger = logger;
            _rootFolderService = rootFolderService;
        }

        public async Task<LibraryScanPathResolution> ResolveAsync(Audiobook audiobook, string? requestedPath)
        {
            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();

                if (!string.IsNullOrEmpty(audiobook.BasePath))
                {
                    var basePath = Path.GetFullPath(audiobook.BasePath);
                    _logger.LogDebug("Audiobook has BasePath; using it as scan root: {ScanRoot}", LogRedaction.SanitizeFilePath(basePath));
                    return LibraryScanPathResolution.Success(basePath);
                }

                if (!string.IsNullOrEmpty(requestedPath))
                {
                    string requestedFull;
                    try
                    {
                        requestedFull = Path.GetFullPath(requestedPath);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Invalid requested scan path provided: {Path}", LogRedaction.SanitizeFilePath(requestedPath));
                        return LibraryScanPathResolution.Failure(new BadRequestObjectResult(new { message = "Invalid scan path", path = requestedPath }));
                    }

                    var allowedRoots = await BuildAllowedRootsAsync(settings?.OutputPath);
                    if (allowedRoots.Count == 0)
                    {
                        _logger.LogWarning("Scan request path provided but no root folders are configured; rejecting request.");
                        return LibraryScanPathResolution.Failure(new BadRequestObjectResult(new { message = "No root folders configured; cannot accept explicit scan path" }));
                    }

                    var allowed = allowedRoots.Any(root => IsPathUnderRoot(requestedFull, root));
                    if (!allowed)
                    {
                        _logger.LogWarning("Requested scan path {Path} is not inside configured root folders", LogRedaction.SanitizeFilePath(requestedPath));
                        return LibraryScanPathResolution.Failure(new BadRequestObjectResult(new { message = "Requested scan path is not within configured root folders", path = requestedPath }));
                    }

                    return LibraryScanPathResolution.Success(requestedFull);
                }

                var outputPath = !string.IsNullOrEmpty(settings?.OutputPath)
                    ? Path.GetFullPath(settings.OutputPath)
                    : null;
                return LibraryScanPathResolution.Success(outputPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to read application settings for scan; cannot validate request path without configured roots");
                if (!string.IsNullOrEmpty(audiobook.BasePath))
                {
                    return LibraryScanPathResolution.Success(Path.GetFullPath(audiobook.BasePath));
                }

                _logger.LogWarning("Configuration unavailable and audiobook has no BasePath; rejecting scan request for audiobook {AudiobookId}", audiobook.Id);
                return LibraryScanPathResolution.Failure(new ObjectResult(new { message = "Failed to determine a safe scan path" })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                });
            }
        }

        private async Task<List<string>> BuildAllowedRootsAsync(string? outputPath)
        {
            var allowedRoots = new List<string>();
            if (_rootFolderService != null)
            {
                var roots = await _rootFolderService.GetAllAsync();
                foreach (var root in roots)
                {
                    TryAddAllowedRoot(allowedRoots, root.Path, "root folder path");
                }
            }

            if (!string.IsNullOrEmpty(outputPath))
            {
                TryAddAllowedRoot(allowedRoots, outputPath, "output path");
            }

            return allowedRoots;
        }

        private void TryAddAllowedRoot(List<string> allowedRoots, string? path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                allowedRoots.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (
                ex is ArgumentException
                || ex is NotSupportedException
                || ex is PathTooLongException
                || ex is System.Security.SecurityException)
            {
                _logger.LogDebug(ex, "Skipping invalid {Label} during scan allowlist build: {Path}", label, LogRedaction.SanitizeFilePath(path));
            }
        }

        private static bool IsPathUnderRoot(string requestedPath, string allowedRoot)
        {
            var trimmedDirectoryRoot = allowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var trimmedAltRoot = allowedRoot.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar;

            return string.Equals(requestedPath, allowedRoot, StringComparison.OrdinalIgnoreCase)
                   || requestedPath.StartsWith(trimmedDirectoryRoot, StringComparison.OrdinalIgnoreCase)
                   || requestedPath.StartsWith(trimmedAltRoot, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed record LibraryScanPathResolution(string? ScanRoot, IActionResult? ErrorResult)
    {
        public static LibraryScanPathResolution Success(string? scanRoot) => new(scanRoot, null);

        public static LibraryScanPathResolution Failure(IActionResult errorResult) => new(null, errorResult);
    }
}
