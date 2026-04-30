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
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_History_Timestamp",
                table: "History",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_CompletedAt",
                table: "Downloads",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_DownloadClientId",
                table: "Downloads",
                column: "DownloadClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_Status",
                table: "Downloads",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadProcessingJobs_DownloadId_Status",
                table: "DownloadProcessingJobs",
                columns: new[] { "DownloadId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadProcessingJobs_Status",
                table: "DownloadProcessingJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Audiobooks_LastSearchTime",
                table: "Audiobooks",
                column: "LastSearchTime");

            migrationBuilder.CreateIndex(
                name: "IX_Audiobooks_Monitored",
                table: "Audiobooks",
                column: "Monitored");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_History_Timestamp",
                table: "History");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_CompletedAt",
                table: "Downloads");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_DownloadClientId",
                table: "Downloads");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_Status",
                table: "Downloads");

            migrationBuilder.DropIndex(
                name: "IX_DownloadProcessingJobs_DownloadId_Status",
                table: "DownloadProcessingJobs");

            migrationBuilder.DropIndex(
                name: "IX_DownloadProcessingJobs_Status",
                table: "DownloadProcessingJobs");

            migrationBuilder.DropIndex(
                name: "IX_Audiobooks_LastSearchTime",
                table: "Audiobooks");

            migrationBuilder.DropIndex(
                name: "IX_Audiobooks_Monitored",
                table: "Audiobooks");
        }
    }
}


