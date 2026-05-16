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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(ListenArrDbContext))]
    [Migration("20260318173000_AddAuthorCacheEntries")]
    public partial class AddAuthorCacheEntries : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthorCacheEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AuthorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthorNameNormalized = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthorAsin = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Region = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    SimilarAuthors = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogBooks = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastFetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorCacheEntries_AuthorAsin_Region",
                table: "AuthorCacheEntries",
                columns: new[] { "AuthorAsin", "Region" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorCacheEntries_AuthorNameNormalized_Region",
                table: "AuthorCacheEntries",
                columns: new[] { "AuthorNameNormalized", "Region" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthorCacheEntries");
        }
    }
}
