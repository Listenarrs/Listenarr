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
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Api.Services.Metadata;
using Listenarr.Infrastructure.Models;
using Listenarr.Application.Repositories;
using Listenarr.Infrastructure.Repositories;

namespace Listenarr.Tests.Features.Api.Services
{
    public class AudioFileService_UpdateAudiobookFieldsTests
    {
        [Fact]
        public async Task EnsureAudiobookFileAsync_PopulatesAudiobookFilePathAndSize()
        {
            // Arrange - create in-memory db
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var audiobook = new Audiobook { Title = "Test Book", Monitored = true };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            // Build service provider with required services (including a mock metadata service)
            var services = new ServiceCollection();
            services.AddSingleton<ListenArrDbContext>(db);
            services.AddSingleton<IAudiobookFileRepository>(_ => new EfAudiobookFileRepository(db));
            services.AddSingleton<IAudiobookRepository>(_ => new AudiobookRepository(db));
            services.AddSingleton<IHistoryRepository>(_ => new EfHistoryRepository(db));
            services.AddSingleton<MetadataExtractionLimiter>();
            services.AddMemoryCache();
            // Minimal metadata service mock so File metadata lookup doesn't throw
            var metadataMock = new Moq.Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Test Book", Duration = TimeSpan.FromSeconds(1), Format = "m4b", BitRate = 64000, SampleRate = 44100, Channels = 2 });
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var logger = new NullLogger<AudioFileService>();
            var memoryCache = provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var limiter = provider.GetRequiredService<MetadataExtractionLimiter>();

            var svc = new AudioFileService(scopeFactory, logger, memoryCache, limiter);

            // Use temp file
            var tempFile = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"afs-test-{Guid.NewGuid()}.m4b");
            System.IO.File.WriteAllText(tempFile, "dummy");

            // Act
            var created = await svc.EnsureAudiobookFileAsync(audiobook.Id, tempFile, "test");

            // Assert
            Assert.True(created);
            var updated = await db.Audiobooks.FindAsync(audiobook.Id);
            Assert.NotNull(updated.FilePath);
            Assert.True(updated.FilePath.Contains(System.IO.Path.GetFileName(tempFile)) || updated.FilePath == tempFile);
            Assert.True(updated.FileSize > 0);

            // Cleanup
            System.IO.File.Delete(tempFile);
        }
    }
}

