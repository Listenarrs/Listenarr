using Listenarr.Api.Services.Search.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Api.Tests.Services.Search.Providers
{
    [Trait("Category", "SearchProvider")]
    [Trait("Third-Party", "Slskd")]
    public class SlskdSearchProviderTests : BaseTests
    {
        private readonly int INDEXER_ID = 1;
        private readonly SlskdApiMock mock = new();

        public IIndexerSearchProvider CreateSearchProvider()
        {
            return new SlskdSearchProvider(
                new HttpClient(mock),
                NullLogger<SlskdSearchProvider>.Instance);
        }

        private void InitDB(ListenArrDbContext context)
        {
            context.Indexers.Add(new Indexer
            {
                Id = INDEXER_ID,
                Name = "TEST SLSKD",
                Protocol = DownloadProtocol.Soulseek,
                Implementation = Implementation.Slskd,
                Url = "http://mock.slskd.tld:5030",
                ApiKey = "THE_MOST_VERY_VERY_SECURE_KEY"
            });
        }

        [Fact]
        [Trait("Method", "SearchAsync")]
        public async Task SearchAsync()
        {
            var db = CreateDB(InitDB);
            var context = db.CreateDbContext();
            var indexer = context.Indexers.First(i => i.Id == INDEXER_ID);

            mock.ResetCallCount();
            Assert.Equal(0, mock.GetCallCount());
            
            var results = await CreateSearchProvider().SearchAsync(indexer, "Jack Vance");
            
            var indexerExist = context.Indexers.Any(i => i.Id == INDEXER_ID);

            Assert.Equal(3, mock.GetCallCount());
            Assert.True(indexerExist);
            Assert.Equal(5, results.Count);
            Assert.Equal(2, results[0].FileCount);
            Assert.Equal(2, results[0].Files.Count());
            Assert.Equal(14784590, results[0].Size);
            Assert.Equal(6340608, results[0].Files[0].Size);
            Assert.Equal(2, results[1].FileCount);
            Assert.Equal(1, results[2].FileCount);
            Assert.Equal("Fear Factory\\1999 - Fear Factory - Messiah [Russia]\\01. Crash Test.flac", results[2].Files[0].Filename);
            Assert.Equal(2, results[3].FileCount);
            Assert.Equal(1, results[4].FileCount);
        }
    }
}
