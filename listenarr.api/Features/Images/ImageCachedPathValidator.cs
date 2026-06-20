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

namespace Listenarr.Api.Features.Images
{
    internal sealed class ImageCachedPathValidator
    {
        private readonly ImagePathValidator _pathValidator;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public ImageCachedPathValidator(ImagePathValidator pathValidator, IFileSystem fileSystem, ILogger logger)
        {
            _pathValidator = pathValidator;
            _logger = logger;
            _fileSystem = fileSystem;
        }

        public string? ValidateReturnedPath(string identifier, string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return relativePath;
            }

            if (Path.IsPathRooted(relativePath))
            {
                _logger.LogWarning("Image service returned rooted path for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(relativePath));
                return null;
            }

            _logger.LogDebug("ImagesController: initial relativePath for {Identifier}: {RelativePath}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(relativePath));
            try
            {
                var candidateFull = Path.GetFullPath(_pathValidator.ResolvePathWithOptionalBase(relativePath));

                if (!_pathValidator.IsInsidePermittedImageRoot(candidateFull))
                {
                    _logger.LogWarning("Resolved image path outside permitted directories for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateFull));
                    return null;
                }

                if (!TryRejectReparsePoint(
                    identifier,
                    candidateFull,
                    "Rejected reparse-point (symlink) image path for identifier {Identifier}: {Path}",
                    "Failed to inspect candidate image attributes for identifier {Identifier}"))
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to validate image path for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                return null;
            }

            return relativePath;
        }

        public bool IsValidMovedPath(string identifier, string movedRelativePath)
        {
            try
            {
                var movedFull = Path.GetFullPath(_pathValidator.ResolvePathWithOptionalBase(movedRelativePath));

                if (!_pathValidator.IsInsidePermittedImageRoot(movedFull))
                {
                    _logger.LogWarning("Moved image path outside permitted directories for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(movedFull));
                    return false;
                }

                if (!_fileSystem.FileExists(movedFull))
                {
                    _logger.LogWarning("Moved image file does not exist for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(movedFull));
                    return false;
                }

                return TryRejectReparsePoint(
                    identifier,
                    movedFull,
                    "Rejected moved reparse-point (symlink) image path for identifier {Identifier}: {Path}",
                    "Failed to inspect moved image attributes for identifier {Identifier}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to validate moved image path for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                return false;
            }
        }

        private bool TryRejectReparsePoint(
            string identifier,
            string fullPath,
            string reparsePointLogMessage,
            string inspectFailureLogMessage)
        {
            try
            {
                if (_fileSystem.FileExists(fullPath))
                {
                    if (_fileSystem.IsReparsePoint(fullPath))
                    {
                        _logger.LogWarning(reparsePointLogMessage, LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(fullPath));
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, inspectFailureLogMessage, LogRedaction.SanitizeText(identifier));
                return false;
            }
        }
    }
}
