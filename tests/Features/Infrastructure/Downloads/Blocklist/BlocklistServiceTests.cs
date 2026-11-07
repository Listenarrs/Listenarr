/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Downloads.Blocklist;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Blocklist;

/// <summary>
/// BlockAsync reads and then writes, and (AudiobookId, ReleaseIdentifier) is uniquely indexed, so
/// two failures for the same release in one poll cycle can both get past the read.
/// </summary>
[Trait("Name", "BlocklistServiceTests")]
[Trait("Category", "Blocklist")]
public sealed class BlocklistServiceTests : BaseTests
{
    private const string Identifier = "btih:abcdef1234567890abcdef1234567890abcdef12";
    private const string OtherIdentifier = "btih:1111111111111111111111111111111111111111";

    private DbContextOptions<ListenArrDbContext> _options = null!;

    // A file-backed SQLite database rather than the shared context from BaseTests, because the
    // race these tests reproduce needs a second connection writing the rival row while the first
    // context still holds its stale read.
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var databasePath = Path.Join(FileService.GetTempPath(), $"blocklist-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        await using var db = new ListenArrDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task BlockAsync_LosingTheInsertRace_IsTreatedAsAlreadyBlocked()
    {
        // The interleaving the unique index exists to catch, made deterministic: the rival row
        // lands between this call's read and its write. Before the catch, the violation left
        // DownloadMonitorService.OnDownloadFailed halfway through its failure handling.
        await using var context = new RacingDbContext(_options, WriteRivalEntry);
        var service = new BlocklistService(context, NullLogger<BlocklistService>.Instance);

        await service.BlockAsync(7, Identifier, "Mine", 800_000_000, "this call");

        var rows = await ReadAllAsync();
        Assert.Equal("the rival", Assert.Single(rows).Reason);
    }

    [Fact]
    public async Task BlockAsync_AfterLosingTheRace_LeavesTheContextUsable()
    {
        // A caught violation leaves the losing row tracked as Added unless it is detached, and the
        // context is scoped, so the next SaveChangesAsync on it would retry that insert and throw
        // again. This is the assertion that fails if the detach is dropped.
        await using var context = new RacingDbContext(_options, WriteRivalEntry);
        var service = new BlocklistService(context, NullLogger<BlocklistService>.Instance);

        await service.BlockAsync(7, Identifier, "Mine", 800_000_000, "this call");
        await service.BlockAsync(7, OtherIdentifier, "A different release", 1, "a later failure");

        var rows = await ReadAllAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.ReleaseIdentifier == OtherIdentifier);
    }

    [Fact]
    public async Task BlockAsync_WhenTheEntryIsAlreadyThere_WritesNothingFurther()
    {
        await using var context = new ListenArrDbContext(_options);
        var service = new BlocklistService(context, NullLogger<BlocklistService>.Instance);

        await service.BlockAsync(7, Identifier, "Mine", 800_000_000, "first");
        await service.BlockAsync(7, Identifier, "Mine again", 800_000_000, "second");

        Assert.Equal("first", Assert.Single(await ReadAllAsync()).Reason);
    }

    private async Task<List<BlockedRelease>> ReadAllAsync()
    {
        await using var db = new ListenArrDbContext(_options);
        return await db.BlockedReleases.AsNoTracking().ToListAsync();
    }

    private void WriteRivalEntry()
    {
        using var rival = new ListenArrDbContext(_options);
        rival.BlockedReleases.Add(new BlockedRelease
        {
            AudiobookId = 7,
            ReleaseIdentifier = Identifier,
            Title = "Mine",
            Size = 800_000_000,
            Reason = "the rival"
        });
        rival.SaveChanges();
    }

    /// <summary>
    /// Runs a rival insert once, immediately before the first save, so the read this service
    /// already did is stale by the time it writes. A genuine UNIQUE violation from SQLite follows,
    /// which is what the production catch has to handle.
    /// </summary>
    private sealed class RacingDbContext(
        DbContextOptions<ListenArrDbContext> options,
        Action beforeFirstSave) : ListenArrDbContext(options)
    {
        private bool _done;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                beforeFirstSave();
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
