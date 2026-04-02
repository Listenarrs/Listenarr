using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests.Services.Adapters
{
    [Trait("Category", "DownloadClientAdapter")]
    [Trait("Third-Party", "Slskd")]
    public class SlskdAdapterTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "slskd-1";
        private readonly string DOWNLOAD_REMOVE_ID = "dl-remove-1";
        private readonly string DOWNLOAD_QUEUE_ID = "dl-queue-1";
        private readonly string DOWNLOAD_COMPLETE_ID = "dl-complete-1";

        public IDownloadClientAdapter CreateAdapter(IDbContextFactory<ListenArrDbContext> db)
        {
            return new SlskdAdapter(
                db,
                new HttpClient(new SlskdApiMock()),
                NullLogger<SlskdAdapter>.Instance);
        }

        private void InitDB(ListenArrDbContext context)
        {
            context.DownloadClientConfigurations.Add(new DownloadClientConfiguration
            {
                Id = CLIENT_CONFIG_ID,
                Name = "Slskd",
                Type = "slskd",
                Host = "localhost",
                Port = 5030
            });

            context.Downloads.Add(new Download
            {
                Id = DOWNLOAD_REMOVE_ID,
                DownloadClientId = CLIENT_CONFIG_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "AnotherOneBiteTheDust",
                    ["Protocol"] = DownloadProtocol.Soulseek,
                    [Download.METADATA_CLIENT_DOWNLOAD_ID_KEY] = new SlskdDownloadIdDto {
                        Ids = [
                            "TRANSFER_1_ID",
                            "TRANSFER_2_ID"
                        ]
                    }
                }
            });

            context.Downloads.Add(new Download
            {
                Id = DOWNLOAD_QUEUE_ID,
                DownloadClientId = CLIENT_CONFIG_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "USER1",
                    ["Protocol"] = DownloadProtocol.Soulseek,
                    [Download.METADATA_CLIENT_DOWNLOAD_ID_KEY] = new SlskdDownloadIdDto {
                        Ids = [
                            "USER1_BOOK2_FILE1",
                            "USER1_BOOK2_FILE2"
                        ]
                    }
                }
            });
        }

        private void InitDBExtended(ListenArrDbContext context)
        {
            InitDB(context);

            context.Downloads.Add(new Download
            {
                Id = DOWNLOAD_COMPLETE_ID,
                DownloadClientId = CLIENT_CONFIG_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "USER2",
                    ["Protocol"] = DownloadProtocol.Soulseek,
                    [Download.METADATA_CLIENT_DOWNLOAD_ID_KEY] = new SlskdDownloadIdDto {
                        Ids = [
                            "USER2_BOOK1_FILE1"
                        ]
                    }
                }
            });

            context.RemotePathMappings.Add(new RemotePathMapping
            {
                Id = 1,
                DownloadClientId = CLIENT_CONFIG_ID,
                Name = "TEST_REMOTE_MAPPING",
                RemotePath = FileUtils.GetAbsolutePath("data", "complete"),
                LocalPath = FileUtils.GetAbsolutePath("remote", "mapped", "complex", "subfolder", "media")
            });
        }

        [Fact]
        [Trait("Method", "AddAsync")]
        public async Task AddAsync()
        {
            var searchResult = new SearchResult
            {
                Uploader = "RandomUser",
                Files = [
                    new FileResult {
                        Filename = "Directory//File1.mp3",
                        Size = 1337
                    },
                    new FileResult {
                        Filename = "Directory//File2.mp3",
                        Size = 42
                    }
                ]
            };

            var db = CreateDB(InitDB);
            var context = db.CreateDbContext();
            var client = context.DownloadClientConfigurations.First(c => c.Id == CLIENT_CONFIG_ID);

            var download = await CreateAdapter(db).AddAsync(client, searchResult);
            
            var clientExist = await context.DownloadClientConfigurations.AnyAsync(c => c.Id == CLIENT_CONFIG_ID);
            var downloadExist = await context.Downloads.AnyAsync(d => d.Id == download.Id);

            Assert.NotNull(download);
            Assert.Equal("slskd-1", download.DownloadClientId);
            Assert.Equal(1379, download.TotalSize);
            Assert.Equal("RandomUser", download.GetMetadata<string>("Uploader"));

            // AddAsync is not responsible for persistence
            Assert.False(downloadExist);
        }

        [Fact]
        [Trait("Method", "RemoveAsync")]
        public async Task RemoveAsync()
        {
            var db = CreateDB(InitDB);
            var context = db.CreateDbContext();
            var client = context.DownloadClientConfigurations.First(c => c.Id == CLIENT_CONFIG_ID);
            var download = context.Downloads.First(d => d.Id == DOWNLOAD_REMOVE_ID);

            var removed = await CreateAdapter(db).RemoveAsync(client, download);

            var clientExist = await context.DownloadClientConfigurations.AnyAsync(c => c.Id == CLIENT_CONFIG_ID);
            var downloadExist = await context.Downloads.AnyAsync(d => d.Id == DOWNLOAD_REMOVE_ID);

            Assert.True(removed);
            Assert.True(clientExist);
            
            // RemoveAsync is not responsible for persistence
            Assert.True(downloadExist);
        }

        [Fact]
        [Trait("Method", "GetQueueAsync")]
        public async Task GetQueueAsync()
        {
            var db = CreateDB(InitDB);
            var context = db.CreateDbContext();
            var client = context.DownloadClientConfigurations.First(c => c.Id == CLIENT_CONFIG_ID);

            var queue = await CreateAdapter(db).GetQueueAsync(client);

            var clientExist = await context.DownloadClientConfigurations.AnyAsync(c => c.Id == CLIENT_CONFIG_ID);
            Assert.True(clientExist);

            Assert.Single(queue);
            Assert.Equal(CLIENT_CONFIG_ID, queue[0].DownloadClientId);
            Assert.Equal(16, Math.Floor(queue[0].Progress));
            Assert.Equal("AAAA\\BBBB\\Awesome Book", queue[0].Title);
            Assert.Equal(30, queue[0].Size);
            Assert.Equal("downloading", queue[0].Status, ignoreCase: true);
        }

        [Fact]
        public async Task FinalizeDownloadAndPathCorrectness()
        {
            var db = CreateDB(InitDBExtended);
            var context = db.CreateDbContext();
            var client = context.DownloadClientConfigurations.First(c => c.Id == CLIENT_CONFIG_ID);
            var download = context.Downloads.First(d => d.Id == DOWNLOAD_COMPLETE_ID);

            var mockService = MockUtils.GetDownloadMonitorServiceMock();

            var queue = await CreateAdapter(db).PollAsync(
                mockService.Object,
                client,
                [download],
                new ApplicationSettings());

            var clientExist = await context.DownloadClientConfigurations.AnyAsync(c => c.Id == CLIENT_CONFIG_ID);
            Assert.True(clientExist);
            
            client = context.DownloadClientConfigurations.First(c => c.Id == CLIENT_CONFIG_ID);
            
            Assert.Equal(FileUtils.GetAbsolutePath("data", "complete"), client.DownloadPath);

            mockService.Verify(s => s.FinalizeDownloadAsync(
                It.IsAny<Download>(),
                FileUtils.GetAbsolutePath("data", "complete", "TEST_SUCCESS"),
                It.IsAny<DownloadClientConfiguration>(),
                It.IsAny<CancellationToken>()
            ), Times.Once);
        }

        [Fact]
        public async Task FinalizeDownloadAndRemotePathMappingCorrectness()
        {
            var db = CreateDB(InitDBExtended);
            var context = db.CreateDbContext();
            var client = context.DownloadClientConfigurations.First(c => c.Id == CLIENT_CONFIG_ID);
            var download = context.Downloads.First(d => d.Id == DOWNLOAD_COMPLETE_ID);

            var downloadProcessingService = TestUtils.GetDownloadProcessingBackgroundService();

            var mockService = MockUtils.GetDownloadMonitorServiceMock();
            
            var queue = new QueueItem
            {
                Id = download.Id,
                Title = "ZZZZ\\YYYY\\TEST_SUCCESS",
                Size = 1337,
                Downloaded = 1337,
                Status = "completed",
                Progress = 100.0,
                DownloadClientId = client.Id,
                ContentPath = FileUtils.GetAbsolutePath("data", "complete", "TEST_SUCCESS")
            };

            var job = await TestUtils.ProcessJobAsync(downloadProcessingService, context, download, queue, client);
            Assert.NotNull(job);
            Assert.Equal(FileUtils.GetAbsolutePath("remote", "mapped", "complex", "subfolder", "media", "TEST_SUCCESS"), job.SourcePath);
        }
    }
}
