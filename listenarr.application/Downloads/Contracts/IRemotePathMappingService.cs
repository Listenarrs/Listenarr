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

namespace Listenarr.Application.Downloads.Contracts;

/// <summary>
/// Service for managing remote path mappings between download clients and Listenarr.
/// Used to translate file paths when download clients are in different containers/systems
/// with different mount points than Listenarr.
/// </summary>
public interface IRemotePathMappingService
{
    /// <summary>
    /// Get all remote path mappings
    /// </summary>
    Task<List<RemotePathMapping>> GetAllAsync();

    /// <summary>
    /// Get a specific remote path mapping by ID
    /// </summary>
    Task<RemotePathMapping?> GetByIdAsync(int id);

    /// <summary>
    /// Get all remote path mappings for a specific download client
    /// </summary>
    Task<List<RemotePathMapping>> GetPathMappingByClientAsync(DownloadClientConfiguration client);

    /// <summary>
    /// Create a new remote path mapping
    /// </summary>
    Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping);

    /// <summary>
    /// Update an existing remote path mapping
    /// </summary>
    Task<RemotePathMapping> UpdateAsync(RemotePathMapping mapping);

    /// <summary>
    /// Delete a remote path mapping
    /// </summary>
    Task<bool> DeleteAsync(int id);

    /// <summary>
    /// Translate a remote path from a download client to a local path for Listenarr.
    /// Finds the best matching path mapping for the given client and applies it.
    /// </summary>
    /// <param name="client">The download client reporting the path</param>
    /// <param name="remotePath">The path as reported by the download client</param>
    /// <returns>The translated local path, or the original path if no mapping matches.</returns>
    /// <exception cref="InvalidOperationException">
    /// A matching mapping exists but its local side is unavailable or unsafe on this host.
    /// </exception>
    Task<string> TranslatePathAsync(DownloadClientConfiguration client, string remotePath);

    /// <summary>
    /// Translates a remote path using mappings the caller has already resolved.
    /// </summary>
    /// <remarks>
    /// For callers translating many paths for one client. Resolving the mappings once and
    /// translating from them keeps a parallel batch off the scoped repository, and so off the
    /// scoped DbContext behind it, which permits one operation at a time.
    /// </remarks>
    string TranslatePath(
        IReadOnlyList<RemotePathMapping> mappings,
        DownloadClientConfiguration client,
        string remotePath);
}
