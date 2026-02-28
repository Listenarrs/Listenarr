using AsyncKeyedLock;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Services;

public class RemotePathMappingService : IRemotePathMappingService
{
    private readonly ListenArrDbContext _context;
    private readonly ILogger<RemotePathMappingService> _logger;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;
    private static readonly AsyncKeyedLocker<string> _locker = new();

    public RemotePathMappingService(
        ListenArrDbContext context,
        ILogger<RemotePathMappingService> logger,
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _context = context;
        _logger = logger;
        _cache = cache;
    }

    public async Task<List<RemotePathMapping>> GetAllAsync()
    {
        return await _context.RemotePathMappings
            .OrderBy(m => m.DownloadClientId)
            .ThenBy(m => m.Name)
            .ToListAsync();
    }

    public async Task<RemotePathMapping?> GetByIdAsync(int id)
    {
        return await _context.RemotePathMappings.FindAsync(id);
    }

    public async Task<List<RemotePathMapping>> GetByClientIdAsync(string downloadClientId)
    {
        if (string.IsNullOrEmpty(downloadClientId)) return new List<RemotePathMapping>();
        var cacheKey = $"rpm_client_{downloadClientId}";

        if (_cache.TryGetValue<List<RemotePathMapping>>(cacheKey, out var mappings))
        {
            return mappings ?? [];
        }

        // Lock by key to prevent multiple concurrent requests from
        // issuing duplicate DB queries (cache stampede). Also use AsNoTracking
        // so we don't cache tracked EF entities tied to a specific DbContext.
        using var _ = await _locker.LockAsync(cacheKey);

        mappings = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);

            var list = await _context.RemotePathMappings
                .AsNoTracking()
                .Where(m => m.DownloadClientId == downloadClientId)
                .OrderByDescending(m => m.RemotePath.Length) // longest first for best match
                .ToListAsync();

            return list;
        });

        return mappings ?? [];
    }

    public async Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping)
    {
        // Normalize paths before saving
        mapping.NormalizePaths();

        mapping.CreatedAt = DateTime.UtcNow;
        mapping.UpdatedAt = DateTime.UtcNow;

        _context.RemotePathMappings.Add(mapping);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Created remote path mapping {MappingId} for client {ClientId}: {RemotePath} -> {LocalPath}",
            mapping.Id, mapping.DownloadClientId, mapping.RemotePath, mapping.LocalPath);
        // Evict cache for this client so subsequent lookups refresh
        try { _cache.Remove($"rpm_client_{mapping.DownloadClientId}"); } catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
        }

        return mapping;
    }

    public async Task<RemotePathMapping> UpdateAsync(RemotePathMapping mapping)
    {
        var existing = await _context.RemotePathMappings.FindAsync(mapping.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Remote path mapping with ID {mapping.Id} not found");
        }

        // Normalize paths before saving
        mapping.NormalizePaths();

        existing.DownloadClientId = mapping.DownloadClientId;
        existing.Name = mapping.Name;
        existing.RemotePath = mapping.RemotePath;
        existing.LocalPath = mapping.LocalPath;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Updated remote path mapping {MappingId} for client {ClientId}: {RemotePath} -> {LocalPath}",
            mapping.Id, mapping.DownloadClientId, mapping.RemotePath, mapping.LocalPath);
        // Evict cache for this client so subsequent lookups refresh
        try { _cache.Remove($"rpm_client_{mapping.DownloadClientId}"); } catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
        }

        return existing;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var mapping = await _context.RemotePathMappings.FindAsync(id);
        if (mapping == null)
        {
            return false;
        }

        _context.RemotePathMappings.Remove(mapping);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Deleted remote path mapping {MappingId} for client {ClientId}",
            id, mapping.DownloadClientId);
        // Evict cache for this client
        try { _cache.Remove($"rpm_client_{mapping.DownloadClientId}"); } catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { 
            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
        }

        return true;
    }

    public async Task<string> TranslatePathAsync(string downloadClientId, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return remotePath;
        }

        // Normalize the input path for consistent comparison
        var normalizedRemotePath = NormalizePath(remotePath);

        // Get all mappings for this client, ordered by path length (longest first)
        // This ensures we match the most specific path first
        // Use cached mapping list per client to avoid frequent DB queries
        var mappings = await GetByClientIdAsync(downloadClientId);

        foreach (var mapping in mappings)
        {
            var normalizedMappingPath = NormalizePath(mapping.RemotePath);

            // Check if the remote path starts with this mapping's remote path
            if (normalizedRemotePath.StartsWith(normalizedMappingPath, StringComparison.OrdinalIgnoreCase))
            {
                // Replace the remote path prefix with the local path
                var relativePath = normalizedRemotePath.Substring(normalizedMappingPath.Length);
                var localPath = NormalizePath(mapping.LocalPath) + relativePath;

                _logger.LogDebug(
                    "Translated path for client {ClientId}: {RemotePath} -> {LocalPath} (using mapping {MappingId})",
                    downloadClientId, remotePath, localPath, mapping.Id);

                return localPath;
            }
        }

        _logger.LogDebug(
            "No path mapping found for client {ClientId} and path {RemotePath}, returning original path",
            downloadClientId, remotePath);

        // No mapping found, return original path
        return remotePath;
    }

    public async Task<bool> RequiresTranslationAsync(string downloadClientId, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return false;
        }

        var normalizedRemotePath = NormalizePath(remotePath);

        // Use cached mappings to avoid hitting the database on every check.
        var mappings = await GetByClientIdAsync(downloadClientId);
        foreach (var m in mappings)
        {
            if (normalizedRemotePath.StartsWith(NormalizePath(m.RemotePath)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalize a path for consistent comparison:
    /// - Convert backslashes to forward slashes
    /// - Ensure trailing slash
    /// - Trim whitespace
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        path = path.Trim().Replace('\\', '/');

        // Ensure trailing slash for directory paths
        if (!path.EndsWith('/'))
        {
            path += '/';
        }

        return path;
    }
}

