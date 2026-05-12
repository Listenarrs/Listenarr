using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Domain.Models
{
    [Trait("Name", "AudiobookIdentifierSyncTests")]
    [Trait("Category", "Domain")]
    public class AudiobookIdentifierSyncTests
    {
        [Fact]
        public void Sync_AddsAsinFromLegacyField_WhenIdentifiersListEmpty()
        {
            var audiobook = new Audiobook { Asin = "B0ABCDEFGH" };

            AudiobookIdentifierSync.Sync(audiobook);

            Assert.NotNull(audiobook.ExternalIdentifiers);
            var asin = Assert.Single(audiobook.ExternalIdentifiers!, i => i.Type == AudiobookExternalIdentifierType.Asin);
            Assert.Equal("B0ABCDEFGH", asin.ValueNormalized);
            Assert.Equal(AudiobookExternalIdentifierSource.Imported, asin.Source);
            Assert.True(asin.IsPrimary);
        }

        [Fact]
        public void Sync_AddsIsbnFromLegacyList_NormalizingFormat()
        {
            var audiobook = new Audiobook { Isbn = new List<string> { "978-3-16-148410-0" } };

            AudiobookIdentifierSync.Sync(audiobook);

            var isbn = Assert.Single(audiobook.ExternalIdentifiers!, i => i.Type == AudiobookExternalIdentifierType.Isbn);
            Assert.Equal("9783161484100", isbn.ValueNormalized);
        }

        [Fact]
        public void Sync_PreservesProviderIdentifiers_OnlyReplacesGivenSource()
        {
            var audiobook = new Audiobook
            {
                Asin = "B0NEWASIN1",
                ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                {
                    new()
                    {
                        Type = AudiobookExternalIdentifierType.Asin,
                        ValueNormalized = "B0PROVIDR1",
                        Source = AudiobookExternalIdentifierSource.Provider,
                        IsPrimary = true,
                    }
                }
            };

            AudiobookIdentifierSync.Sync(audiobook, AudiobookExternalIdentifierSource.Imported);

            Assert.Equal(2, audiobook.ExternalIdentifiers!.Count);
            Assert.Contains(audiobook.ExternalIdentifiers, i => i.Source == AudiobookExternalIdentifierSource.Provider && i.ValueNormalized == "B0PROVIDR1");
            Assert.Contains(audiobook.ExternalIdentifiers, i => i.Source == AudiobookExternalIdentifierSource.Imported && i.ValueNormalized == "B0NEWASIN1");
        }

        [Fact]
        public void Sync_SkipsLegacyAsin_WhenExistingProviderIdentifierHasSameValue()
        {
            var audiobook = new Audiobook
            {
                Asin = "B0SHAREDXX",
                ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                {
                    new()
                    {
                        Type = AudiobookExternalIdentifierType.Asin,
                        ValueNormalized = "B0SHAREDXX",
                        Source = AudiobookExternalIdentifierSource.Provider,
                    }
                }
            };

            AudiobookIdentifierSync.Sync(audiobook);

            Assert.Single(audiobook.ExternalIdentifiers!);
            Assert.Equal(AudiobookExternalIdentifierSource.Provider, audiobook.ExternalIdentifiers![0].Source);
        }

        [Fact]
        public void Sync_RemovesStaleImportedIdentifiers_WhenLegacyFieldsEmpty()
        {
            var audiobook = new Audiobook
            {
                ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                {
                    new()
                    {
                        Type = AudiobookExternalIdentifierType.Asin,
                        ValueNormalized = "B0STALEXXX",
                        Source = AudiobookExternalIdentifierSource.Imported,
                    }
                }
            };

            AudiobookIdentifierSync.Sync(audiobook);

            Assert.Empty(audiobook.ExternalIdentifiers!);
        }
    }
}
