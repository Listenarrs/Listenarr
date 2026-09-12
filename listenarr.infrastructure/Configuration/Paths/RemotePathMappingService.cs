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

        // A client's mappings change only when the user edits them, and every queue poll needs the
        // whole set for that client. Serving them from the shared memory cache keeps the
        // steady-state poll off the repository entirely, which matters because the repository and
        // the DbContext behind it are scoped while several clients are polled concurrently from a
        // single scope. Readarr does the same thing in
        // src/NzbDrone.Core/RemotePathMappings/RemotePathMappingService.cs, where All() is served
        // from a cache with a ten second lifetime that add, update and remove clear.
        //
        // The three writers below already removed this key. Only the population was missing, so
        // nothing was ever in the cache and every caller went to the database.
        private static readonly TimeSpan ClientCacheLifetime = TimeSpan.FromSeconds(10);

        public async Task<List<RemotePathMapping>> GetPathMappingByClientAsync(DownloadClientConfiguration client)
        {
            var cacheKey = ClientCacheKey(client.Id);

            if (cache.TryGetValue(cacheKey, out RemotePathMapping[]? cached) && cached is not null)
            {
                return [.. cached];
            }

            var mappings = await remotePathMappingRepository.GetByClientIdAsync(client.Id);

            // Cache a private copy and hand every caller its own list. The repository reads these
            // untracked, so sharing the instances across scopes is safe, but sharing the list
            // itself would let one caller's edit reach the next one.
            cache.Set(cacheKey, mappings.ToArray(), ClientCacheLifetime);

            return mappings;
        }

        private static string ClientCacheKey(string downloadClientId) => $"rpm_client_{downloadClientId}";

        public async Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping)
        {
            mapping.NormalizePaths();
            RequireUsableRemotePath(mapping.RemotePath);
            mapping.CreatedAt = DateTime.UtcNow;
            mapping.UpdatedAt = DateTime.UtcNow;

            var saved = await remotePathMappingRepository.SaveAsync(mapping);

            logger.LogInformation($"Created remote path mapping {saved.Id} for client {saved.DownloadClientId}: {saved.RemotePath} -> {saved.LocalPath}");

            try
            {
                cache.Remove(ClientCacheKey(saved.DownloadClientId));
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
            RequireUsableRemotePath(mapping.RemotePath);
            mapping.CreatedAt = existing.CreatedAt;
            mapping.UpdatedAt = DateTime.UtcNow;

            var saved = await remotePathMappingRepository.SaveAsync(mapping);

            logger.LogInformation(
                "Updated remote path mapping {MappingId} for client {ClientId}: {RemotePath} -> {LocalPath}",
                saved.Id, saved.DownloadClientId, saved.RemotePath, saved.LocalPath);

            try { cache.Remove(ClientCacheKey(saved.DownloadClientId)); }
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

                try { cache.Remove(ClientCacheKey(existing.DownloadClientId)); }
                catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            return deleted;
        }

        public async Task<string> TranslatePathAsync(DownloadClientConfiguration client, string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
            {
                return remotePath;
            }

            return TranslatePath(await GetPathMappingByClientAsync(client), client, remotePath);
        }

        // The mapping lookup and the translation are separated so a caller translating many paths
        // for one client can resolve the mappings once. The repository is scoped and so is the
        // DbContext behind it, so translating a batch in parallel while each call did its own
        // lookup meant concurrent queries on a context that permits one at a time.
        public string TranslatePath(
            IReadOnlyList<RemotePathMapping> mappings,
            DownloadClientConfiguration client,
            string remotePath)
        {
            // TranslatePathAsync always had the mappings in hand. This overload takes them from a
            // caller, so the one contract it adds is that they are actually there. Failing here
            // says which argument was wrong; without it an empty-looking translation just returns
            // the remote path and the caller never learns that nothing was consulted.
            ArgumentNullException.ThrowIfNull(mappings);

            if (string.IsNullOrEmpty(remotePath))
            {
                return remotePath;
            }

            foreach (var mapping in mappings)
            {
                if (!TryGetRemoteSemantics(
                        mapping.RemotePath,
                        out var remoteSemantics))
                {
                    logger.LogWarning(
                        "Remote path mapping {MappingId} has ambiguous or non-absolute remote syntax and was ignored for client {ClientId}",
                        mapping.Id,
                        client.Id);
                    continue;
                }

                if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    mapping.RemotePath,
                    remotePath,
                    remoteSemantics,
                    out var relativePath))
                {
                    continue;
                }

                // Mappings are ordered from most-specific remote root to broadest.
                // Once a mapping owns this remote path, an unusable local side must
                // not silently delegate the same path to a broader mapping.
                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        mapping.LocalPath,
                        out var localRoot,
                        out var localReason))
                {
                    logger.LogWarning(
                        "Remote path mapping {MappingId} owns the reported path but its local root is unavailable on this host for client {ClientId}: {Reason}",
                        mapping.Id,
                        client.Id,
                        localReason);
                    throw new InvalidOperationException(
                        $"The matching remote path mapping {mapping.Id} is unavailable on this host.");
                }

                if (string.IsNullOrEmpty(relativePath))
                {
                    return FileUtils.EnsureTrailingSeparator(localRoot);
                }

                var remoteSeparators = remoteSemantics.Syntax == FileSystemPathSyntax.Windows
                    ? new[] { '\\', '/' }
                    : new[] { '/' };
                var localRelativePath = string.Join(
                    Path.DirectorySeparatorChar,
                    relativePath.Split(remoteSeparators, StringSplitOptions.RemoveEmptyEntries));
                if (FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    localRoot,
                    localRelativePath,
                    FileSystemPathSemantics.CurrentHostDefault,
                    out var mappedPath))
                {
                    return mappedPath;
                }

                logger.LogWarning(
                    "Remote path mapping {MappingId} owns the reported path but produced an unsafe mapped path for client {ClientId}",
                    mapping.Id,
                    client.Id);
                throw new InvalidOperationException(
                    $"The matching remote path mapping {mapping.Id} produced an unsafe local path.");
            }

            return remotePath;
        }

        private static void RequireUsableRemotePath(string remotePath)
        {
            if (!TryGetRemoteSemantics(remotePath, out _))
            {
                throw new ArgumentException(
                    "RemotePath must use an unambiguous absolute Windows or Unix path syntax.",
                    nameof(remotePath));
            }
        }

        private static bool TryGetRemoteSemantics(
            string remotePath,
            out FileSystemPathSemantics semantics)
        {
            if (remotePath.Length >= 3
                && char.IsLetter(remotePath[0])
                && remotePath[1] == ':'
                && remotePath[2] is '/' or '\\'
                || remotePath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                semantics = new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Insensitive);
                return true;
            }

            if (remotePath.StartsWith("//", StringComparison.Ordinal)
                || !remotePath.StartsWith("/", StringComparison.Ordinal))
            {
                semantics = default;
                return false;
            }

            semantics = new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Sensitive);
            return true;
        }
    }
}
