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
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning
{
    internal static class ScanPathPlanner
    {
        public static string CalculateBasePath(
            IReadOnlyCollection<string> filePaths,
            FileSystemPathSemantics semantics,
            string? provenBookBoundary = null,
            string? authorizedScanRoot = null)
        {
            if (filePaths.Count == 0)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(provenBookBoundary))
            {
                var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
                    provenBookBoundary,
                    semantics.Syntax);
                var boundaryIsAuthorized = string.IsNullOrWhiteSpace(authorizedScanRoot)
                    || FileSystemPathIdentity.IsSameOrInside(
                        canonicalBoundary,
                        authorizedScanRoot,
                        semantics);
                if (boundaryIsAuthorized
                    && filePaths.All(path => FileSystemPathIdentity.IsSameOrInside(
                        path,
                        canonicalBoundary,
                        semantics)))
                {
                    return canonicalBoundary;
                }
            }

            var directories = filePaths
                .Select(path => FileSystemPathIdentity.Canonicalize(
                    Path.GetDirectoryName(path) ?? path,
                    semantics.Syntax))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(semantics.Comparer)
                .ToList();
            if (directories.Count == 0)
            {
                return string.Empty;
            }

            return directories.Count == 1
                ? directories[0]
                : FileUtils.GetCommonPathForDirectories(directories, semantics)
                    ?? directories[0];
        }

    }
}
