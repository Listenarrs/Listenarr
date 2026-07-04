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

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Cache
{
    /// <summary>
    /// Cover-resolution cache backed by a dedicated sidecar SQLite database
    /// (config/cache/images/ImageCache.db), independent of the app's main listenarr.db — so it
    /// needs no EF migration and can be deleted/rebuilt freely. A PRIMARY KEY lookup on the
    /// identifier gives near-instant reads. Connections are opened per-operation (pooled) for
    /// thread safety; WAL lets reads and the single writer proceed concurrently.
    /// </summary>
    public sealed class ImageCacheStore : IImageCacheStore
    {
        private readonly ILogger<ImageCacheStore> _logger;
        private readonly string _connectionString;

        public ImageCacheStore(ILogger<ImageCacheStore> logger, IApplicationPathService applicationPathService)
        {
            _logger = logger;
            var dbPath = applicationPathService.ResolveFromConfig("cache", "images", "ImageCache.db");
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = true,
            }.ToString();

            Initialize();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        private void Initialize()
        {
            try
            {
                using var connection = Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS cover_cache (
    identifier        TEXT PRIMARY KEY,
    status            INTEGER NOT NULL,
    relative_path     TEXT,
    source_url        TEXT,
    last_checked_utc  INTEGER NOT NULL,
    thumb_sizes       TEXT
);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ImageCache.db; cover caching disabled for this run.");
            }
        }

        public CoverCacheEntry? TryGet(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return null;
            }

            try
            {
                using var connection = Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT identifier, status, relative_path, source_url, last_checked_utc, thumb_sizes " +
                    "FROM cover_cache WHERE identifier = $id LIMIT 1;";
                cmd.Parameters.AddWithValue("$id", identifier);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                return new CoverCacheEntry(
                    reader.GetString(0),
                    (CoverCacheStatus)reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ImageCache TryGet failed for {Identifier}", identifier);
                return null;
            }
        }

        public void UpsertResolved(string identifier, string relativePath, string? sourceUrl, string? thumbSizes = null)
            => Upsert(identifier, CoverCacheStatus.Resolved, relativePath, sourceUrl, thumbSizes);

        public void UpsertNoCover(string identifier)
            => Upsert(identifier, CoverCacheStatus.NoCover, relativePath: null, sourceUrl: null, thumbSizes: null);

        private void Upsert(string identifier, CoverCacheStatus status, string? relativePath, string? sourceUrl, string? thumbSizes)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return;
            }

            try
            {
                using var connection = Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
INSERT INTO cover_cache (identifier, status, relative_path, source_url, last_checked_utc, thumb_sizes)
VALUES ($id, $status, $path, $url, $ts, $sizes)
ON CONFLICT(identifier) DO UPDATE SET
    status = $status,
    relative_path = $path,
    source_url = $url,
    last_checked_utc = $ts,
    thumb_sizes = $sizes;";
                cmd.Parameters.AddWithValue("$id", identifier);
                cmd.Parameters.AddWithValue("$status", (int)status);
                cmd.Parameters.AddWithValue("$path", (object?)relativePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$url", (object?)sourceUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$sizes", (object?)thumbSizes ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ImageCache Upsert failed for {Identifier}", identifier);
            }
        }

        public void Remove(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return;
            }

            try
            {
                using var connection = Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM cover_cache WHERE identifier = $id;";
                cmd.Parameters.AddWithValue("$id", identifier);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ImageCache Remove failed for {Identifier}", identifier);
            }
        }
    }
}
