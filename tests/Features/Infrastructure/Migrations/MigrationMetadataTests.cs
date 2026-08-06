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
        public void AddMarkerlessMoveExecutionState_IsDiscoverableAndContainsOnlyExpectedColumns()
        {
            var attribute = typeof(AddMarkerlessMoveExecutionState)
                .GetCustomAttribute<MigrationAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(
                "20260805192525_AddMarkerlessMoveExecutionState",
                attribute!.Id);

            var migration = new AddMarkerlessMoveExecutionState();
            var upBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");
            var downBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");
            typeof(AddMarkerlessMoveExecutionState)
                .GetMethod(
                    "Up",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [upBuilder]);
            typeof(AddMarkerlessMoveExecutionState)
                .GetMethod(
                    "Down",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [downBuilder]);

            var expectedColumns = new[]
            {
                "DirectoryObjectIdentity",
                "ExecutionProtocolVersion",
                "SourceDirectoryCleanupState",
                "SourceDirectoryObjectIdentity",
                "SourcePhysicalObjectIdentity",
                "TargetDirectoryObjectIdentity",
                "TargetPhysicalObjectIdentity"
            };
            Assert.Equal(
                expectedColumns,
                upBuilder.Operations
                    .Select(operation => Assert.IsType<AddColumnOperation>(operation).Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
            Assert.Equal(
                expectedColumns,
                downBuilder.Operations
                    .Select(operation => Assert.IsType<DropColumnOperation>(operation).Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
        }

        [Fact]
        public void AddMarkerlessFileMutationJournal_IsDiscoverableAndIsolated()
        {
            var attribute = typeof(AddMarkerlessFileMutationJournal)
                .GetCustomAttribute<MigrationAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(
                "20260805202154_AddMarkerlessFileMutationJournal",
                attribute!.Id);

            var migration = new AddMarkerlessFileMutationJournal();
            var upBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");
            var downBuilder = new MigrationBuilder(
                "Microsoft.EntityFrameworkCore.Sqlite");
            typeof(AddMarkerlessFileMutationJournal)
                .GetMethod(
                    "Up",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [upBuilder]);
            typeof(AddMarkerlessFileMutationJournal)
                .GetMethod(
                    "Down",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(migration, [downBuilder]);

            var create = Assert.Single(
                upBuilder.Operations.OfType<CreateTableOperation>());
            Assert.Equal("FileMutationJournals", create.Name);
            Assert.Equal(
                [
                    "Action",
                    "AudiobookId",
                    "CreatedAt",
                    "DestinationPath",
                    "Error",
                    "OperationId",
                    "ProtocolVersion",
                    "SourceLength",
                    "SourcePath",
                    "SourcePhysicalObjectIdentity",
                    "SourceSha256",
                    "State",
                    "TargetPhysicalObjectIdentity",
                    "UpdatedAt"
                ],
                create.Columns
                    .Select(column => column.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
            Assert.Equal(2, upBuilder.Operations.OfType<CreateIndexOperation>().Count());
            Assert.Equal(3, upBuilder.Operations.Count);
            Assert.Equal(
                "FileMutationJournals",
                Assert.Single(downBuilder.Operations.OfType<DropTableOperation>()).Name);
            Assert.Single(downBuilder.Operations);
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
