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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Configuration.Paths
{
    public class RemotePathMappingService(
        IRemotePathMappingRepository remotePathMappingRepository,
        ILogger<RemotePathMappingService> logger,
        IMemoryCache cache) : IRemotePathMappingService
    {
        public async Task<List<RemotePathMapping>> GetAllAsync()
        {
            var all = await remotePathMappingRepository.GetAllAsync();
            return all.OrderBy(m => m.DownloadClientId).ThenBy(m => m.Name).ToList();
        }

        public async Task<RemotePathMapping?> GetByIdAsync(int id)
        {
            return await remotePathMappingRepository.GetByIdAsync(id);
        }

        public async Task<List<RemotePathMapping>> GetPathMappingByClientAsync(DownloadClientConfiguration client)
        {
            return await remotePathMappingRepository.GetByClientIdAsync(client.Id);
        }

        public async Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping)
        {
            mapping.NormalizePaths();
            mapping.CreatedAt = DateTime.UtcNow;
            mapping.UpdatedAt = DateTime.UtcNow;

            var saved = await remotePathMappingRepository.SaveAsync(mapping);

            logger.LogInformation($"Created remote path mapping {saved.Id} for client {saved.DownloadClientId}: {saved.RemotePath} -> {saved.LocalPath}");

            try
            {
                cache.Remove($"rpm_client_{saved.DownloadClientId}");
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            return saved;
        }

        public async Task<RemotePathMapping> UpdateAsync(RemotePathMapping mapping)
        {
            var existing = await remotePathMappingRepository.GetByIdAsync(mapping.Id);
            if (existing == null)
            {
                throw new KeyNotFoundException($"Remote path mapping with ID {mapping.Id} not found");
            }

            mapping.NormalizePaths();
            mapping.CreatedAt = existing.CreatedAt;
            mapping.UpdatedAt = DateTime.UtcNow;

            var saved = await remotePathMappingRepository.SaveAsync(mapping);

            logger.LogInformation(
                "Updated remote path mapping {MappingId} for client {ClientId}: {RemotePath} -> {LocalPath}",
                saved.Id, saved.DownloadClientId, saved.RemotePath, saved.LocalPath);

            try { cache.Remove($"rpm_client_{saved.DownloadClientId}"); }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            return saved;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var existing = await remotePathMappingRepository.GetByIdAsync(id);
            if (existing == null) return false;

            var deleted = await remotePathMappingRepository.DeleteAsync(id);

            if (deleted)
            {
                logger.LogInformation(
                    "Deleted remote path mapping {MappingId} for client {ClientId}",
                    id, existing.DownloadClientId);

                try { cache.Remove($"rpm_client_{existing.DownloadClientId}"); }
                catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            return deleted;
        }

        public async Task<string> TranslatePathAsync(DownloadClientConfiguration client, string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return remotePath;
            }

            remotePath = FileUtils.NormalizeStoredPath(remotePath);

            // We cannot make sure the given path is a file or a directory as it is possibly unnaccessible in its current form
            // thus we try the mapping on the unmodified given path and then we try to map as if it were a directory
            string[] tryingRemotePaths = [
                remotePath,
                FileUtils.EnsureTrailingSeparator(remotePath)
            ];

            foreach (var currentRemotePath in tryingRemotePaths)
            {
                var mappings = await GetPathMappingByClientAsync(client);
                foreach (var mapping in mappings)
                {
                    if (!FileUtils.IsPathSameOrInside(currentRemotePath, mapping.RemotePath))
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(mapping.RemotePath, currentRemotePath);
                    if (string.Equals(relativePath, ".", StringComparison.Ordinal))
                    {
                        return FileUtils.NormalizeStoredPath(mapping.LocalPath);
                    }

                    if (FileUtils.TryResolveRelativePathWithinBase(mapping.LocalPath, relativePath, out var mappedPath))
                    {
                        return mappedPath;
                    }

                    logger.LogWarning(
                        "Remote path mapping {MappingId} produced an unsafe mapped path for client {ClientId}",
                        mapping.Id,
                        client.Id);
                }
            }

            return remotePath;
        }
    }
}
