using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudiobookVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VerificationStatus",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "VerificationConfidence",
                table: "Audiobooks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedBy",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationMethod",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationTranscript",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationDetailJson",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationOpeningSeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "VerificationClosingSeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "VerificationLowCpuPriority",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "VerificationOnImport",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "VerificationStatus", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "VerificationConfidence", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "VerifiedAt", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "VerifiedBy", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "VerificationMethod", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "VerificationTranscript", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "VerificationDetailJson", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "VerificationOpeningSeconds", table: "ApplicationSettings");
            migrationBuilder.DropColumn(name: "VerificationClosingSeconds", table: "ApplicationSettings");
            migrationBuilder.DropColumn(name: "VerificationLowCpuPriority", table: "ApplicationSettings");
            migrationBuilder.DropColumn(name: "VerificationOnImport", table: "ApplicationSettings");
        }
    }
}
