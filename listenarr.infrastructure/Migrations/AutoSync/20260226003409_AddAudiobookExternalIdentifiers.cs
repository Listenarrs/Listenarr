using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations.AutoSync
{
    /// <inheritdoc />
    public partial class AddAudiobookExternalIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudiobookExternalIdentifiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ValueRaw = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ValueNormalized = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Region = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookExternalIdentifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookExternalIdentifiers_Audiobooks_AudiobookId",
                        column: x => x.AudiobookId,
                        principalTable: "Audiobooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Backfill legacy identifier fields so existing libraries immediately gain the
            // new typed identifier model without user intervention.
            migrationBuilder.Sql("""
                INSERT INTO "AudiobookExternalIdentifiers"
                    ("AudiobookId", "Type", "ValueRaw", "ValueNormalized", "Region", "IsPrimary", "Source", "CreatedAt", "UpdatedAt")
                SELECT
                    a."Id",
                    'Asin',
                    trim(a."Asin"),
                    upper(trim(a."Asin")),
                    NULL,
                    1,
                    'Imported',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "Audiobooks" a
                WHERE a."Asin" IS NOT NULL
                  AND trim(a."Asin") <> '';
                """);

            migrationBuilder.Sql("""
                INSERT INTO "AudiobookExternalIdentifiers"
                    ("AudiobookId", "Type", "ValueRaw", "ValueNormalized", "Region", "IsPrimary", "Source", "CreatedAt", "UpdatedAt")
                SELECT
                    a."Id",
                    'Isbn',
                    trim(CAST(j.value AS TEXT)),
                    upper(replace(replace(trim(CAST(j.value AS TEXT)), '-', ''), ' ', '')),
                    NULL,
                    0,
                    'Imported',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "Audiobooks" a
                JOIN json_each(a."Isbn") j
                WHERE a."Isbn" IS NOT NULL
                  AND json_valid(a."Isbn") = 1
                  AND trim(CAST(j.value AS TEXT)) <> '';
                """);

            migrationBuilder.Sql("""
                INSERT INTO "AudiobookExternalIdentifiers"
                    ("AudiobookId", "Type", "ValueRaw", "ValueNormalized", "Region", "IsPrimary", "Source", "CreatedAt", "UpdatedAt")
                SELECT
                    a."Id",
                    'OpenLibraryId',
                    trim(a."OpenLibraryId"),
                    upper(trim(a."OpenLibraryId")),
                    NULL,
                    1,
                    'Imported',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "Audiobooks" a
                WHERE a."OpenLibraryId" IS NOT NULL
                  AND trim(a."OpenLibraryId") <> '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookExternalIdentifiers_AudiobookId",
                table: "AudiobookExternalIdentifiers",
                column: "AudiobookId");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookExternalIdentifiers_AudiobookId_Type_IsPrimary",
                table: "AudiobookExternalIdentifiers",
                columns: new[] { "AudiobookId", "Type", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookExternalIdentifiers_Type_ValueNormalized",
                table: "AudiobookExternalIdentifiers",
                columns: new[] { "Type", "ValueNormalized" });

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookExternalIdentifiers_Type_ValueNormalized_Region",
                table: "AudiobookExternalIdentifiers",
                columns: new[] { "Type", "ValueNormalized", "Region" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudiobookExternalIdentifiers");
        }
    }
}
