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
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Models
{
    public static class SqlitePragmaInitializer
    {
        public static void ApplyPragmas(DbContext context)
        {
            var conn = context.Database.GetDbConnection();
            if (conn is SqliteConnection sqliteConn)
            {
                if (sqliteConn.State != System.Data.ConnectionState.Open)
                {
                    sqliteConn.Open();
                }

                using var cmd = sqliteConn.CreateCommand();
                cmd.CommandText = @"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA journal_size_limit=6144000;";
                cmd.ExecuteNonQuery();
            }
        }
    }
}

