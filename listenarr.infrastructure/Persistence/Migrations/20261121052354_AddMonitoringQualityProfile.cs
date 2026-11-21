using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringQualityProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QualityProfileId",
                table: "MonitoredSeries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QualityProfileId",
                table: "MonitoredAuthors",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredSeries_QualityProfileId",
                table: "MonitoredSeries",
                column: "QualityProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredAuthors_QualityProfileId",
                table: "MonitoredAuthors",
                column: "QualityProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MonitoredSeries_QualityProfileId",
                table: "MonitoredSeries");

            migrationBuilder.DropIndex(
                name: "IX_MonitoredAuthors_QualityProfileId",
                table: "MonitoredAuthors");

            migrationBuilder.DropColumn(
                name: "QualityProfileId",
                table: "MonitoredSeries");

            migrationBuilder.DropColumn(
                name: "QualityProfileId",
                table: "MonitoredAuthors");
        }
    }
}
