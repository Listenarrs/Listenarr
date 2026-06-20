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
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files
{
    public class AudioFileServiceTests : BaseTests
    {
        private Audiobook _audiobook = new AudiobookBuilder()
            .WithTitle("Generic book")
            .WithAuthor("Random guy")
            .Build();

        public override async Task InitializeAsync()
        {
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Duration = TimeSpan.FromSeconds(1234), Format = "m4b", BitRate = 64000, SampleRate = 32000, Channels = 1 });

            _services.AddSingleton(metadataMock.Object);
            Init();
            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            await _audiobookRepository.AddAsync(_audiobook);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_CreatesFileRecord_HappyPath()
        {
            var testFile = Path.Join(Path.GetTempPath(), $"afs-test-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(testFile, "dummy");

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var created = await svc.EnsureAudiobookFileAsync(_audiobook, testFile, "test");
            Assert.True(created);

            var file = (await _audiobookFileRepository.GetByAudiobookIdAsync(_audiobook.Id)).First(f => f.Path == testFile);
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

            var svc = _provider.GetRequiredService<IAudiobookFileService>();

            var result = await svc.EnsureAudiobookFileAsync(_audiobook, "C:\\fake\\path.m4b", "test");
            Assert.False(result);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RefusesFileOutsideAudiobookFolder_AndCreatesHistory()
        {
            // Create audiobook with a legacy FilePath in Author/BookA folder
            var bookA = new Audiobook { Title = "Book A", Authors = new System.Collections.Generic.List<string> { "Author" }, FilePath = Path.Join(Path.GetTempPath(), "Author", "BookA", "track1.m4b") };
            await _audiobookRepository.AddAsync(bookA);

            // Ensure the audiobook directory exists on disk for the containment check
            var bookADir = Path.GetDirectoryName(bookA.FilePath);
            if (!Directory.Exists(bookADir)) Directory.CreateDirectory(bookADir!);

            // Create a file in a sibling folder Author/BookB which should be refused
            var rejectedDir = Path.Join(Path.GetTempPath(), "Author", "BookB");
            if (!Directory.Exists(rejectedDir)) Directory.CreateDirectory(rejectedDir);
            var rejectedFile = Path.Join(rejectedDir, $"rejected-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(rejectedFile, "dummy");

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var result = await svc.EnsureAudiobookFileAsync(bookA, rejectedFile, "test-scan");
            Assert.False(result);

            // History entry should be created
            var histories = await _historyRepository.GetByAudiobookIdAsync(bookA.Id);
            Assert.NotNull(histories);
            var history = histories.First(h => h.EventType == "File Association Refused");
            Assert.NotNull(history);
            Assert.Contains("Refused to associate file", history.Message);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_AllowsFileWithinBasePath_WhenBasePathHasTrailingSeparator()
        {
            var oldDir = Path.Join(Path.GetTempPath(), "listenarr-audiofile-old", Guid.NewGuid().ToString(), "Old Folder");
            Directory.CreateDirectory(oldDir);
            var oldFile = Path.Join(oldDir, "track1.m4b");
            await File.WriteAllTextAsync(oldFile, "old");

            var importDir = Path.Join(Path.GetTempPath(), "listenarr-audiofile-new", Guid.NewGuid().ToString(), "Jack of Shadows");
            Directory.CreateDirectory(importDir);
            var candidateFile = Path.Join(importDir, "Jack of Shadows_ Rediscovered Classics, Book 23-14.mp3");
            await File.WriteAllTextAsync(candidateFile, "new");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Format = "mp3", BitRate = 128000 });
            _services.AddSingleton(metadataMock.Object);
            Init();
            await InitDataAsync();

            var book = new Audiobook
            {
                Title = "Jack of Shadows",
                FilePath = oldFile,
                BasePath = importDir + Path.DirectorySeparatorChar
            };
            await _audiobookRepository.AddAsync(book);

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var created = await svc.EnsureAudiobookFileAsync(book, candidateFile, "test-scan");
            Assert.True(created);

            var file = (await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id)).First(f => f.Path == candidateFile);
            Assert.NotNull(file);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                (await _historyRepository.GetByAudiobookIdAsync(book.Id))
                .First(h => h.EventType == "File Association Refused"));
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_RejectsNonAudioFile()
        {
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Format = "jpg" });
            _services.AddSingleton(metadataMock.Object);
            Init();
            await InitDataAsync();

            var testFile = await FileService.GetTempFileAsync($"afs-test-{Guid.NewGuid()}.jpg");

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var created = await svc.EnsureAudiobookFileAsync(_audiobook, testFile, "test");

            Assert.False(created);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => (await _audiobookFileRepository.GetByAudiobookIdAsync(_audiobook.Id)).First(f => f.Path == testFile));
        }
        [Fact]
        public async Task EnsureAudiobookFileAsync_PersistsMetadataFromMetadataService()
        {
            var testFile = await FileService.GetTempFileAsync($"meta-int-{Guid.NewGuid()}.m4b");

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var created = await svc.EnsureAudiobookFileAsync(_audiobook, testFile, "test");
            Assert.True(created);

            var file = (await _audiobookFileRepository.GetByAudiobookIdAsync(_audiobook.Id)).First(f => f.Path == testFile);
            Assert.NotNull(file);
            Assert.Equal(1234, (int)file.DurationSeconds!.Value);
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

