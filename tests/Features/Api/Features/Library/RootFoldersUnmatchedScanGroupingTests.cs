using System.Text.Json;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;
using AppRootFoldersController = Listenarr.Api.Features.Library.RootFoldersController;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Name", "RootFoldersUnmatchedScanGroupingTests")]
[Trait("Category", "Api")]
public sealed class RootFoldersUnmatchedScanGroupingTests : BaseTests
{
    // Drives the same path as Library Import: the scan endpoint enqueues the job, the processor
    // runs it against real files, and the results endpoint returns the items. The hosted worker
    // is not started here, so the test reads the job off the queue and runs it itself.
    [Theory]
    [InlineData("Series Book 11 of 20", "Series Book 12 of 20")]
    [InlineData("Rougon-Macquart Book 011 of 020", "Rougon-Macquart Book 012 of 020")]
    [InlineData("Rougon-Macquart Vol 11 of 20", "Rougon-Macquart Vol 12 of 20")]
    [InlineData("Rougon-Macquart Volume 10 of 20", "Rougon-Macquart Volume 11 of 20")]
    public async Task ScanUnmatched_SeriesFilesWithAMultiDigitIndex_ComeBackAsSeparateItems(
        string first,
        string second)
    {
        var items = await ScanFolderAsync(
            "Boxed Set",
            [$"{first}.m4b", $"{second}.m4b"]);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(1, item.GetProperty("FileCount").GetInt32()));
    }

    [Fact]
    public async Task ScanUnmatched_ChapterFilesIndexedAsNumberOfTotal_ComeBackAsOneItem()
    {
        var chapters = Enumerable.Range(1, 6)
            .Select(n => $"A Princess of Mars {n:000} of 006.mp3")
            .ToArray();

        var items = await ScanFolderAsync("A Princess of Mars", chapters);

        var item = Assert.Single(items);
        Assert.Equal(6, item.GetProperty("FileCount").GetInt32());
    }

    // A job canceled through the queue stops the processor at its own checkpoint, even when the
    // processing token itself is not canceled (the queue keeps the job Canceled regardless): it
    // throws, keeps the Canceled status with no results, and never sends the completion event.
    [Fact]
    public async Task ProcessJobAsync_JobCanceledThroughTheQueue_ThrowsAndSkipsTheFolder()
    {
        var pipeline = await BuildScanPipelineAsync("Canceled Scan", ["Some Book.m4b"]);

        Assert.True(pipeline.Queue.TryCancel(pipeline.JobId, out _));

        // Not the job's own token: only the processor's job-cancellation checkpoints can stop it here
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.Processor.ProcessJobAsync(pipeline.Job, CancellationToken.None));

        pipeline.ClientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "UnmatchedScanComplete",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        var results = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            pipeline.Controller.GetUnmatchedResults(pipeline.JobId));
        var payload = JsonSerializer.SerializeToElement(results.Value);
        Assert.Equal("Canceled", payload.GetProperty("status").GetString());
        Assert.Empty(payload.GetProperty("items").EnumerateArray());
    }

    private async Task<List<JsonElement>> ScanFolderAsync(
        string folderName,
        IReadOnlyList<string> fileNames)
    {
        var pipeline = await BuildScanPipelineAsync(folderName, fileNames);
        await pipeline.Processor.ProcessJobAsync(pipeline.Job, CancellationToken.None);

        var results = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            pipeline.Controller.GetUnmatchedResults(pipeline.JobId));
        var payload = JsonSerializer.SerializeToElement(results.Value);
        Assert.Equal("Completed", payload.GetProperty("status").GetString());
        return payload.GetProperty("items").EnumerateArray().ToList();
    }

    private sealed record ScanPipeline(
        AppRootFoldersController Controller,
        UnmatchedScanQueueService Queue,
        UnmatchedScanProcessor Processor,
        Guid JobId,
        UnmatchedScanJob Job,
        Mock<IClientProxy> ClientProxy);

    private async Task<ScanPipeline> BuildScanPipelineAsync(
        string folderName,
        IReadOnlyList<string> fileNames)
    {
        var root = FileService.GetTempDirectory("unmatched-scan-grouping-root");
        var folder = Path.Join(root, folderName);
        Directory.CreateDirectory(folder);
        foreach (var fileName in fileNames)
        {
            await FileService.GetFileAsync(folder, fileName, "audio");
        }

        var rootFolder = await AddAuthorizedRootAsync(root);
        await CreateApplicationSettings();
        var queue = new UnmatchedScanQueueService(
            _provider.GetRequiredService<ILogger<UnmatchedScanQueueService>>(),
            _provider.GetRequiredService<IFileSystemSemanticsResolver>());
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(clients => clients.All).Returns(clientProxy.Object);
        var hubContext = new Mock<IHubContext<SettingsHub>>();
        hubContext.Setup(hub => hub.Clients).Returns(hubClients.Object);
        var processor = new UnmatchedScanProcessor(
            queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<UnmatchedScanProcessor>>(),
            hubContext.Object,
            _provider.GetRequiredService<IFfmpegService>(),
            _provider.GetRequiredService<IFileSystemSemanticsResolver>());
        var controller = ActivatorUtilities.CreateInstance<AppRootFoldersController>(
            _provider,
            (IUnmatchedScanQueueService)queue);

        var enqueued = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            await controller.ScanUnmatched(rootFolder.Id));
        var jobId = Guid.Parse(JsonSerializer.SerializeToElement(enqueued.Value)
            .GetProperty("jobId")
            .GetString()!);
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal(jobId, job.Id);

        return new ScanPipeline(controller, queue, processor, jobId, job, clientProxy);
    }
}
