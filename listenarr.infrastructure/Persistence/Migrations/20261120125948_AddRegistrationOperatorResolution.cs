using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationOperatorResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolutionNote",
                table: "FileMutationJournals",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionReviewJson",
                table: "FileMutationJournals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAtUtc",
                table: "FileMutationJournals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                table: "FileMutationJournals",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolutionNote",
                table: "FileMutationJournals");

            migrationBuilder.DropColumn(
                name: "ResolutionReviewJson",
                table: "FileMutationJournals");

            migrationBuilder.DropColumn(
                name: "ResolvedAtUtc",
                table: "FileMutationJournals");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                table: "FileMutationJournals");
        }
    }
}
