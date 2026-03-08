using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Listenarr.Api.Controllers;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    [Trait("Area", "DownloadsApi")]
    public class DownloadsControllerTests
    {
        [Fact]
        public async Task GetDownloads_FiltersDisabledClients_AndKeepsDDL()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Downloads.AddRange(
                new Download
                {
                    Id = "d-enabled",
                    Title = "Enabled",
                    Status = DownloadStatus.Completed,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow.AddMinutes(-1)
                },
                new Download
                {
                    Id = "d-disabled",
                    Title = "Disabled",
                    Status = DownloadStatus.Completed,
                    DownloadClientId = "client-disabled",
                    StartedAt = DateTime.UtcNow.AddMinutes(-2)
                },
                new Download
                {
                    Id = "d-ddl",
                    Title = "DDL",
                    Status = DownloadStatus.Completed,
                    DownloadClientId = "DDL",
                    StartedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>
                {
                    new DownloadClientConfiguration { Id = "client-enabled", Name = "Enabled Client", IsEnabled = true },
                    new DownloadClientConfiguration { Id = "client-disabled", Name = "Disabled Client", IsEnabled = false }
                });

            var controller = new DownloadsController(
                db,
                NullLogger<DownloadsController>.Instance,
                configMock.Object,
                null);

            var action = await controller.GetDownloads();

            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var ids = ExtractIds(payload);

            Assert.Contains("d-enabled", ids);
            Assert.Contains("d-ddl", ids);
            Assert.DoesNotContain("d-disabled", ids);
        }

        [Fact]
        public async Task GetActiveDownloads_FiltersDisabledClients_AndKeepsDDL()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Downloads.AddRange(
                new Download
                {
                    Id = "a-enabled",
                    Title = "Enabled Active",
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow.AddMinutes(-1)
                },
                new Download
                {
                    Id = "a-disabled",
                    Title = "Disabled Active",
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "client-disabled",
                    StartedAt = DateTime.UtcNow.AddMinutes(-2)
                },
                new Download
                {
                    Id = "a-ddl",
                    Title = "DDL Active",
                    Status = DownloadStatus.Queued,
                    DownloadClientId = "DDL",
                    StartedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>
                {
                    new DownloadClientConfiguration { Id = "client-enabled", Name = "Enabled Client", IsEnabled = true },
                    new DownloadClientConfiguration { Id = "client-disabled", Name = "Disabled Client", IsEnabled = false }
                });

            var controller = new DownloadsController(
                db,
                NullLogger<DownloadsController>.Instance,
                configMock.Object,
                null);

            var action = await controller.GetActiveDownloads();

            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var ids = ExtractIds(payload);

            Assert.Contains("a-enabled", ids);
            Assert.Contains("a-ddl", ids);
            Assert.DoesNotContain("a-disabled", ids);
        }

        [Fact]
        [Trait("Scenario", "ActiveEndpointIncludesImportPendingAndExcludesTerminalStates")]
        public async Task GetActiveDownloads_IncludesImportPending_ExcludesImportBlocked()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Downloads.AddRange(
                new Download
                {
                    Id = "d-queued",
                    Title = "Queued",
                    Status = DownloadStatus.Queued,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "d-downloading",
                    Title = "Downloading",
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "d-processing",
                    Title = "Processing",
                    Status = DownloadStatus.Processing,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "d-importpending",
                    Title = "Import Pending",
                    Status = DownloadStatus.ImportPending,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "d-importblocked",
                    Title = "Import Blocked",
                    Status = DownloadStatus.ImportBlocked,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "d-failed",
                    Title = "Failed",
                    Status = DownloadStatus.Failed,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "d-moved",
                    Title = "Moved",
                    Status = DownloadStatus.Moved,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>
                {
                    new DownloadClientConfiguration { Id = "client-enabled", Name = "Enabled Client", IsEnabled = true }
                });

            var controller = new DownloadsController(
                db,
                NullLogger<DownloadsController>.Instance,
                configMock.Object,
                null);

            var action = await controller.GetActiveDownloads();
            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var ids = ExtractIds(payload);

            Assert.Contains("d-queued", ids);
            Assert.Contains("d-downloading", ids);
            Assert.Contains("d-processing", ids);
            Assert.Contains("d-importpending", ids);

            Assert.DoesNotContain("d-importblocked", ids);
            Assert.DoesNotContain("d-failed", ids);
            Assert.DoesNotContain("d-moved", ids);
        }

        [Fact]
        [Trait("Scenario", "ClearFailedRemovesFailedAndImportBlockedOnly")]
        public async Task ClearFailedDownloads_RemovesOnlyFailedAndImportBlocked()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Downloads.AddRange(
                new Download
                {
                    Id = "keep-queued",
                    Title = "Queued",
                    Status = DownloadStatus.Queued,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "remove-failed",
                    Title = "Failed",
                    Status = DownloadStatus.Failed,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "remove-importblocked",
                    Title = "ImportBlocked",
                    Status = DownloadStatus.ImportBlocked,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                },
                new Download
                {
                    Id = "keep-completed",
                    Title = "Completed",
                    Status = DownloadStatus.Completed,
                    DownloadClientId = "client-enabled",
                    StartedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>());

            var controller = new DownloadsController(
                db,
                NullLogger<DownloadsController>.Instance,
                configMock.Object,
                null);

            var action = await controller.ClearFailedDownloads();
            var ok = Assert.IsType<OkObjectResult>(action);
            Assert.NotNull(ok.Value);

            var countObj = ok.Value!.GetType().GetProperty("count")?.GetValue(ok.Value);
            var count = countObj is int i ? i : Convert.ToInt32(countObj);
            Assert.Equal(2, count);

            var remaining = await db.Downloads.Select(d => d.Id).ToListAsync();
            Assert.Contains("keep-queued", remaining);
            Assert.Contains("keep-completed", remaining);
            Assert.DoesNotContain("remove-failed", remaining);
            Assert.DoesNotContain("remove-importblocked", remaining);
        }

        [Fact]
        [Trait("Scenario", "BlockedDownloadDetailsIncludeReasonMessagesAttempts")]
        public async Task GetDownload_ImportBlocked_IncludesBlockReasonAndMessages()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Downloads.Add(new Download
            {
                Id = "d-blocked",
                Title = "Blocked Download",
                Status = DownloadStatus.ImportBlocked,
                DownloadClientId = "client-enabled",
                ImportBlockReason = "NoImportableFiles",
                ImportBlockMessages = new List<string> { "Manual interaction is required." },
                ImportAttempts = 3,
                StartedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>
                {
                    new DownloadClientConfiguration { Id = "client-enabled", Name = "Enabled Client", IsEnabled = true }
                });

            var controller = new DownloadsController(
                db,
                NullLogger<DownloadsController>.Instance,
                configMock.Object,
                null);

            var action = await controller.GetDownload("d-blocked");

            var ok = Assert.IsType<OkObjectResult>(action.Result);
            Assert.NotNull(ok.Value);

            var reason = ok.Value!.GetType().GetProperty("importBlockReason")?.GetValue(ok.Value)?.ToString();
            var messages = ok.Value.GetType().GetProperty("importBlockMessages")?.GetValue(ok.Value) as IEnumerable<string>;
            var attemptsObj = ok.Value.GetType().GetProperty("importAttempts")?.GetValue(ok.Value);
            var attempts = attemptsObj is int i ? i : Convert.ToInt32(attemptsObj);

            Assert.Equal("NoImportableFiles", reason);
            Assert.NotNull(messages);
            Assert.Contains(messages!, m => m.Contains("Manual interaction", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, attempts);
        }

        [Fact]
        [Trait("Scenario", "RetryBlockedImportResetsToImportPending")]
        public async Task RetryBlockedImport_ImportBlocked_TransitionsToImportPending()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Downloads.Add(new Download
            {
                Id = "d-retry",
                Title = "Retry Blocked",
                Status = DownloadStatus.ImportBlocked,
                DownloadClientId = "client-enabled",
                ImportBlockReason = "RepeatedFailure",
                ImportBlockMessages = new List<string> { "still failing" },
                ImportAttempts = 3,
                StartedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>());

            var controller = new DownloadsController(
                db,
                NullLogger<DownloadsController>.Instance,
                configMock.Object,
                null);

            var action = await controller.RetryBlockedImport("d-retry");
            var ok = Assert.IsType<OkObjectResult>(action);
            Assert.NotNull(ok.Value);

            var status = ok.Value!.GetType().GetProperty("status")?.GetValue(ok.Value)?.ToString();
            Assert.Equal("ImportPending", status);

            var updated = await db.Downloads.FindAsync("d-retry");
            Assert.NotNull(updated);
            Assert.Equal(DownloadStatus.ImportPending, updated!.Status);
            Assert.Null(updated.ImportBlockReason);
            Assert.Null(updated.ImportBlockMessages);
            Assert.Equal(0, updated.ImportAttempts);
        }

        [Fact]
        [Trait("Scenario", "RetryBlockedImportRejectsNonBlockedStatus")]
        public async Task RetryBlockedImport_NonBlocked_ReturnsBadRequest()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Downloads.Add(new Download
            {
                Id = "d-not-blocked",
                Title = "Not Blocked",
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-enabled",
                StartedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var configMock = new Mock<IConfigurationService>();
            configMock
                .Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration>());

            var controller = new DownloadsController(
                db,
                NullLogger<DownloadsController>.Instance,
                configMock.Object,
                null);

            var action = await controller.RetryBlockedImport("d-not-blocked");
            var badRequest = Assert.IsType<BadRequestObjectResult>(action);
            Assert.NotNull(badRequest.Value);

            var status = badRequest.Value!.GetType().GetProperty("status")?.GetValue(badRequest.Value)?.ToString();
            Assert.Equal("Downloading", status);
        }

        private static HashSet<string> ExtractIds(IEnumerable<object> payload)
        {
            return payload
                .Select(item => item.GetType().GetProperty("id")?.GetValue(item)?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
