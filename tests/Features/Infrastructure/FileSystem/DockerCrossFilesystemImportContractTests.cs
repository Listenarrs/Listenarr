using System.Security.Cryptography;
using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Category", "Infrastructure")]
[Trait("Name", "DockerCrossFilesystemImportContractTests")]
public sealed class DockerCrossFilesystemImportContractTests : BaseTests
{
    [NetworkStorageTheory]
    [InlineData(false, FileAction.Move)]
    [InlineData(false, FileAction.Copy)]
    [InlineData(false, FileAction.HardlinkCopy)]
    [InlineData(true, FileAction.Move)]
    [InlineData(true, FileAction.Copy)]
    [InlineData(true, FileAction.HardlinkCopy)]
    public async Task ImportAcrossMountedFilesystems_VerifiesRegisteredAudioAndCompanion(
        bool manual,
        FileAction action)
    {
        var metadata = new Mock<IMetadataService>();
        metadata.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<string>()))
            .ReturnsAsync(new AudioMetadata { Format = "mp3", Duration = TimeSpan.FromSeconds(1) });
        metadata.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(new AudioMetadata { Format = "mp3", Duration = TimeSpan.FromSeconds(1) });
        var messages = new List<string>();
        var logger = new Mock<ILogger<ManualImportCompanionImporter>>();
        logger.Setup(service => service.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation => messages.Add(
                ((Delegate)invocation.Arguments[4]).DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])
                + Environment.NewLine + invocation.Arguments[3])));
        var gateway = new DownloadClientGatewayMock();
        Init(builder => builder.WithSingleton(metadata.Object).WithSingleton(logger.Object)
            .WithSingleton<IDownloadClientGateway>(gateway));
        var sourceMount = Environment.GetEnvironmentVariable(NetworkStorageTheoryAttribute.PathEnvironmentVariable)!;
        var destinationMount = Environment.GetEnvironmentVariable(CrossVolumeFactAttribute.DestinationPathEnvironmentVariable)
            ?? throw new InvalidOperationException("A cross-filesystem destination is required.");
        var token = Guid.NewGuid().ToString("N");
        var sourceRoot = Path.Join(sourceMount, "cross-import-source-" + token);
        var libraryRoot = Path.Join(destinationMount, "cross-import-library-" + token);
        var bookFolder = Path.Join(libraryRoot, "Book");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(bookFolder);
        var expected = new Dictionary<string, string>
        {
            ["Part 1.mp3"] = "cross-filesystem-chapter-one",
            ["Part 2.mp3"] = "cross-filesystem-chapter-two",
            ["cover.jpg"] = "cross-filesystem-cover"
        };
        foreach (var item in expected)
            await File.WriteAllTextAsync(Path.Join(sourceRoot, item.Key), item.Value);

        var comparison = _provider.GetRequiredService<IFileSystemVolumeResolver>()
            .Compare(sourceRoot, libraryRoot);
        Assert.True(comparison.IsAvailable, comparison.Reason);
        Assert.False(comparison.SameVolume);
        var semantics = await _provider.GetRequiredService<IFileSystemSemanticsResolver>()
            .ResolveAsync(libraryRoot, FileSystemCaseSensitivityMode.Auto);
        Assert.Equal(PathIdentityState.Valid, semantics.State);
        var root = new RootFolderBuilder().WithPath(libraryRoot)
            .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Auto).Build();
        root.ResolvedCaseSensitivity = semantics.Semantics.CaseSensitivity;
        root.PathIdentityState = PathIdentityState.Valid;
        root.PathIdentityKey = FileSystemPathIdentity.CreateKey("root", libraryRoot, semantics.Semantics);
        await _rootFolderRepository.AddAsync(root);
        Assert.Equal(WeakStorageSourceCleanupPolicy.RetainSource, root.WeakStorageSourceCleanupPolicy);
        var health = await _provider.GetRequiredService<IRootFolderStorageHealthResolver>().ResolveAsync(root);
        Assert.True(health.CanPublishAdditively, health.Message);
        var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Cross Filesystem Book").WithBasePath(bookFolder).Build());
        var settings = new ApplicationSettingsBuilder().WithOutputPath(libraryRoot)
            .WithoutMetadataProcessing().WithFolderNamingPattern("")
            .WithFileNamingPattern("{Title}").WithMultiFileNamingPattern("{Title}-{DiskNumber:00}").Build();
        settings.CompletedFileAction = action;
        await _applicationSettingsRepository.SaveAsync(settings);

        if (manual)
        {
            var request = new ManualImportRequestDto
            {
                Path = sourceRoot,
                Mode = "interactive",
                Action = action,
                IncludeCompanionFiles = true,
                Items = expected.Keys.Where(name => name.EndsWith(".mp3", StringComparison.Ordinal))
                    .Select(name => new ManualImportItemDto
                    {
                        FullPath = Path.Join(sourceRoot, name),
                        MatchedAudiobookId = book.Id
                    }).ToList()
            };
            var response = Assert.IsType<OkObjectResult>((await ActivatorUtilities
                .CreateInstance<ManualImportController>(_provider).Start(request)).Result);
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(response.Value);
            Assert.Equal(2, payload.GetProperty("importedCount").GetInt32());
        }
        else
        {
            gateway.SourceFiles = expected.Keys.Select(name => Path.Join(sourceRoot, name)).ToList();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow).WithPath(sourceRoot).WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(book).Build());
            var jobId = await _provider.GetRequiredService<IDownloadProcessingJobService>().EnqueueAsync(download);
            Assert.NotEmpty(jobId);
            await _provider.GetRequiredService<DownloadProcessingJobProcessor>().ProcessQueueAsync(CancellationToken.None);
        }

        var registered = await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id);
        Assert.Equal(2, registered.Count(file => file.Path!.EndsWith(".mp3", StringComparison.Ordinal)));
        var published = Directory.GetFiles(libraryRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".mp3", StringComparison.Ordinal) || path.EndsWith(".jpg", StringComparison.Ordinal))
            .ToArray();
        Assert.True(published.Length == 3, string.Join(Environment.NewLine, messages));
        var expectedHashes = expected.Values.Select(content => Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))).Order().ToArray();
        var actualHashes = published.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .Order().ToArray();
        Assert.Equal(expectedHashes, actualHashes);
        Assert.All(registered, file => Assert.Contains(file.Path!, published));
        foreach (var item in expected)
        {
            var source = Path.Join(sourceRoot, item.Key);
            if (action != FileAction.Move) Assert.True(File.Exists(source));
            if (File.Exists(source)) Assert.Equal(item.Value, await File.ReadAllTextAsync(source));
        }
    }
}
