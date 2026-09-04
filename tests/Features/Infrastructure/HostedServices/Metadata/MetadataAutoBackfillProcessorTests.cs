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
using Listenarr.Infrastructure.HostedServices.Metadata;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.HostedServices.Metadata
{
    [Trait("Name", "MetadataAutoBackfillProcessorTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class MetadataAutoBackfillProcessorTests : BaseTests
    {
        private const string Asin = "B00B5HZGUG";

        private static AudiobookMetadataEnvelope ProviderEnvelope()
        {
            var response = new AudibleBookResponseBuilder()
                .WithAsin(Asin)
                .WithTitle("The Martian (Provider Title)")
                .WithRegion("us")
                .WithAuthor("Andy Weir")
                .WithNarrator("R. C. Bray")
                .WithLengthMinutes(653)
                .WithReleaseDate("2013-03-22")
                .Build();
            response.Description = "Six days ago, astronaut Mark Watney became one of the first people to walk on Mars.";
            response.Publisher = "Podium Audio";
            response.Language = "english";
            response.ImageUrl = "https://m.media-amazon.com/images/I/martian.jpg";
            return new AudiobookMetadataEnvelope(response, "Audible", "https://www.audible.com/pd/" + Asin);
        }

        private async Task EnableBackfillAsync(bool enabled = true)
        {
            var settings = new ApplicationSettingsBuilder().Build();
            settings.MetadataAutoBackfillEnabled = enabled;
            await _applicationSettingsRepository.SaveAsync(settings);
        }

        private async Task<Audiobook> SeedBookWithFileAsync(string tempDirName, Action<Audiobook>? configure = null)
        {
            var audioPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory(tempDirName),
                "book.m4b",
                "audio");
            var audiobook = new AudiobookBuilder()
                .WithTitle("The Martian")
                .WithAuthor("Andy Weir")
                .WithBasePath(Path.GetDirectoryName(audioPath)!)
                .Build();
            audiobook.Asin = Asin;
            configure?.Invoke(audiobook);
            audiobook = await _audiobookRepository.AddAsync(audiobook);
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            return audiobook;
        }

        private MetadataAutoBackfillProcessor CreateProcessor() => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
            _provider.GetRequiredService<IMoveQueueService>(),
            NullLogger<MetadataAutoBackfillProcessor>.Instance);

        private async Task<Audiobook> ReloadAsync(int id)
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            return await db.Audiobooks.AsNoTracking().SingleAsync(a => a.Id == id);
        }

        [Fact]
        public async Task RunCycleAsync_FillsBlankFieldsAndCover_LeavesPopulatedFieldsAlone()
        {
            var metadataService = new Mock<IAudiobookMetadataService>(MockBehavior.Strict);
            metadataService
                .Setup(s => s.GetMetadataAsync(Asin, "us", true))
                .ReturnsAsync(ProviderEnvelope());
            var imageCache = new Mock<IImageCacheService>();
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync(Asin, "https://m.media-amazon.com/images/I/martian.jpg"))
                .ReturnsAsync("images/library/" + Asin + ".jpg");
            Init(builder => builder
                .WithSingleton(metadataService.Object)
                .WithSingleton(imageCache.Object));
            await EnableBackfillAsync();
            var audiobook = await SeedBookWithFileAsync("metadata-backfill-fill");

            await CreateProcessor().RunCycleAsync(CancellationToken.None);

            var persisted = await ReloadAsync(audiobook.Id);
            Assert.Equal("The Martian", persisted.Title);
            Assert.Equal(new[] { "Andy Weir" }, persisted.Authors);
            Assert.StartsWith("Six days ago", persisted.Description);
            Assert.Equal("Podium Audio", persisted.Publisher);
            Assert.Equal("english", persisted.Language);
            Assert.Equal("2013", persisted.PublishYear);
            Assert.Equal("2013-03-22", persisted.PublishedDate);
            Assert.Equal(653, persisted.Runtime);
            Assert.Equal(new[] { "R. C. Bray" }, persisted.Narrators);
            Assert.Equal("/images/library/" + Asin + ".jpg", persisted.ImageUrl);
            Assert.NotNull(persisted.MetadataBackfillAttemptedAt);
            metadataService.VerifyAll();
        }

        [Fact]
        public async Task RunCycleAsync_SettingDisabled_NeverLooksUp()
        {
            var metadataService = new Mock<IAudiobookMetadataService>(MockBehavior.Strict);
            Init(builder => builder.WithSingleton(metadataService.Object));
            await EnableBackfillAsync(enabled: false);
            var audiobook = await SeedBookWithFileAsync("metadata-backfill-disabled");

            await CreateProcessor().RunCycleAsync(CancellationToken.None);

            var persisted = await ReloadAsync(audiobook.Id);
            Assert.Null(persisted.Description);
            Assert.Null(persisted.MetadataBackfillAttemptedAt);
            metadataService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RunCycleAsync_BookWithoutFiles_IsNotACandidate()
        {
            var metadataService = new Mock<IAudiobookMetadataService>(MockBehavior.Strict);
            Init(builder => builder.WithSingleton(metadataService.Object));
            await EnableBackfillAsync();
            var wishlist = new AudiobookBuilder().WithTitle("Wanted Only").Build();
            wishlist.Asin = Asin;
            wishlist = await _audiobookRepository.AddAsync(wishlist);

            await CreateProcessor().RunCycleAsync(CancellationToken.None);

            var persisted = await ReloadAsync(wishlist.Id);
            Assert.Null(persisted.Description);
            Assert.Null(persisted.MetadataBackfillAttemptedAt);
            metadataService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RunCycleAsync_CompleteBook_IsNotACandidate()
        {
            var metadataService = new Mock<IAudiobookMetadataService>(MockBehavior.Strict);
            Init(builder => builder.WithSingleton(metadataService.Object));
            await EnableBackfillAsync();
            await SeedBookWithFileAsync("metadata-backfill-complete", book =>
            {
                book.Description = "Already described.";
                book.ImageUrl = "/images/library/existing.jpg";
                book.Publisher = "Existing Publisher";
                book.Language = "english";
                book.PublishedDate = "2013-03-22";
            });

            await CreateProcessor().RunCycleAsync(CancellationToken.None);

            metadataService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RunCycleAsync_NoProviderMetadata_MarksAttemptedAndRetriesLater()
        {
            var metadataService = new Mock<IAudiobookMetadataService>(MockBehavior.Strict);
            metadataService
                .Setup(s => s.GetMetadataAsync(Asin, "us", true))
                .ReturnsAsync((AudiobookMetadataEnvelope?)null);
            var asinLookup = new Mock<IAsinLookupService>(MockBehavior.Strict);
            Init(builder => builder
                .WithSingleton(metadataService.Object)
                .WithSingleton(asinLookup.Object));
            await EnableBackfillAsync();
            var audiobook = await SeedBookWithFileAsync("metadata-backfill-miss");

            var processor = CreateProcessor();
            await processor.RunCycleAsync(CancellationToken.None);

            var persisted = await ReloadAsync(audiobook.Id);
            Assert.Null(persisted.Description);
            Assert.NotNull(persisted.MetadataBackfillAttemptedAt);
            metadataService.Verify(s => s.GetMetadataAsync(Asin, "us", true), Times.Once);

            // A second cycle inside the retry window must not hit the provider again.
            await processor.RunCycleAsync(CancellationToken.None);
            metadataService.Verify(s => s.GetMetadataAsync(Asin, "us", true), Times.Once);
            asinLookup.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RunCycleAsync_UnresolvedMove_SkipsWithoutMarkingAttempted()
        {
            var metadataService = new Mock<IAudiobookMetadataService>(MockBehavior.Strict);
            Init(builder => builder.WithSingleton(metadataService.Object));
            await EnableBackfillAsync();
            var audiobook = await SeedBookWithFileAsync("metadata-backfill-move");
            await MoveJobTestFactory.SeedUnresolvedExecutionAsync(
                _provider,
                audiobook.Id,
                audiobook.BasePath!,
                Path.Join(FileService.GetTempPath(), $"metadata-backfill-move-target-{Guid.NewGuid():N}"));

            await CreateProcessor().RunCycleAsync(CancellationToken.None);

            var persisted = await ReloadAsync(audiobook.Id);
            Assert.Null(persisted.Description);
            Assert.Null(persisted.MetadataBackfillAttemptedAt);
            metadataService.VerifyNoOtherCalls();
        }
    }
}
