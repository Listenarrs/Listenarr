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
using System.Reflection;
using Listenarr.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Listenarr.Tests.Features.Infrastructure.Migrations
{
    public class MigrationMetadataTests
    {
        [Fact]
        public void AddImportBlacklistExtensionsMigration_IsDiscoverableByEf()
        {
            var attribute = typeof(AddImportBlacklistExtensionsToApplicationSettings)
                .GetCustomAttribute<MigrationAttribute>();

            Assert.NotNull(attribute);
            Assert.Equal("20260317123000_AddImportBlacklistExtensionsToApplicationSettings", attribute!.Id);
        }

        [Fact]
        public void AddRootFolderRelocationSkippedItemsMigration_IsDiscoverableByEf()
        {
            var attribute = typeof(AddRootFolderRelocationSkippedItems)
                .GetCustomAttribute<MigrationAttribute>();

            Assert.NotNull(attribute);
            Assert.Equal("20260708224900_AddRootFolderRelocationSkippedItems", attribute!.Id);
        }

        [Fact]
        public void AddLibraryDirectoryOwnershipRootForeignKey_IsDiscoverableAndIsolated()
        {
            var attribute = typeof(AddLibraryDirectoryOwnershipRootForeignKey)
                .GetCustomAttribute<MigrationAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(
                "20260805034058_AddLibraryDirectoryOwnershipRootForeignKey",
                attribute!.Id);

            var migration = new AddLibraryDirectoryOwnershipRootForeignKey();
            var upBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");
            var downBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");
            typeof(AddLibraryDirectoryOwnershipRootForeignKey)
                .GetMethod(
                    "Up",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [upBuilder]);
            typeof(AddLibraryDirectoryOwnershipRootForeignKey)
                .GetMethod(
                    "Down",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [downBuilder]);

            var addForeignKey = Assert.Single(upBuilder.Operations);
            Assert.Equal(
                "FK_LibraryDirectoryOwnerships_RootFolders_ManagedRootFolderId",
                Assert.IsType<AddForeignKeyOperation>(addForeignKey).Name);
            var dropForeignKey = Assert.Single(downBuilder.Operations);
            Assert.Equal(
                "FK_LibraryDirectoryOwnerships_RootFolders_ManagedRootFolderId",
                Assert.IsType<DropForeignKeyOperation>(dropForeignKey).Name);
        }

        [Fact]
        public void OwnershipRecoveryProtocols_ContainsNoRawSqlOperations()
        {
            var migration = new AddOwnershipRecoveryProtocols();
            var upBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");
            var downBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");

            typeof(AddOwnershipRecoveryProtocols)
                .GetMethod(
                    "Up",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [upBuilder]);
            typeof(AddOwnershipRecoveryProtocols)
                .GetMethod(
                    "Down",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [downBuilder]);

            Assert.Empty(upBuilder.Operations.OfType<SqlOperation>());
            Assert.Empty(downBuilder.Operations.OfType<SqlOperation>());
        }
    }
}
