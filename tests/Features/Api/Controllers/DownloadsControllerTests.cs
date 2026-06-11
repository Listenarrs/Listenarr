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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Area", "DownloadsApi")]
    [Trait("Name", "DownloadsControllerTests")]
    [Trait("Category", "DownloadsController")]
    public class DownloadsControllerTests : BaseTests
    {
        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder().Build();
        public override async Task InitializeAsync()
        {
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("client-enabled")
                .WithName("Enabled Client")
                .Enabled()
                .Build());

            var disabledClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("client-disabled")
                .WithName("Disabled Client")
                .Disabled()
                .Build());

            var directDownloadClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("DDL")
                .WithName("Direct Download")
                .Enabled()
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-enabled")
                .WithStatus(DownloadStatus.Downloading)
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-disabled")
                .WithStatus(DownloadStatus.Queued)
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(disabledClient)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-ddl")
                .WithStatus(DownloadStatus.Downloading)
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(directDownloadClient)
                .Build());
        }

        [Fact]
        [Trait("Method", "GetDownloads")]
        public async Task GetDownloads_FiltersDisabledClients_AndKeepsDDL()
        {
            var controller = MockUtils.CreateDownloadsController(_provider);
            var action = await controller.GetDownloads();

            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var ids = ExtractIds(payload);

            Assert.Contains("d-enabled", ids);
            Assert.Contains("d-ddl", ids);
            Assert.DoesNotContain("d-disabled", ids);
        }

        [Fact]
        [Trait("Method", "GetActiveDownloads")]
        public async Task GetActiveDownloads_FiltersDisabledClients_AndKeepsDDL()
        {
            var controller = MockUtils.CreateDownloadsController(_provider);
            var action = await controller.GetActiveDownloads();

            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var ids = ExtractIds(payload);

            Assert.Contains("d-enabled", ids);
            Assert.Contains("d-ddl", ids);
            Assert.DoesNotContain("d-disabled", ids);
        }

        [Fact]
        [Trait("Method", "GetActiveDownloads")]
        [Trait("Scenario", "ActiveEndpointIncludesImportPendingAndExcludesTerminalStates")]
        public async Task GetActiveDownloads_IncludesImportPending_ExcludesImportBlocked()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-queued")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Queued)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-downloading")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Downloading)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-processing")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Processing)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-importpending")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-importblocked")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.ImportBlocked)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-failed")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Failed)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-moved")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Moved)
                .Build());

            var controller = MockUtils.CreateDownloadsController(_provider);
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
        [Trait("Method", "ClearFailedDownloads")]
        [Trait("Scenario", "ClearFailedRemovesFailedAndImportBlockedOnly")]
        public async Task ClearFailedDownloads_RemovesOnlyFailedAndImportBlocked()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("keep-queued")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Queued)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("remove-failed")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Failed)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("remove-importblocked")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.ImportBlocked)
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("keep-completed")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithCompletedStatus(DateTime.UtcNow)
                .Build());

            var controller = MockUtils.CreateDownloadsController(_provider);
            var action = await controller.ClearFailedDownloads();
            var ok = Assert.IsType<OkObjectResult>(action);
            Assert.NotNull(ok.Value);

            var countObj = ok.Value!.GetType().GetProperty("count")?.GetValue(ok.Value);
            var count = countObj is int i ? i : Convert.ToInt32(countObj);
            Assert.Equal(2, count);

            var remaining = (await _downloadRepository.GetAllAsync()).Select(download => download.Id).ToList();
            Assert.Contains("keep-queued", remaining);
            Assert.Contains("keep-completed", remaining);
            Assert.DoesNotContain("remove-failed", remaining);
            Assert.DoesNotContain("remove-importblocked", remaining);
        }

        [Fact]
        [Trait("Method", "ClearFailedDownloads")]
        [Trait("Scenario", "BlockedDownloadDetailsIncludeReasonMessagesAttempts")]
        public async Task GetDownload_ImportBlocked_IncludesBlockReasonAndMessages()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-blocked")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithBlockedStatus("NoImportableFiles")
                .WithImportAttempts(3)
                .WithBlockMessage("Manual interaction is required.")
                .Build());

            var controller = MockUtils.CreateDownloadsController(_provider);
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
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "RetryBlockedImportResetsToImportPending")]
        public async Task RetryBlockedImport_ImportBlocked_TransitionsToImportPending()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-retry")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithBlockedStatus("RepeatedFailure")
                .WithImportAttempts(3)
                .WithBlockMessage("still failing")
                .Build());

            var controller = MockUtils.CreateDownloadsController(_provider);
            var action = await controller.RetryBlockedImport("d-retry");
            var ok = Assert.IsType<OkObjectResult>(action);
            Assert.NotNull(ok.Value);

            var status = ok.Value!.GetType().GetProperty("status")?.GetValue(ok.Value)?.ToString();
            Assert.Equal("ImportPending", status);

            var updated = await _downloadRepository.GetByIdAsync("d-retry");
            Assert.NotNull(updated);
            Assert.Equal(DownloadStatus.ImportPending, updated!.Status);
            Assert.Null(updated.ImportBlockReason);
            Assert.Null(updated.ImportBlockMessages);
            Assert.Equal(0, updated.ImportAttempts);
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "RetryBlockedImportRejectsNonBlockedStatus")]
        public async Task RetryBlockedImport_NonBlocked_ReturnsBadRequest()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("d-not-blocked")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithDownloadClientConfiguration(_client)
                .WithStatus(DownloadStatus.Downloading)
                .Build());

            var controller = MockUtils.CreateDownloadsController(_provider);
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
