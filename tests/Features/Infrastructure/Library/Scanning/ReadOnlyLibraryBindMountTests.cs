using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "ReadOnlyLibraryBindMountTests")]
[Trait("Category", "Infrastructure")]
public sealed class ReadOnlyLibraryBindMountTests : BaseTests
{
    [ReadOnlyBindMountFact]
    public async Task ScanAsync_RealReadOnlyBindMount_DoesNotMutateLibrary()
    {
        var scanRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                ReadOnlyBindMountFactAttribute.LibraryPathEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The read-only library bind mount was not provided."));
        var expectedFile = Path.Join(
            scanRoot,
            "Author",
            "Book B012345678",
            "01.m4b");
        Assert.True(File.Exists(expectedFile));

        var audiobookToAdd = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();
        audiobookToAdd.Asin = "B012345678";
        var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);

        var settings = await _applicationSettingsRepository.GetAsync()
            ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                new ApplicationSettingsBuilder().Build());
        settings.OutputPath = scanRoot;
        await _applicationSettingsRepository.SaveAsync(settings);

        var authorization = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(scanRoot);
        Assert.True(authorization.IsAuthorized, authorization.Error);
        var pathIdentity = Assert.IsType<PathIdentitySnapshot>(
            authorization.Identity);
        var physicalIdentity = Assert.IsType<ScanPathPhysicalIdentity>(
            authorization.PhysicalIdentity);

        var result = await _provider.GetRequiredService<IAudiobookScanService>()
            .ScanAsync(new AudiobookScanCommand(
                audiobook.Id,
                scanRoot,
                pathIdentity,
                physicalIdentity,
                IsAuthoritativeScope: true));

        Assert.Contains(expectedFile, result.AttributedFiles);
        var tracked = Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(expectedFile, tracked.Path);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                scanRoot,
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr",
                StringComparison.OrdinalIgnoreCase));
    }
}
