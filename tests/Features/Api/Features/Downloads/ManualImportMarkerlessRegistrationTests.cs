using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportMarkerlessRegistrationTests")]
[Trait("Category", "Api")]
public sealed class ManualImportMarkerlessRegistrationTests : BaseTests
{
    public ManualImportMarkerlessRegistrationTests()
    {
        var metadata = new Mock<IMetadataService>();
        metadata.Setup(service => service.ExtractFileMetadataAsync(
                It.IsAny<string>()))
            .ReturnsAsync(new AudioMetadata
            {
                Title = "Manual Markerless",
                Format = "mp3",
                BitRate = 128000
            });
        Init(builder => builder.WithSingleton(metadata.Object));
    }

    [Fact]
    public async Task Start_Move_UsesMarkerlessRegistrationJournalWithoutLibraryArtifacts()
    {
        var outputRoot = FileService.GetTempDirectory("manual-markerless-out");
        var sourceRoot = FileService.GetTempDirectory("manual-markerless-src");
        var sourceFile = await FileService.GetFileAsync(
            sourceRoot,
            "incoming.mp3",
            "manual markerless audio");
        await AddAuthorizedRootAsync(outputRoot);

        var settings = await _applicationSettingsRepository.GetAsync()
            ?? new ApplicationSettings();
        settings.OutputPath = outputRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);

        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Manual Markerless",
            Authors = ["Author"],
            BasePath = outputRoot
        });
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(
            _provider);
        var request = new ManualImportRequestDto
        {
            Path = sourceRoot,
            Mode = "interactive",
            Action = FileAction.Move,
            Items =
            [
                new ManualImportItemDto
                {
                    FullPath = sourceFile,
                    MatchedAudiobookId = audiobook.Id
                }
            ]
        };

        var action = await controller.Start(request);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            action.Result);
        Assert.Equal(
            1,
            Assert.IsType<int>(ok.Value!.GetType()
                .GetProperty("importedCount")!
                .GetValue(ok.Value)));
        Assert.False(File.Exists(sourceFile));
        var destination = Path.Join(outputRoot, "Manual Markerless.mp3");
        Assert.Equal(
            "manual markerless audio",
            await File.ReadAllTextAsync(destination));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(FileAction.Move, journal.Action);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Equal(audiobook.Id, journal.AudiobookId);
        Assert.Equal(Path.GetFullPath(sourceFile), journal.SourcePath);
        Assert.Equal(Path.GetFullPath(destination), journal.DestinationPath);

        AssertNoListenarrArtifacts(sourceRoot);
        AssertNoListenarrArtifacts(outputRoot);
    }

    private static void AssertNoListenarrArtifacts(string root)
    {
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr",
                StringComparison.OrdinalIgnoreCase));
    }
}
