/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Builders;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class QualityProfileSeedTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"quality-seed-{Guid.NewGuid():N}.db");
    private DbContextOptions<ListenArrDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        await using var db = new ListenArrDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task SeedDefaultProfile_WhenTableEmpty_CreatesExactlyOneDefaultProfile()
    {
        // Given an empty QualityProfiles table
        await using var db = new ListenArrDbContext(_options);
        var repository = new QualityProfileRepository(db);

        // When seeding
        var seeded = await repository.SeedDefaultProfileIfMissingAsync();

        // Then exactly one default profile exists
        Assert.True(seeded);
        var profiles = await repository.GetAllAsync();
        var profile = Assert.Single(profiles);
        Assert.True(profile.IsDefault);
        Assert.Equal("Any Quality", profile.Name);
        Assert.NotEmpty(profile.Qualities);
        Assert.All(profile.Qualities, quality => Assert.True(quality.Allowed));
    }

    [Fact]
    public async Task SeedDefaultProfile_RunTwice_RemainsExactlyOneProfile()
    {
        // Given a fresh database seeded once
        await using var db = new ListenArrDbContext(_options);
        var repository = new QualityProfileRepository(db);
        Assert.True(await repository.SeedDefaultProfileIfMissingAsync());

        // When seeding again
        var seededSecond = await repository.SeedDefaultProfileIfMissingAsync();

        // Then nothing is added and exactly one profile remains (idempotent)
        Assert.False(seededSecond);
        Assert.Single(await repository.GetAllAsync());
    }

    [Fact]
    public async Task SeedDefaultProfile_WhenProfilesExist_SeedsNothing()
    {
        // Given a table that already has a (non-default) profile
        await using var db = new ListenArrDbContext(_options);
        var repository = new QualityProfileRepository(db);
        await repository.AddAsync(new QualityProfileBuilder()
            .WithName("Custom Only")
            .Build());

        // When seeding
        var seeded = await repository.SeedDefaultProfileIfMissingAsync();

        // Then no seed happens and the original profile is untouched
        Assert.False(seeded);
        var profile = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("Custom Only", profile.Name);
        Assert.False(profile.IsDefault);
    }
}
