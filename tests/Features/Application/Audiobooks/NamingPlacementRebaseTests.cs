using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Infrastructure.Persistence.Migrations;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Application.Audiobooks;

[Trait("Name", "NamingPlacementRebaseTests")]
[Trait("Category", "Naming")]
public sealed class NamingPlacementRebaseTests : BaseTests
{
    [Theory]
    [InlineData("{Author}/{Title}", "{Title}", "Author/Book")]
    [InlineData("", "{Author}/{Title}/audio", "Author/Book")]
    [InlineData("", "{Title}", "")]
    [InlineData("", "{Author}\\{Title}\\audio", "Author/Book")]
    [InlineData("{Author}/{Title} - {Subtitle}", "{Title}", "Author/Book - Subtitle")]
    [InlineData("", "{Author}/{Title} - {Subtitle}/audio", "Author/Book - Subtitle")]
    public void DirectoryPreview_UsesTheCurrentNamingService(string folder, string file, string expected)
    {
        var book = new Audiobook { Title = "Book", Subtitle = "Subtitle", Authors = ["Author"] };
        var settings = new ApplicationSettings { FolderNamingPattern = folder, FileNamingPattern = file };
        var naming = _provider.GetRequiredService<IFileNamingService>();
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), AudiobookNamingDirectory.Render(book, settings, naming));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManualPlanning_UsesPlacementInsteadOfTheObservedPath(bool custom, bool separateFolder)
    {
        var root = FileService.GetTempDirectory("placement-root");
        var observed = FileService.GetTempDirectory("observed");
        var book = new Audiobook { Title = "Book", Subtitle = "Subtitle", Authors = ["Author"], BasePath = observed };
        if (custom) book.SetCustomDestination(root); else book.SetNamingRoot(root);
        var settings = new ApplicationSettings
        {
            OutputPath = root,
            FolderNamingPattern = separateFolder ? "{Author}/{Title}" : "",
            MultiFileNamingPattern = separateFolder ? "Disc {DiskNumber:00}/Part {ChapterNumber:00}" : "{Author}/{Title}/Disc {DiskNumber:00}/Part {ChapterNumber:00}"
        };
        var planner = new ManualImportPathPlanner(_provider.GetRequiredService<IFileNamingService>());
        var result = await planner.GeneratePathAsync(book, new AudioMetadata(), new ManualImportItemDto
        {
            FullPath = Path.Join(root, "part1.wav"),
            DiskNumberHint = 1,
            ChapterNumberHint = 1
        }, observed, [], settings,
            new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, FileSystemCaseSensitivity.Sensitive), true);
        var expected = custom
            ? separateFolder ? Path.Join(root, "Disc 01", "Part 01.wav") : Path.Join(root, "Part 01.wav")
            : Path.Join(root, "Author", "Book", "Disc 01", "Part 01.wav");
        Assert.Equal(expected, result.DestinationPath);
    }

    [Fact]
    public void PlacementMigration_PreservesCanaryModelAndAddsNullableContext()
    {
        var model = new AddAudiobookNamingDestination().TargetModel;
        var book = model.FindEntityType(typeof(Audiobook).FullName!);
        Assert.NotNull(book);
        Assert.True(book.FindProperty(nameof(Audiobook.NamingRootPath))!.IsNullable);
        Assert.True(book.FindProperty(nameof(Audiobook.CustomDestinationPath))!.IsNullable);
        Assert.NotNull(model.FindEntityType(typeof(RootFolderRelocation).FullName!));
    }

    [Fact]
    public async Task Placement_RoundTripsWithoutChangingAnObservedBasePath()
    {
        var observed = FileService.GetTempDirectory("observed-base");
        var root = FileService.GetTempDirectory("selected-root");
        var book = new Audiobook { Title = "Roundtrip", BasePath = observed };
        book.SetNamingRoot(root);
        var saved = await _audiobookRepository.AddAsync(book);
        var reloaded = await _audiobookRepository.GetByIdAsync(saved.Id);
        Assert.Equal(root, reloaded!.NamingRootPath);
        Assert.Null(reloaded.CustomDestinationPath);
        Assert.Equal(observed, reloaded.BasePath);
        reloaded.SetCustomDestination(observed);
        await _audiobookRepository.UpdateAsync(reloaded);
        var custom = await _audiobookRepository.GetByIdAsync(saved.Id);
        Assert.Null(custom!.NamingRootPath);
        Assert.Equal(observed, custom.CustomDestinationPath);
    }
    [Fact]
    public async Task DetachedMetadata_DoesNotRevertPlacementIntent()
    {
        var book = new Audiobook { Title = "Original", BasePath = FileService.GetTempDirectory("detached-base") };
        book.SetNamingRoot(FileService.GetTempDirectory("old-root"));
        var saved = await _audiobookRepository.AddAsync(book);
        var detached = await _audiobookRepository.GetByIdSnapshotAsync(saved.Id);
        saved.SetCustomDestination(saved.BasePath!);
        await _audiobookRepository.UpdateAsync(saved);
        detached!.Title = "Updated";
        await _audiobookRepository.UpdateAsync(detached);
        var current = await _audiobookRepository.GetByIdAsync(saved.Id);
        Assert.Equal("Updated", current!.Title);
        Assert.Null(current.NamingRootPath);
        Assert.Equal(saved.BasePath, current.CustomDestinationPath);
    }

    [Fact]
    public async Task Ownership_UsesPersistedPlacementAndRejectsCallerSuppliedPlacement()
    {
        var root = FileService.GetTempDirectory("owned-placement");
        var outside = FileService.GetTempDirectory("outside-placement");
        await AddAuthorizedRootAsync(root);
        var book = new Audiobook { Title = "Ownership", BasePath = Path.Join(root, "old-layout") };
        book.SetNamingRoot(root);
        var saved = await _audiobookRepository.AddAsync(book);
        var service = _provider.GetRequiredService<IAudiobookFileService>();
        var permitted = await service.CheckAudiobookFileOwnershipAsync(saved, Path.Join(root, "new-layout", "part1.wav"));
        Assert.Equal(AudiobookFileOwnershipCheckOutcome.Available, permitted.Outcome);
        var spoofed = new Audiobook { Id = saved.Id, BasePath = outside, NamingRootPath = outside };
        var rejected = await service.CheckAudiobookFileOwnershipAsync(spoofed, Path.Join(outside, "part1.wav"));
        Assert.Equal(AudiobookFileOwnershipCheckOutcome.IdentityUnavailable, rejected.Outcome);
        Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(saved.Id));
    }

    [Fact]
    public void DirectoryPreview_UsesTheSamePrimaryAuthorAsManualNaming()
    {
        var book = new Audiobook { Title = "Book", Authors = ["First Author", "Second Author"] };
        var settings = new ApplicationSettings { FolderNamingPattern = "{Author}/{Title}" };
        var naming = _provider.GetRequiredService<IFileNamingService>();
        Assert.Equal(Path.Join("First Author", "Book"), AudiobookNamingDirectory.Render(book, settings, naming));
    }

    [Fact]
    public async Task RootReassignment_PreservesPlacementWithoutAnObservedBasePath()
    {
        var source = await AddAuthorizedRootAsync(FileService.GetTempDirectory("future-source"));
        var target = await AddAuthorizedRootAsync(FileService.GetTempDirectory("future-target"), "Target");
        var book = new Audiobook { Title = "Future placement", NamingRootPath = source.Path };
        book = await _audiobookRepository.AddAsync(book);
        var semantics = new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, source.ResolvedCaseSensitivity);
        Assert.True(await _rootFolderRepository.HasAudiobooksUnderPathAsync(source.Path, semantics));
        Assert.Contains(await _rootFolderRepository.GetAudiobooksUnderPathAsync(source.Path, semantics), row => row.Id == book.Id);
        await _provider.GetRequiredService<IRootFolderService>().DeleteAsync(source.Id, target.Id);
        var current = await _audiobookRepository.GetByIdSnapshotAsync(book.Id);
        Assert.Equal(target.Path, current!.NamingRootPath);
        Assert.True(string.IsNullOrEmpty(current.BasePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataUpdate_PreservesIntentForEquivalentPathsAndRejectsInvalidPaths(bool invalid)
    {
        var root = FileService.GetTempDirectory("metadata-placement-root");
        await AddAuthorizedRootAsync(root);
        var book = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Before",
            BasePath = root,
            NamingRootPath = root
        });
        var result = await _provider.GetRequiredService<LibraryUpdateWorkflow>().UpdateAsync(book.Id,
            new AudiobookUpdateRequest { Title = "After", BasePath = invalid ? "\0" : root + Path.DirectorySeparatorChar });
        if (invalid) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<OkObjectResult>(result);
        var current = await _audiobookRepository.GetByIdSnapshotAsync(book.Id);
        Assert.Equal(invalid ? "Before" : "After", current!.Title);
        Assert.Equal(root, current.NamingRootPath);
        Assert.Null(current.CustomDestinationPath);
    }

}
