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
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files
{
    public class AudioFileService_UpdateAudiobookFieldsTests : BaseTests
    {
        [Fact]
        public async Task EnsureAudiobookFileAsync_PopulatesAudiobookFilePathAndSize()
        {
            // Minimal metadata service mock so File metadata lookup doesn't throw
            var metadataMock = new Mock<IMetadataService>();
            var metadata = new AudioMetadata
            {
                Title = "Test Book",
                Duration = TimeSpan.FromSeconds(1),
                Format = "m4b",
                BitRate = 64000,
                SampleRate = 44100,
                Channels = 2
            };
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(metadata);
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(
                    It.IsAny<MetadataFileSource>()))
                .ReturnsAsync(metadata);
            _services.AddSingleton(metadataMock.Object);
            Init();

            // Use temp file and establish the authoritative audiobook folder first.
            var tempFile = await FileService.GetTempFileAsync($"afs-test-{Guid.NewGuid()}.m4b");
            var audiobook = new Audiobook
            {
                Title = "Test Book",
                Monitored = true,
                BasePath = Path.GetDirectoryName(tempFile)
            };
            await _audiobookRepository.AddAsync(audiobook);

            var audiobookFileService = _provider.GetRequiredService<IAudiobookFileService>();

            // Act
            var created = await audiobookFileService.EnsureAudiobookFileAsync(audiobook, tempFile, "test");

            // Assert
            Assert.True(created);
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            var file = files.First(f => f.Path == tempFile);
            Assert.NotNull(file);
            Assert.True(files.Sum(f => f.Size) > 0);
        }
    }
}

