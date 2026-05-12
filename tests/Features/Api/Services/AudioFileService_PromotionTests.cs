using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "AudioFileService_PromotionTests")]
    [Trait("Category", "AudioFileService")]
    public class AudioFileService_PromotionTests
    {
        [Fact]
        public void Promote_FillsAllBlankFields_FromMetadata()
        {
            var audiobook = new Audiobook();
            var meta = new AudioMetadata
            {
                Title = "Mistborn",
                Subtitle = "The Final Empire",
                AlbumArtist = "Brandon Sanderson",
                Narrator = "Michael Kramer",
                Series = "Mistborn",
                SeriesPosition = 1m,
                Publisher = "Tor",
                Language = "en",
                Description = "An ash-covered fantasy world.",
                Year = 2006,
                Asin = "B002UZHDC0",
                Isbn = "9780765311788",
            };

            var changed = AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.Equal("Mistborn", audiobook.Title);
            Assert.Equal("The Final Empire", audiobook.Subtitle);
            Assert.NotNull(audiobook.Authors);
            Assert.Equal("Brandon Sanderson", audiobook.Authors![0]);
            Assert.NotNull(audiobook.Narrators);
            Assert.Equal("Michael Kramer", audiobook.Narrators![0]);
            Assert.Equal("Mistborn", audiobook.Series);
            Assert.Equal("1", audiobook.SeriesNumber);
            Assert.Equal("Tor", audiobook.Publisher);
            Assert.Equal("en", audiobook.Language);
            Assert.Equal("An ash-covered fantasy world.", audiobook.Description);
            Assert.Equal("2006", audiobook.PublishYear);
            Assert.Equal("B002UZHDC0", audiobook.Asin);
            Assert.Equal(new List<string> { "9780765311788" }, audiobook.Isbn);
            Assert.True(changed, "Identifier-changed flag should be set when ASIN or ISBN is filled.");
        }

        [Fact]
        public void Promote_NeverOverwritesExistingNonBlankFields()
        {
            var audiobook = new Audiobook
            {
                Title = "Existing Title",
                Subtitle = "Existing Subtitle",
                Authors = new List<string> { "Existing Author" },
                Narrators = new List<string> { "Existing Narrator" },
                Series = "Existing Series",
                SeriesNumber = "5",
                Publisher = "Existing Pub",
                Language = "fr",
                Description = "Existing description",
                PublishYear = "1999",
                Asin = "B0EXISTING",
                Isbn = new List<string> { "1111111111" },
            };
            var meta = new AudioMetadata
            {
                Title = "New Title",
                Subtitle = "New Subtitle",
                AlbumArtist = "New Author",
                Narrator = "New Narrator",
                Series = "New Series",
                SeriesPosition = 99m,
                Publisher = "New Pub",
                Language = "de",
                Description = "New description",
                Year = 2099,
                Asin = "B0NEWWWWWW",
                Isbn = "9999999999999",
            };

            var changed = AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.Equal("Existing Title", audiobook.Title);
            Assert.Equal("Existing Subtitle", audiobook.Subtitle);
            Assert.Equal(new List<string> { "Existing Author" }, audiobook.Authors);
            Assert.Equal(new List<string> { "Existing Narrator" }, audiobook.Narrators);
            Assert.Equal("Existing Series", audiobook.Series);
            Assert.Equal("5", audiobook.SeriesNumber);
            Assert.Equal("Existing Pub", audiobook.Publisher);
            Assert.Equal("fr", audiobook.Language);
            Assert.Equal("Existing description", audiobook.Description);
            Assert.Equal("1999", audiobook.PublishYear);
            Assert.Equal("B0EXISTING", audiobook.Asin);
            Assert.Equal(new List<string> { "1111111111" }, audiobook.Isbn);
            Assert.False(changed);
        }

        [Fact]
        public void Promote_DoesNotInferAuthor_WhenArtistMatchesNarrator()
        {
            var audiobook = new Audiobook();
            var meta = new AudioMetadata
            {
                Artist = "Will Wheaton",
                AlbumArtist = "Will Wheaton",
                Narrator = "Will Wheaton",
            };

            AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.True(audiobook.Authors == null || audiobook.Authors.Count == 0);
            Assert.NotNull(audiobook.Narrators);
        }

        [Fact]
        public void Promote_PrefersAlbumArtist_OverArtist()
        {
            var audiobook = new Audiobook();
            var meta = new AudioMetadata
            {
                Artist = "Some Reader",
                AlbumArtist = "Real Author",
            };

            AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.NotNull(audiobook.Authors);
            Assert.Equal("Real Author", audiobook.Authors![0]);
        }

        [Fact]
        public void Promote_SplitsMultiAuthorTag_OnSemicolonAndSlashOnly()
        {
            var audiobook = new Audiobook();
            var meta = new AudioMetadata { AlbumArtist = "Author One; Author Two / Author Three" };

            AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.Equal(new List<string> { "Author One", "Author Two", "Author Three" }, audiobook.Authors);
        }

        [Fact]
        public void Promote_KeepsLastFirstNameTagAsSingleAuthor()
        {
            var audiobook = new Audiobook();
            var meta = new AudioMetadata { AlbumArtist = "Sanderson, Brandon" };

            AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.NotNull(audiobook.Authors);
            Assert.Equal(new List<string> { "Sanderson, Brandon" }, audiobook.Authors);
        }

        [Fact]
        public void Promote_RejectsInvalidAsinAndIsbn()
        {
            var audiobook = new Audiobook();
            var meta = new AudioMetadata
            {
                Asin = "NOTANASIN",
                Isbn = "12345",
            };

            var changed = AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.Null(audiobook.Asin);
            Assert.True(audiobook.Isbn == null || audiobook.Isbn.Count == 0);
            Assert.False(changed);
        }

        [Fact]
        public void Promote_FillsSeriesNumber_FromDecimalPosition()
        {
            var audiobook = new Audiobook();
            var meta = new AudioMetadata { SeriesPosition = 2.5m };

            AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.Equal("2.5", audiobook.SeriesNumber);
        }

        [Fact]
        public void Promote_ReturnsTrue_WhenOnlyIdentifierChanges()
        {
            var audiobook = new Audiobook { Title = "Already set" };
            var meta = new AudioMetadata { Asin = "B0ABCDEFGH" };

            var changed = AudioFileService.PromoteBlankFieldsFromMetadata(audiobook, meta);

            Assert.True(changed);
            Assert.Equal("B0ABCDEFGH", audiobook.Asin);
        }
    }
}
