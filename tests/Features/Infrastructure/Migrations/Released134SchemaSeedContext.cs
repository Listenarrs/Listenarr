/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

/// <summary>
/// Creates the context the released-upgrade tests use to seed rows through the current entity
/// classes into a database migrated only to the released 1.3.4 schema
/// (<c>20260825021432_AddWeakStorageVerifiedCleanup</c>). Entity properties added after that
/// release have no column yet, so EF would fail the INSERT; they are ignored here instead. When a
/// migration adds a column to an entity these tests seed (Audiobooks, AudiobookFiles, RootFolders,
/// LibraryDirectoryOwnerships, MoveJobs, RootFolderRelocations), ignore its property below.
/// </summary>
internal static class Released134SchemaSeedContext
{
    public static ListenArrDbContext Create(DbContextOptions<ListenArrDbContext> options) =>
        new(new DbContextOptionsBuilder<ListenArrDbContext>(options)
            .ReplaceService<IModelCustomizer, Released134ModelCustomizer>()
            // The trimmed model intentionally differs from the migration snapshot.
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    private sealed class Released134ModelCustomizer(ModelCustomizerDependencies dependencies)
        : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);

            // 20261019085836_AddBookLevelNarratorAndAudiobookshelfSettings
            modelBuilder.Entity<Audiobook>().Ignore(audiobook => audiobook.RequiredNarrator);
        }
    }
}
