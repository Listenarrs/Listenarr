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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Moq;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Metadata;
using Listenarr.Infrastructure.Models;
using Listenarr.Application.Repositories;
using Listenarr.Infrastructure.Repositories;

namespace Listenarr.Tests.Features.Api.Services
{
    public class AudioFileServiceTests
    {
        [Fact]
        public async Task EnsureAudiobookFileAsync_CreatesFileRecord_HappyPath()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var book = new Audiobook { Title = "Test" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var testFile = Path.Join(Path.GetTempPath(), $"afs-test-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(testFile, "dummy");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Duration = TimeSpan.FromSeconds(1234), Format = "m4b", BitRate = 64000 });

            // Build service provider with required services
            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton(db);
            services.AddSingleton<IAudiobookFileRepository>(_ => new EfAudiobookFileRepository(db));
            services.AddSingleton<IAudiobookRepository>(_ => new AudiobookRepository(db));
            services.AddSingleton<IHistoryRepository>(_ => new EfHistoryRepository(db));
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddMemoryCache();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            var svc = new AudioFileService(scopeFactory, loggerMock.Object, provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), provider.GetRequiredService<MetadataExtractionLimiter>());

            var created = await svc.EnsureAudiobookFileAsync(book.Id, testFile, "test");
            Assert.True(created);

            var file = await db.AudiobookFiles.FirstOrDefaultAsync(f => f.AudiobookId == book.Id && f.Path == testFile);
            Assert.NotNull(file);
            Assert.Equal("m4b", file.Format);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_HandlesUniqueConstraintViolation_ReturnsFalse()
        {
            // Create a fake DbContext that throws DbUpdateException on SaveChangesAsync
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            // subclass ListenArrDbContext to override SaveChangesAsync
            var db = new ThrowingSaveChangesDbContext(options);

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>())).ReturnsAsync(new AudioMetadata());

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton<ListenArrDbContext>(db);
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddMemoryCache();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            var svc = new AudioFileService(scopeFactory, loggerMock.Object, provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), provider.GetRequiredService<MetadataExtractionLimiter>());

            var result = await svc.EnsureAudiobookFileAsync(1, "C:\\fake\\path.m4b", "test");
            Assert.False(result);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RefusesFileOutsideAudiobookFolder_AndCreatesHistory()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);

            // Create audiobook with a legacy FilePath in Author/BookA folder
            var bookA = new Audiobook { Title = "Book A", Authors = new System.Collections.Generic.List<string> { "Author" }, FilePath = Path.Join(Path.GetTempPath(), "Author", "BookA", "track1.m4b") };
            db.Audiobooks.Add(bookA);
            await db.SaveChangesAsync();

            // Ensure the audiobook directory exists on disk for the containment check
            var bookADir = Path.GetDirectoryName(bookA.FilePath);
            if (!Directory.Exists(bookADir)) Directory.CreateDirectory(bookADir!);

            // Create a file in a sibling folder Author/BookB which should be refused
            var rejectedDir = Path.Join(Path.GetTempPath(), "Author", "BookB");
            if (!Directory.Exists(rejectedDir)) Directory.CreateDirectory(rejectedDir);
            var rejectedFile = Path.Join(rejectedDir, $"rejected-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(rejectedFile, "dummy");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>())).ReturnsAsync(new AudioMetadata());

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton<ListenArrDbContext>(db);
            services.AddSingleton<IAudiobookFileRepository>(_ => new EfAudiobookFileRepository(db));
            services.AddSingleton<IAudiobookRepository>(_ => new AudiobookRepository(db));
            services.AddSingleton<IHistoryRepository>(_ => new EfHistoryRepository(db));
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddMemoryCache();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            var svc = new AudioFileService(scopeFactory, loggerMock.Object, provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), provider.GetRequiredService<MetadataExtractionLimiter>());

            var result = await svc.EnsureAudiobookFileAsync(bookA.Id, rejectedFile, "test-scan");
            Assert.False(result);

            // History entry should be created
            var history = await db.History.FirstOrDefaultAsync(h => h.AudiobookId == bookA.Id && h.EventType == "File Association Refused");
            Assert.NotNull(history);
            Assert.Contains("Refused to associate file", history.Message);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_AllowsFileWithinBasePath_WhenBasePathHasTrailingSeparator()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);

            var oldDir = Path.Join(Path.GetTempPath(), "listenarr-audiofile-old", Guid.NewGuid().ToString(), "Old Folder");
            Directory.CreateDirectory(oldDir);
            var oldFile = Path.Join(oldDir, "track1.m4b");
            await File.WriteAllTextAsync(oldFile, "old");

            var importDir = Path.Join(Path.GetTempPath(), "listenarr-audiofile-new", Guid.NewGuid().ToString(), "Jack of Shadows");
            Directory.CreateDirectory(importDir);
            var candidateFile = Path.Join(importDir, "Jack of Shadows_ Rediscovered Classics, Book 23-14.mp3");
            await File.WriteAllTextAsync(candidateFile, "new");

            var book = new Audiobook
            {
                Title = "Jack of Shadows",
                FilePath = oldFile,
                BasePath = importDir + Path.DirectorySeparatorChar
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Format = "mp3", BitRate = 128000 });

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton(db);
            services.AddSingleton<IAudiobookFileRepository>(_ => new EfAudiobookFileRepository(db));
            services.AddSingleton<IAudiobookRepository>(_ => new AudiobookRepository(db));
            services.AddSingleton<IHistoryRepository>(_ => new EfHistoryRepository(db));
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddMemoryCache();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            var svc = new AudioFileService(
                scopeFactory,
                loggerMock.Object,
                provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                provider.GetRequiredService<MetadataExtractionLimiter>());

            var created = await svc.EnsureAudiobookFileAsync(book.Id, candidateFile, "test-scan");
            Assert.True(created);

            var file = await db.AudiobookFiles.FirstOrDefaultAsync(f => f.AudiobookId == book.Id && f.Path == candidateFile);
            Assert.NotNull(file);
            Assert.DoesNotContain(db.History, h => h.AudiobookId == book.Id && h.EventType == "File Association Refused");
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RejectsNonAudioFile()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var book = new Audiobook { Title = "Test" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var testFile = Path.Join(Path.GetTempPath(), $"afs-test-{Guid.NewGuid()}.jpg");
            await File.WriteAllTextAsync(testFile, "dummy");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Format = "jpg" });

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton(db);
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddMemoryCache();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            var svc = new AudioFileService(
                scopeFactory,
                loggerMock.Object,
                provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                provider.GetRequiredService<MetadataExtractionLimiter>());

            var created = await svc.EnsureAudiobookFileAsync(book.Id, testFile, "test");

            Assert.False(created);
            Assert.Null(await db.AudiobookFiles.FirstOrDefaultAsync(f => f.AudiobookId == book.Id && f.Path == testFile));

            File.Delete(testFile);
        }
        [Fact]
        public async Task EnsureAudiobookFileAsync_PersistsMetadataFromMetadataService()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var book = new Audiobook { Title = "IntegrationTest" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var testFile = Path.Join(Path.GetTempPath(), $"meta-int-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(testFile, "dummy");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Duration = TimeSpan.FromSeconds(3210), Format = "m4b", BitRate = 64000, SampleRate = 32000, Channels = 1 });

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton(db);
            services.AddSingleton<IAudiobookFileRepository>(_ => new EfAudiobookFileRepository(db));
            services.AddSingleton<IAudiobookRepository>(_ => new AudiobookRepository(db));
            services.AddSingleton<IHistoryRepository>(_ => new EfHistoryRepository(db));
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddMemoryCache();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            var svc = new AudioFileService(scopeFactory, loggerMock.Object, provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), provider.GetRequiredService<MetadataExtractionLimiter>());

            var created = await svc.EnsureAudiobookFileAsync(book.Id, testFile, "test");
            Assert.True(created);

            var file = await db.AudiobookFiles.FirstOrDefaultAsync(f => f.AudiobookId == book.Id && f.Path == testFile);
            Assert.NotNull(file);
            Assert.Equal(3210, (int)file.DurationSeconds!.Value);
            Assert.Equal("m4b", file.Format);
            Assert.Equal(64000, file.Bitrate);
            Assert.Equal(32000, file.SampleRate);
            Assert.Equal(1, file.Channels);
        }

        // Test helper DbContext that throws on SaveChangesAsync
        private class ThrowingSaveChangesDbContext : ListenArrDbContext
        {
            public ThrowingSaveChangesDbContext(DbContextOptions<ListenArrDbContext> options) : base(options) { }

            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            {
                throw new DbUpdateException("Constraint failed", new Exception("UNIQUE constraint failed: AudiobookFiles.AudiobookId, Path"));
            }
        }
    }
}

