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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure
{
    /// <summary>
    /// Design-time factory used by EF tools to create ListenArrDbContext without relying on the application's DI.
    /// Uses a local SQLite file under the repo (listenarr.api/config/database/listenarr.db) by default.
    /// </summary>
    public class ListenArrDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ListenArrDbContext>();

            // Prefer explicit environment variable if set (helps CI or custom paths)
            var dbPath = Environment.GetEnvironmentVariable("LISTENARR_SQLITE_PATH");
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                // Try to resolve repo root deterministically by walking up from the
                // assembly base directory (AppContext.BaseDirectory). This is more
                // reliable than Directory.GetCurrentDirectory() which depends on
                // how the process was launched.
                string? repoRoot = FindRepoRoot();
                if (repoRoot != null)
                {
                    var relativeDbPath = Path.Join(".env", "development", "config", "database", "listenarr.db");
                    var candidate = Path.Join(repoRoot, relativeDbPath);
                    dbPath = Path.GetFullPath(candidate);
                }
                else
                {
                    // Last-resort: fall back to current directory behavior to remain compatible
                    var relativePart = Path.Join("..", "listenarr.api", "config", "database", "listenarr.db");
                    var cwdCandidate = Path.Join(Directory.GetCurrentDirectory(), relativePart);
                    dbPath = Path.GetFullPath(cwdCandidate);
                }
            }

            var migrationsAssembly = typeof(QualityProfileRepository).Assembly.GetName().Name;

            optionsBuilder.UseSqlite($"Data Source={dbPath}", sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(migrationsAssembly);
            });

            return new ListenArrDbContext(optionsBuilder.Options);
        }

        private static string? FindRepoRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    // Look for solution file or listenarr.api folder as sentinel
                    const string slnxName = "listenarr.slnx";
                    const string slnName = "listenarr.sln";
                    const string apiFolderName = "listenarr.api";

                    var slnx = Path.Join(dir.FullName, slnxName);
                    var sln = Path.Join(dir.FullName, slnName);
                    var apiFolder = Path.Join(dir.FullName, apiFolderName);

                    if (File.Exists(slnx) || File.Exists(sln) || Directory.Exists(apiFolder))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                // Nothing is logged here: this is the EF design-time factory, outside the running application.
            }

            return null;
        }
    }
}
