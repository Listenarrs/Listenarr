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

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(ListenArrDbContext))]
    [Migration("20260319101500_RenameAudimetaMetadataSourceToAudible")]
    public partial class RenameAudimetaMetadataSourceToAudible : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ApiConfigurations
                SET Name = 'Audible',
                    BaseUrl = 'https://api.audible.com'
                WHERE Type = 'metadata'
                  AND (
                    Name = 'Audimeta'
                    OR BaseUrl LIKE '%audimeta.de%'
                  );
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ApiConfigurations
                SET Name = 'Audimeta',
                    BaseUrl = 'https://audimeta.de'
                WHERE Type = 'metadata'
                  AND (
                    Name = 'Audible'
                    OR BaseUrl LIKE '%api.audible%'
                  );
            ");
        }
    }
}
