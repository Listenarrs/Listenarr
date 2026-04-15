using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeStoredPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE Audiobooks
                SET BasePath = RTRIM(TRIM(BasePath), '/\')
                WHERE BasePath IS NOT NULL AND BasePath != RTRIM(TRIM(BasePath), '/\');

                UPDATE ApplicationSettings
                SET OutputPath = RTRIM(TRIM(OutputPath), '/\')
                WHERE OutputPath != RTRIM(TRIM(OutputPath), '/\');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
