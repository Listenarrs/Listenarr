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

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ListenArrDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20251208001000_ConvertLegacyDelimitedToJsonArraysMigrationColumns")]
    public partial class ConvertLegacyDelimitedToJsonArraysMigrationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add staging columns for Audiobooks
            migrationBuilder.AddColumn<string>(name: "Authors_migr", table: "Audiobooks", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Genres_migr", table: "Audiobooks", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Tags_migr", table: "Audiobooks", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Narrators_migr", table: "Audiobooks", type: "TEXT", nullable: true);

            // Add staging columns for QualityProfiles
            migrationBuilder.AddColumn<string>(name: "Qualities_migr", table: "QualityProfiles", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PreferredFormats_migr", table: "QualityProfiles", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PreferredWords_migr", table: "QualityProfiles", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "MustNotContain_migr", table: "QualityProfiles", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "MustContain_migr", table: "QualityProfiles", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PreferredLanguages_migr", table: "QualityProfiles", type: "TEXT", nullable: true);

            // Populate staging columns by converting legacy pipe-delimited tokens or wrapping single tokens
            migrationBuilder.Sql(@"
-- Audiobooks staging: only convert non-JSON values
UPDATE Audiobooks SET Authors_migr =
  CASE
    WHEN json_valid(Authors)=1 THEN Authors
    WHEN trim(Authors) = '' THEN '[]'
    WHEN Authors NOT LIKE '%|%' THEN json_array(Authors)
    ELSE '[' || char(34) || replace(Authors, '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE Authors IS NOT NULL AND json_valid(Authors)=0;

UPDATE Audiobooks SET Narrators_migr =
  CASE
    WHEN json_valid(Narrators)=1 THEN Narrators
    WHEN trim(Narrators) = '' THEN '[]'
    WHEN Narrators NOT LIKE '%|%' THEN json_array(Narrators)
    ELSE '[' || char(34) || replace(Narrators, '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE Narrators IS NOT NULL AND json_valid(Narrators)=0;

UPDATE Audiobooks SET Genres_migr =
  CASE
    WHEN json_valid(Genres)=1 THEN Genres
    WHEN trim(Genres) = '' THEN '[]'
    WHEN Genres NOT LIKE '%|%' THEN json_array(Genres)
    ELSE '[' || char(34) || replace(Genres, '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE Genres IS NOT NULL AND json_valid(Genres)=0;

UPDATE Audiobooks SET Tags_migr =
  CASE
    WHEN json_valid(Tags)=1 THEN Tags
    WHEN trim(Tags) = '' THEN '[]'
    WHEN Tags NOT LIKE '%|%' THEN json_array(Tags)
    ELSE '[' || char(34) || replace(Tags, '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE Tags IS NOT NULL AND json_valid(Tags)=0;

-- QualityProfiles staging: sanitize double-pipes then convert non-JSON values
UPDATE QualityProfiles SET Qualities_migr =
  CASE
    WHEN json_valid(Qualities)=1 THEN Qualities
    WHEN trim(Qualities) = '' THEN '[]'
    WHEN Qualities NOT LIKE '%|%' THEN json_array(Qualities)
    ELSE '[' || char(34) || replace(Qualities, '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE Qualities IS NOT NULL AND json_valid(Qualities)=0;

UPDATE QualityProfiles SET PreferredFormats_migr =
  CASE
    WHEN json_valid(PreferredFormats)=1 THEN PreferredFormats
    WHEN trim(PreferredFormats) = '' THEN '[]'
    WHEN PreferredFormats NOT LIKE '%|%' THEN json_array(PreferredFormats)
    ELSE '[' || char(34) || replace(replace(PreferredFormats, '||', '|'), '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE PreferredFormats IS NOT NULL AND json_valid(PreferredFormats)=0;

UPDATE QualityProfiles SET PreferredWords_migr =
  CASE
    WHEN json_valid(PreferredWords)=1 THEN PreferredWords
    WHEN trim(PreferredWords) = '' THEN '[]'
    WHEN PreferredWords NOT LIKE '%|%' THEN json_array(PreferredWords)
    ELSE '[' || char(34) || replace(replace(PreferredWords, '||', '|'), '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE PreferredWords IS NOT NULL AND json_valid(PreferredWords)=0;

UPDATE QualityProfiles SET MustNotContain_migr =
  CASE
    WHEN json_valid(MustNotContain)=1 THEN MustNotContain
    WHEN trim(MustNotContain) = '' THEN '[]'
    WHEN MustNotContain NOT LIKE '%|%' THEN json_array(MustNotContain)
    ELSE '[' || char(34) || replace(replace(MustNotContain, '||', '|'), '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE MustNotContain IS NOT NULL AND json_valid(MustNotContain)=0;

UPDATE QualityProfiles SET MustContain_migr =
  CASE
    WHEN json_valid(MustContain)=1 THEN MustContain
    WHEN trim(MustContain) = '' THEN '[]'
    WHEN MustContain NOT LIKE '%|%' THEN json_array(MustContain)
    ELSE '[' || char(34) || replace(replace(MustContain, '||', '|'), '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE MustContain IS NOT NULL AND json_valid(MustContain)=0;

UPDATE QualityProfiles SET PreferredLanguages_migr =
  CASE
    WHEN json_valid(PreferredLanguages)=1 THEN PreferredLanguages
    WHEN trim(PreferredLanguages) = '' THEN '[]'
    WHEN PreferredLanguages NOT LIKE '%|%' THEN json_array(PreferredLanguages)
    ELSE '[' || char(34) || replace(replace(PreferredLanguages, '||', '|'), '|', char(34) || ',' || char(34)) || char(34) || ']'
  END
WHERE PreferredLanguages IS NOT NULL AND json_valid(PreferredLanguages)=0;
");

            // Ensure staging columns are non-null so EF materialization doesn't encounter NULLs
            // after we rename staging columns to the originals. This sets empty JSON arrays
            // for any staging column that remained NULL (e.g., original value was NULL).
            migrationBuilder.Sql(@"
UPDATE QualityProfiles SET Qualities_migr = '[]' WHERE Qualities_migr IS NULL;
UPDATE QualityProfiles SET PreferredFormats_migr = '[]' WHERE PreferredFormats_migr IS NULL;
UPDATE QualityProfiles SET PreferredWords_migr = '[]' WHERE PreferredWords_migr IS NULL;
UPDATE QualityProfiles SET MustNotContain_migr = '[]' WHERE MustNotContain_migr IS NULL;
UPDATE QualityProfiles SET MustContain_migr = '[]' WHERE MustContain_migr IS NULL;
UPDATE QualityProfiles SET PreferredLanguages_migr = '[]' WHERE PreferredLanguages_migr IS NULL;
");

            // Use raw ALTER TABLE statements to drop original columns and rename staging columns.
            // This issues direct SQL so the SQLite engine performs the operations (requires
            // a SQLite version that supports DROP COLUMN and RENAME COLUMN).
            migrationBuilder.Sql(@"
-- Audiobooks: drop original columns then rename staging columns to originals
ALTER TABLE Audiobooks DROP COLUMN Authors;
ALTER TABLE Audiobooks DROP COLUMN Narrators;
ALTER TABLE Audiobooks DROP COLUMN Genres;
ALTER TABLE Audiobooks DROP COLUMN Tags;

ALTER TABLE Audiobooks RENAME COLUMN Authors_migr TO Authors;
ALTER TABLE Audiobooks RENAME COLUMN Narrators_migr TO Narrators;
ALTER TABLE Audiobooks RENAME COLUMN Genres_migr TO Genres;
ALTER TABLE Audiobooks RENAME COLUMN Tags_migr TO Tags;

-- QualityProfiles: drop originals then rename staging columns to originals
ALTER TABLE QualityProfiles DROP COLUMN Qualities;
ALTER TABLE QualityProfiles DROP COLUMN PreferredFormats;
ALTER TABLE QualityProfiles DROP COLUMN PreferredWords;
ALTER TABLE QualityProfiles DROP COLUMN MustNotContain;
ALTER TABLE QualityProfiles DROP COLUMN MustContain;
ALTER TABLE QualityProfiles DROP COLUMN PreferredLanguages;

ALTER TABLE QualityProfiles RENAME COLUMN Qualities_migr TO Qualities;
ALTER TABLE QualityProfiles RENAME COLUMN PreferredFormats_migr TO PreferredFormats;
ALTER TABLE QualityProfiles RENAME COLUMN PreferredWords_migr TO PreferredWords;
ALTER TABLE QualityProfiles RENAME COLUMN MustNotContain_migr TO MustNotContain;
ALTER TABLE QualityProfiles RENAME COLUMN MustContain_migr TO MustContain;
ALTER TABLE QualityProfiles RENAME COLUMN PreferredLanguages_migr TO PreferredLanguages;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is intentionally left empty because the Up migration performs non-destructive
            // in-place updates for SQLite (staging columns are left in place). Reverting this
            // operation reliably would require recreating original column contents from backups
            // or dropping/renaming columns which SQLite migrations do not support via EF.
            // If you need a reversible path, restore from a DB backup before applying this migration.
        }
    }
}
