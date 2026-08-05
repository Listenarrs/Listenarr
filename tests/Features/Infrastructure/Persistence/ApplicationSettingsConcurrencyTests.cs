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
using Listenarr.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class ApplicationSettingsConcurrencyTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"settings-concurrency-{Guid.NewGuid():N}.db");
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
    public async Task ConcurrentInitialSave_ReturnsSingletonSettingsForBothCallers()
    {
        await using var db1 = new ListenArrDbContext(_options);
        await using var db2 = new ListenArrDbContext(_options);
        var repository1 = new EfApplicationSettingsRepository(db1);
        var repository2 = new EfApplicationSettingsRepository(db2);

        var results = await Task.WhenAll(
            repository1.InitializeIfMissingAsync(new ApplicationSettings()),
            repository2.InitializeIfMissingAsync(new ApplicationSettings()));

        Assert.All(results, settings => Assert.Equal(1, settings.Id));
        await using var verificationDb = new ListenArrDbContext(_options);
        Assert.Equal(1, await verificationDb.ApplicationSettings.CountAsync());
    }

    [Fact]
    public async Task InitialSave_RacedByExternalInitialization_ThrowsConflictInsteadOfReportingSuccess()
    {
        var interceptor = new InsertCompetingSettingsInterceptor(_options);
        var racingOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new ListenArrDbContext(racingOptions);
        var repository = new EfApplicationSettingsRepository(db);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            repository.SaveAsync(new ApplicationSettings
            {
                OutputPath = "submitted"
            }));

        Assert.Equal("settings_concurrency_conflict", exception.Code);
        await using var verificationDb = new ListenArrDbContext(_options);
        var persisted = await verificationDb.ApplicationSettings
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal("winner", persisted.OutputPath);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task ExistingSettingsUpdate_WithoutVersion_ThrowsStableConflict()
    {
        await using (var seedDb = new ListenArrDbContext(_options))
        {
            await new EfApplicationSettingsRepository(seedDb).SaveAsync(new ApplicationSettings
            {
                OutputPath = "original"
            });
        }

        await using var updateDb = new ListenArrDbContext(_options);
        var repository = new EfApplicationSettingsRepository(updateDb);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            repository.SaveAsync(new ApplicationSettings
            {
                OutputPath = "versionless-overwrite"
            }));

        Assert.Equal("settings_concurrency_conflict", exception.Code);
        await using var verificationDb = new ListenArrDbContext(_options);
        var persisted = await verificationDb.ApplicationSettings
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal("original", persisted.OutputPath);
        Assert.Equal(1, persisted.Version);
    }

    private sealed class InsertCompetingSettingsInterceptor(
        DbContextOptions<ListenArrDbContext> competingOptions) : SaveChangesInterceptor
    {
        private int _invoked;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _invoked, 1) != 0
                || eventData.Context?.ChangeTracker
                    .Entries<ApplicationSettings>()
                    .All(entry => entry.State != EntityState.Added) != false)
            {
                return result;
            }

            await using var competingDb = new ListenArrDbContext(competingOptions);
            competingDb.ApplicationSettings.Add(new ApplicationSettings
            {
                Id = 1,
                Version = 1,
                OutputPath = "winner"
            });
            await competingDb.SaveChangesAsync(cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task StaleSettingsUpdate_ThrowsStableConflict()
    {
        await using (var seedDb = new ListenArrDbContext(_options))
        {
            await new EfApplicationSettingsRepository(seedDb).SaveAsync(new ApplicationSettings());
        }

        await using var db1 = new ListenArrDbContext(_options);
        await using var db2 = new ListenArrDbContext(_options);
        var repository1 = new EfApplicationSettingsRepository(db1);
        var repository2 = new EfApplicationSettingsRepository(db2);
        var first = await repository1.GetAsync();
        var stale = await repository2.GetAsync();
        Assert.NotNull(first);
        Assert.NotNull(stale);

        first!.OutputPath = "first";
        await repository1.SaveAsync(first);
        stale!.OutputPath = "stale";

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(
            () => repository2.SaveAsync(stale));

        Assert.Equal("settings_concurrency_conflict", exception.Code);
    }
}
