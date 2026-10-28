using Listenarr.Application.Mapping;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Mapping
{
    [Trait("Name", "QueueItemConverterTests")]
    [Trait("Category", "QueueItemConverter")]
    public class QueueItemConverterTests : BaseTests
    {
        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_DoesNotCompleteDownload_WhenClientReportsUnknownSizeWithPartialProgress()
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 0,
                DownloadedSize = 0,
                Progress = 10m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = 10,
                Size = 0,
                Downloaded = 0
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Downloading, updated.Status);
            Assert.Equal(10m, updated.Progress);
            Assert.Equal(0L, updated.Metadata["AmountLeft"]);
            Assert.False((bool)updated.Metadata["HasReliableSize"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_PreservesDownloadedSize_WhenClientReportsUnknownSize()
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 1_000,
                DownloadedSize = 100,
                Progress = 10m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = 10,
                Size = 0,
                Downloaded = 0
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Downloading, updated.Status);
            Assert.Equal(10m, updated.Progress);
            Assert.Equal(1_000L, updated.TotalSize);
            Assert.Equal(100L, updated.DownloadedSize);
            Assert.Equal(0L, updated.Metadata["AmountLeft"]);
            Assert.False((bool)updated.Metadata["HasReliableSize"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_DoesNotCompleteDownload_WhenUnknownSizeHasOneHundredProgressButActiveStatus()
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 1_000,
                DownloadedSize = 900,
                Progress = 90m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = 100,
                Size = 0,
                Downloaded = 0
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Downloading, updated.Status);
            Assert.Equal(100m, updated.Progress);
            Assert.Equal(900L, updated.DownloadedSize);
            Assert.False((bool)updated.Metadata["HasReliableSize"]);
        }

        [Theory]
        [Trait("Method", "UpdateFromQueueItem")]
        [InlineData("completed")]
        [InlineData("success")]
        public void UpdateFromQueueItem_CompletesDownload_WhenClientReportsExplicitCompletedStateWithUnknownSize(string status)
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 1_000,
                DownloadedSize = 900,
                Progress = 90m
            };
            var item = new QueueItem
            {
                Status = status,
                Progress = 100,
                Size = 0,
                Downloaded = 0
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Completed, updated.Status);
            Assert.Equal(100m, updated.Progress);
            Assert.Equal(900L, updated.DownloadedSize);
            Assert.False((bool)updated.Metadata["HasReliableSize"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_IgnoresInvalidProgress()
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 1_000,
                DownloadedSize = 100,
                Progress = 10m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = double.NaN,
                Size = 0,
                Downloaded = 0
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Downloading, updated.Status);
            Assert.Equal(10m, updated.Progress);
            Assert.Equal(100L, updated.DownloadedSize);
            Assert.False((bool)updated.Metadata["HasReliableSize"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_CompletesDownload_WhenAmountLeftIsZeroEvenIfProgressIsBelowOneHundred()
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 1_000,
                DownloadedSize = 999,
                Progress = 99.9m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = 99.9,
                Size = 1_000,
                Downloaded = 1_000
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Completed, updated.Status);
            Assert.Equal(0L, updated.Metadata["AmountLeft"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_ClampsNegativeAmountLeft_WhenClientReportsDownloadedGreaterThanSize()
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                TotalSize = 1_000,
                DownloadedSize = 999,
                Progress = 99.9m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = 99.9,
                Size = 1_000,
                Downloaded = 1_001
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Completed, updated.Status);
            Assert.Equal(0L, updated.Metadata["AmountLeft"]);
        }

        [Theory]
        [InlineData(DownloadStatus.ImportPending)]
        [InlineData(DownloadStatus.Processing)]
        [InlineData(DownloadStatus.Moved)]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_DoesNotChangeDownloadPath_ForImportOwnedStates(DownloadStatus status)
        {
            var download = new Download
            {
                Status = status,
                DownloadPath = "/stable/import/path"
            };
            var item = new QueueItem
            {
                Status = "downloading",
                LocalPath = "/stale/client/path",
                ContentPath = "/client/content/path",
                CanRemove = true
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(status, updated.Status);
            Assert.Equal("/stable/import/path", updated.DownloadPath);
            Assert.Equal("/client/content/path", updated.Metadata["ClientContentPath"]);
            Assert.True((bool)updated.Metadata["CanBeRemoved"]);
            Assert.False(updated.Metadata.ContainsKey("ClientState"));
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_DoesNotRegressImportPending_WhenClientReportsDownloading()
        {
            var download = new Download
            {
                Status = DownloadStatus.ImportPending,
                TotalSize = 1_000,
                DownloadedSize = 1_000,
                Progress = 100m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = 45,
                Size = 1_000,
                Downloaded = 450,
                CanRemove = false
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.ImportPending, updated.Status);
            Assert.Equal(100m, updated.Progress);
            Assert.Equal(1_000L, updated.DownloadedSize);
            Assert.False((bool)updated.Metadata["CanBeRemoved"]);
            Assert.False(updated.Metadata.ContainsKey("ClientState"));
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_DoesNotFailImportPending_WhenClientReportsFailed()
        {
            var download = new Download
            {
                Status = DownloadStatus.ImportPending,
                ErrorMessage = null,
                Progress = 100m
            };
            var item = new QueueItem
            {
                Status = "failed",
                Progress = 100,
                ErrorMessage = "Missing files",
                ClientFailureReason = "missing source files",
                CanRemove = true
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.ImportPending, updated.Status);
            Assert.Null(updated.ErrorMessage);
            Assert.False(updated.Metadata.ContainsKey("ClientFailureReason"));
            Assert.True((bool)updated.Metadata["CanBeRemoved"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_DoesNotRegressProcessing_WhenClientReportsQueued()
        {
            var download = new Download
            {
                Status = DownloadStatus.Processing,
                TotalSize = 2_000,
                DownloadedSize = 2_000,
                Progress = 100m
            };
            var item = new QueueItem
            {
                Status = "queued",
                Progress = 0,
                Size = 2_000,
                Downloaded = 0,
                CanRemove = false
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Processing, updated.Status);
            Assert.Equal(100m, updated.Progress);
            Assert.Equal(2_000L, updated.DownloadedSize);
            Assert.False((bool)updated.Metadata["CanBeRemoved"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_DoesNotRegressMoved_WhenClientReportsDownloading()
        {
            var download = new Download
            {
                Status = DownloadStatus.Moved,
                TotalSize = 1_000,
                DownloadedSize = 1_000,
                Progress = 100m
            };
            var item = new QueueItem
            {
                Status = "downloading",
                Progress = 10,
                Size = 1_000,
                Downloaded = 100,
                CanRemove = false
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Moved, updated.Status);
            Assert.Equal(100m, updated.Progress);
            Assert.Equal(1_000L, updated.DownloadedSize);
            Assert.False((bool)updated.Metadata["CanBeRemoved"]);
        }

        [Fact]
        [Trait("Method", "UpdateFromQueueItem")]
        public void UpdateFromQueueItem_StillUpdatesSafeMetadata_ForMovedDownload()
        {
            var download = new Download
            {
                Status = DownloadStatus.Moved,
                Metadata = new Dictionary<string, object>
                {
                    ["ClientContentPath"] = "/old/path"
                }
            };
            var item = new QueueItem
            {
                Status = "failed",
                ContentPath = "/new/path",
                ClientFailureReason = "client says missing files",
                CanRemove = true
            };

            var updated = QueueItemConverter.UpdateFromQueueItem(download, item);

            Assert.Equal(DownloadStatus.Moved, updated.Status);
            Assert.True((bool)updated.Metadata["CanBeRemoved"]);
            Assert.Equal("/new/path", updated.Metadata["ClientContentPath"]);
            Assert.False(updated.Metadata.ContainsKey("ClientFailureReason"));
        }
    }
}
