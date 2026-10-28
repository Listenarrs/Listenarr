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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    public class AudiobookRepositoryTests
    {
        [Fact]
        public async Task GetAll_IncludesWantedFlag_ForMonitoredWithoutFiles()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            // Monitored without files -> wanted = true
            var wantedBook = new AudiobookBuilder().WithTitle("Wanted Book").WithMonitored().Build();
            db.Audiobooks.Add(wantedBook);

            // Monitored with files -> wanted = false
            var hasFileBook = new AudiobookBuilder().WithTitle("Has File").WithMonitored().Build();
            db.Audiobooks.Add(hasFileBook);
            await db.SaveChangesAsync();

            var file = new AudiobookFileBuilder().WithAudiobook(hasFileBook).WithPath("C:\\temp\\f.m4b").WithSize(1234).Build();
            db.AudiobookFiles.Add(file);
            await db.SaveChangesAsync();

            // Exercise repository directly similar to controller
            var audiobooks = await db.Audiobooks.Include(a => a.Files).ToListAsync();

            var dto = audiobooks.Select(a => new
            {
                id = a.Id,
                wanted = a.Monitored && (a.Files == null || !a.Files.Any())
            }).ToList();

            var wantedDto = dto.First(d => d.id == wantedBook.Id);
            var hasFileDto = dto.First(d => d.id == hasFileBook.Id);

            Assert.True(wantedDto.wanted);
            Assert.False(hasFileDto.wanted);
        }

        [Fact]
        public async Task GetByIdQueries_DoNotTriggerMultipleCollectionIncludeWarning()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Throw(
                    RelationalEventId.MultipleCollectionIncludeWarning))
                .Options;
            await using var db = new ListenArrDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var qualityProfile = new QualityProfile { Name = "Split Query Profile" };
            var audiobook = new Audiobook
            {
                Title = "Split Query Book",
                QualityProfile = qualityProfile,
                Files = [new AudiobookFile { Path = "/library/book.m4b" }],
                ExternalIdentifiers =
                [
                    new AudiobookExternalIdentifier
                    {
                        Type = AudiobookExternalIdentifierType.Asin,
                        ValueRaw = "B000SPLIT1",
                        ValueNormalized = "B000SPLIT1",
                        Source = AudiobookExternalIdentifierSource.Manual
                    }
                ],
                SeriesMemberships =
                [
                    new AudiobookSeriesMembership
                    {
                        SeriesName = "Split Query Series"
                    }
                ]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var repository = new AudiobookRepository(db);

            var tracked = Assert.IsType<Audiobook>(
                await repository.GetByIdAsync(audiobook.Id));
            Assert.NotNull(tracked.QualityProfile);
            Assert.Single(tracked.Files!);
            Assert.Single(tracked.ExternalIdentifiers!);
            Assert.Single(tracked.SeriesMemberships!);

            db.ChangeTracker.Clear();
            var snapshot = Assert.IsType<Audiobook>(
                await repository.GetByIdSnapshotAsync(audiobook.Id));
            Assert.NotNull(snapshot.QualityProfile);
            Assert.Single(snapshot.Files!);
            Assert.Single(snapshot.ExternalIdentifiers!);
            Assert.Single(snapshot.SeriesMemberships!);
            Assert.Equal(EntityState.Detached, db.Entry(snapshot).State);
        }

        [Fact]
        public async Task GetByIdsWithFilesAsync_ReturnsDetachedPreviewSnapshots()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            await using var db = new ListenArrDbContext(options);
            var audiobook = new Audiobook
            {
                Title = "Preview Snapshot",
                Files = [new AudiobookFile { Path = "/library/book.m4b" }]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var repository = new AudiobookRepository(db);

            var snapshot = Assert.Single(await repository.GetByIdsWithFilesAsync([audiobook.Id]));

            Assert.Equal(EntityState.Detached, db.Entry(snapshot).State);
            Assert.All(snapshot.Files!, file => Assert.Equal(EntityState.Detached, db.Entry(file).State));
        }

        [Fact]
        public async Task GetForScanAsync_DoesNotLoadUnneededNavigationGraphs()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            await using var db = new ListenArrDbContext(options);
            var qualityProfile = new QualityProfile { Name = "Scan Profile" };
            db.QualityProfiles.Add(qualityProfile);
            await db.SaveChangesAsync();
            var audiobook = new Audiobook
            {
                Title = "Narrow Scan Read",
                BasePath = "/library/book",
                QualityProfileId = qualityProfile.Id
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.AudiobookFiles.Add(new AudiobookFile
            {
                AudiobookId = audiobook.Id,
                Path = "/library/book/book.m4b"
            });
            db.AudiobookExternalIdentifiers.Add(new AudiobookExternalIdentifier
            {
                AudiobookId = audiobook.Id,
                Type = AudiobookExternalIdentifierType.Asin,
                ValueRaw = "B000SCAN01",
                ValueNormalized = "B000SCAN01",
                Source = AudiobookExternalIdentifierSource.Manual
            });
            db.AudiobookSeriesMemberships.Add(new AudiobookSeriesMembership
            {
                AudiobookId = audiobook.Id,
                SeriesName = "Scan Series"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var repository = new AudiobookRepository(db);

            var tracked = Assert.IsType<Audiobook>(
                await repository.GetForScanAsync(audiobook.Id));

            var entry = db.Entry(tracked);
            Assert.False(entry.Collection(candidate => candidate.Files!).IsLoaded);
            Assert.False(entry.Collection(candidate => candidate.ExternalIdentifiers!).IsLoaded);
            Assert.False(entry.Collection(candidate => candidate.SeriesMemberships!).IsLoaded);
            Assert.False(entry.Reference(candidate => candidate.QualityProfile).IsLoaded);
            Assert.Null(tracked.Files);
            Assert.Null(tracked.ExternalIdentifiers);
            Assert.Null(tracked.SeriesMemberships);
            Assert.Null(tracked.QualityProfile);

            db.ChangeTracker.Clear();
            var snapshot = Assert.IsType<Audiobook>(
                await repository.GetForScanSnapshotAsync(audiobook.Id));
            Assert.Equal(EntityState.Detached, db.Entry(snapshot).State);
            Assert.Null(snapshot.Files);
            Assert.Null(snapshot.ExternalIdentifiers);
            Assert.Null(snapshot.SeriesMemberships);
            Assert.Null(snapshot.QualityProfile);

            var updateSnapshot = Assert.IsType<Audiobook>(
                await repository.GetForUpdateSnapshotAsync(audiobook.Id));
            Assert.Equal(EntityState.Detached, db.Entry(updateSnapshot).State);
            Assert.Null(updateSnapshot.Files);
            Assert.Null(updateSnapshot.ExternalIdentifiers);
            Assert.Null(updateSnapshot.SeriesMemberships);
            Assert.Null(updateSnapshot.QualityProfile);
        }

        [Fact]
        public async Task UpdateAsync_TrackedMetadataChange_DoesNotOverwriteNewerBasePathFromAnotherContext()
        {
            var databaseName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
            int audiobookId;

            await using (var seed = new ListenArrDbContext(options))
            {
                var audiobook = new AudiobookBuilder()
                    .WithTitle("Concurrent Update")
                    .WithBasePath("/library/source")
                    .Build();
                seed.Audiobooks.Add(audiobook);
                await seed.SaveChangesAsync();
                audiobookId = audiobook.Id;
            }

            await using var metadataContext = new ListenArrDbContext(options);
            var metadataRepository = new AudiobookRepository(metadataContext);
            var staleMetadataEntity = await metadataRepository.GetByIdAsync(audiobookId);
            Assert.NotNull(staleMetadataEntity);

            await using (var moveContext = new ListenArrDbContext(options))
            {
                var movedAudiobook = await moveContext.Audiobooks.SingleAsync(candidate => candidate.Id == audiobookId);
                movedAudiobook.BasePath = "/library/target";
                await moveContext.SaveChangesAsync();
            }

            staleMetadataEntity!.Monitored = false;
            await metadataRepository.UpdateAsync(staleMetadataEntity);

            await using var verification = new ListenArrDbContext(options);
            var persisted = await verification.Audiobooks.SingleAsync(candidate => candidate.Id == audiobookId);
            Assert.Equal("/library/target", persisted.BasePath);
            Assert.False(persisted.Monitored);
        }

        [Fact]
        public async Task UpdateAsync_DetachedMetadataChange_DoesNotOverwriteNewerPathReferences()
        {
            var databaseName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
            Audiobook staleMetadataEntity;

            await using (var seed = new ListenArrDbContext(options))
            {
                var audiobook = new AudiobookBuilder()
                    .WithTitle("Detached Concurrent Update")
                    .WithBasePath("/library/source")
                    .Build();
                audiobook.FilePath = "/library/source/book.m4b";
                audiobook.FileSize = 100;
                audiobook.ImageUrl = "/library/source/cover.jpg";
                seed.Audiobooks.Add(audiobook);
                await seed.SaveChangesAsync();
                staleMetadataEntity = await seed.Audiobooks
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == audiobook.Id);
            }

            await using (var moveContext = new ListenArrDbContext(options))
            {
                var movedAudiobook = await moveContext.Audiobooks
                    .SingleAsync(candidate => candidate.Id == staleMetadataEntity.Id);
                movedAudiobook.BasePath = "/library/target";
                movedAudiobook.FilePath = "/library/target/book.m4b";
                movedAudiobook.FileSize = 200;
                movedAudiobook.ImageUrl = "/library/target/cover.jpg";
                await moveContext.SaveChangesAsync();
            }

            staleMetadataEntity.Monitored = false;
            await using (var metadataContext = new ListenArrDbContext(options))
            {
                var metadataRepository = new AudiobookRepository(metadataContext);
                await metadataRepository.UpdateAsync(staleMetadataEntity);
            }

            await using var verification = new ListenArrDbContext(options);
            var persisted = await verification.Audiobooks
                .SingleAsync(candidate => candidate.Id == staleMetadataEntity.Id);
            Assert.Equal("/library/target", persisted.BasePath);
            Assert.Equal("/library/target/book.m4b", persisted.FilePath);
            Assert.Equal(200, persisted.FileSize);
            Assert.Equal("/library/target/cover.jpg", persisted.ImageUrl);
            Assert.False(persisted.Monitored);
        }

        [Fact]
        public async Task UpdateWithIdentifierReplaceAsync_DoesNotOverwriteNewerBasePath()
        {
            var databaseName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
            int audiobookId;

            await using (var seed = new ListenArrDbContext(options))
            {
                var audiobook = new AudiobookBuilder()
                    .WithTitle("Identifier Update")
                    .WithBasePath("/library/source")
                    .Build();
                seed.Audiobooks.Add(audiobook);
                await seed.SaveChangesAsync();
                audiobookId = audiobook.Id;
            }

            await using var identifierContext = new ListenArrDbContext(options);
            var repository = new AudiobookRepository(identifierContext);
            var staleAudiobook = await repository.GetByIdAsync(audiobookId);
            Assert.NotNull(staleAudiobook);

            await using (var moveContext = new ListenArrDbContext(options))
            {
                var movedAudiobook = await moveContext.Audiobooks.SingleAsync(candidate => candidate.Id == audiobookId);
                movedAudiobook.BasePath = "/library/target";
                await moveContext.SaveChangesAsync();
            }

            await repository.UpdateWithIdentifierReplaceAsync(
                staleAudiobook!,
                [new AudiobookExternalIdentifier
                {
                    Type = AudiobookExternalIdentifierType.Asin,
                    ValueRaw = "B012345678",
                    ValueNormalized = "B012345678",
                    IsPrimary = true,
                    Source = AudiobookExternalIdentifierSource.Manual
                }]);

            await using var verification = new ListenArrDbContext(options);
            var persisted = await verification.Audiobooks
                .Include(audiobook => audiobook.ExternalIdentifiers)
                .SingleAsync(candidate => candidate.Id == audiobookId);
            Assert.Equal("/library/target", persisted.BasePath);
            var identifier = Assert.Single(persisted.ExternalIdentifiers);
            Assert.Equal("B012345678", identifier.ValueNormalized);
        }

        [Fact]
        public async Task GetById_IncludesWantedFlag_Correctly()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new AudiobookBuilder().WithTitle("Single Book").WithMonitored().Build();
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Initially should be wanted
            var updated = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var wanted = updated.Monitored && (updated.Files == null || !updated.Files.Any());
            Assert.True(wanted);

            // Add file and re-evaluate
            var file = new AudiobookFileBuilder().WithAudiobook(book).WithPath("C:\\temp\\single.m4b").WithSize(1024).Build();
            db.AudiobookFiles.Add(file);
            await db.SaveChangesAsync();

            var updated2 = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var wanted2 = updated2.Monitored && (updated2.Files == null || !updated2.Files.Any());
            Assert.False(wanted2);
        }
    }
}

