using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations.AutoSync
{
    /// <inheritdoc />
    public partial class SyncModelToCurrent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This is a no-op migration that serves as a marker to sync the EF model with the current state of the codebase.
            // It allows us to capture any model changes that may have occurred since the last explicit migration and ensure the database schema is up to date.
            // If this migration has pending model changes, it will fail during 'dotnet ef migrations add' and prompt the developer to review and create explicit migrations for those changes.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
