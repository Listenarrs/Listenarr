/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Reflection;
using Listenarr.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

public class MigrationMetadataTests
{
    [Fact]
    public void AddImportBlacklistExtensionsMigration_IsDiscoverableByEf()
    {
        AssertMigrationId<AddImportBlacklistExtensionsToApplicationSettings>(
            "20260317123000_AddImportBlacklistExtensionsToApplicationSettings");
    }

    [Fact]
    public void AddMoveJobSourcePathHistoryRepair_IsDiscoverableByEf()
    {
        AssertMigrationId<AddMoveJobSourcePath>(
            "20251124102000_AddMoveJobSourcePath");
    }

    [Fact]
    public void AddProcessExecutionLogsHistoryRepair_IsDiscoverableAndPreservesCanaryModel()
    {
        AssertMigrationId<AddProcessExecutionLogs>(
            "20260809121006_AddProcessExecutionLogs");

        var migration = new AddProcessExecutionLogs();
        var upBuilder = BuildOperations(migration, "Up");
        var downBuilder = BuildOperations(migration, "Down");

        var create = Assert.Single(upBuilder.Operations.OfType<CreateTableOperation>());
        Assert.Equal("ProcessExecutionLogs", create.Name);
        Assert.Single(upBuilder.Operations);

        var drop = Assert.Single(downBuilder.Operations.OfType<DropTableOperation>());
        Assert.Equal("ProcessExecutionLogs", drop.Name);
        Assert.Single(downBuilder.Operations);

        var model = migration.TargetModel;
        var applicationSettings = AssertEntity(
            model,
            "Listenarr.Domain.Configuration.ApplicationSettings");
        Assert.NotNull(applicationSettings.FindProperty("Version"));

        var download = AssertEntity(model, "Listenarr.Domain.Downloads.Download");
        Assert.NotNull(download.FindProperty("ActiveAudiobookDeduplicationKey"));

        var importJob = AssertEntity(
            model,
            "Listenarr.Domain.Downloads.DownloadProcessingJob");
        Assert.NotNull(importJob.FindProperty("ActiveDeduplicationKey"));
    }

    [Fact]
    public void AddDurableMarkerlessLibraryMoves_IsDiscoverableAndConsolidated()
    {
        AssertMigrationId<AddDurableMarkerlessLibraryMoves>(
            "20260809141455_AddDurableMarkerlessLibraryMoves");

        var migration = new AddDurableMarkerlessLibraryMoves();
        var upBuilder = BuildOperations(migration, "Up");
        var downBuilder = BuildOperations(migration, "Down");

        Assert.Equal(78, upBuilder.Operations.Count);
        Assert.Equal(59, downBuilder.Operations.Count);
        Assert.Empty(upBuilder.Operations.OfType<SqlOperation>());

        var createdTables = upBuilder.Operations
            .OfType<CreateTableOperation>()
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("FileMutationJournals", createdTables);
        Assert.Contains("LibraryDirectoryOwnerships", createdTables);
        Assert.Contains("MoveJobEntries", createdTables);
        Assert.Contains("MoveScanHandoffs", createdTables);
        Assert.Contains("RootFolderRelocations", createdTables);
        Assert.DoesNotContain("LibraryDirectoryOwnershipRetiredMarkers", createdTables);

        Assert.Empty(upBuilder.Operations.OfType<AddForeignKeyOperation>());
        Assert.DoesNotContain(upBuilder.Operations, operation =>
            operation is DropTableOperation or DropColumnOperation);
    }

    [Fact]
    public void AddMoveJobRelocationForeignKey_IsDiscoverableAndIsolated()
    {
        AssertMigrationId<AddMoveJobRelocationForeignKey>(
            "20260809153711_AddMoveJobRelocationForeignKey");

        var migration = new AddMoveJobRelocationForeignKey();
        var upBuilder = BuildOperations(migration, "Up");
        var downBuilder = BuildOperations(migration, "Down");

        var add = Assert.Single(upBuilder.Operations.OfType<AddForeignKeyOperation>());
        Assert.Equal("FK_MoveJobs_RootFolderRelocations_RelocationId", add.Name);
        Assert.Equal("MoveJobs", add.Table);
        Assert.Equal("RootFolderRelocations", add.PrincipalTable);
        Assert.Equal("RelocationId", Assert.Single(add.Columns));
        Assert.Equal(ReferentialAction.Restrict, add.OnDelete);
        Assert.Single(upBuilder.Operations);

        var drop = Assert.Single(downBuilder.Operations.OfType<DropForeignKeyOperation>());
        Assert.Equal("FK_MoveJobs_RootFolderRelocations_RelocationId", drop.Name);
        Assert.Equal("MoveJobs", drop.Table);
        Assert.Single(downBuilder.Operations);
    }

    [Fact]
    public void FinalMoveMigration_TargetModelMatchesFinalContracts()
    {
        var model = new AddMoveJobRelocationForeignKey().TargetModel;

        var moveJob = AssertEntity(model, "Listenarr.Domain.Audiobooks.MoveJob");
        Assert.Equal(0, moveJob.FindProperty("ExecutionProtocolVersion")?.GetDefaultValue());
        Assert.Equal("None", moveJob.FindProperty("FailureKind")?.GetDefaultValue());
        Assert.Equal("None", moveJob.FindProperty("Phase")?.GetDefaultValue());

        var rootFolder = AssertEntity(model, "Listenarr.Domain.Audiobooks.RootFolder");
        Assert.Equal("Auto", rootFolder.FindProperty("CaseSensitivityMode")?.GetDefaultValue());
        Assert.Equal("Unknown", rootFolder.FindProperty("ResolvedCaseSensitivity")?.GetDefaultValue());
        Assert.Equal("Unavailable", rootFolder.FindProperty("PathIdentityState")?.GetDefaultValue());

        var audiobookFile = AssertEntity(model, "Listenarr.Domain.Audiobooks.AudiobookFile");
        Assert.Equal("Auto", audiobookFile.FindProperty("PathCaseSensitivityMode")?.GetDefaultValue());
        Assert.Equal("Unknown", audiobookFile.FindProperty("PathCaseSensitivity")?.GetDefaultValue());
        Assert.Equal("Unavailable", audiobookFile.FindProperty("PathIdentityState")?.GetDefaultValue());

        Assert.Null(model.FindEntityType(
            "Listenarr.Domain.Audiobooks.LibraryDirectoryOwnershipRetiredMarker"));
    }

    private static MigrationBuilder BuildOperations(Migration migration, string methodName)
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        migration.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return builder;
    }

    private static IEntityType AssertEntity(IModel model, string name) =>
        Assert.IsAssignableFrom<IEntityType>(model.FindEntityType(name));

    private static void AssertMigrationId<TMigration>(string expected)
        where TMigration : Migration
    {
        var attribute = typeof(TMigration).GetCustomAttribute<MigrationAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(expected, attribute!.Id);
    }
}
