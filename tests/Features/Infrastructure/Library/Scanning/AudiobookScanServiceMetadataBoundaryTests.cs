using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "AudiobookScanServiceMetadataBoundaryTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookScanServiceMetadataBoundaryTests : BaseTests
{
    [LinuxFact]
    public async Task ScanAsync_CaseDistinctMetadataFolders_RemainConflicting()
    {
        var root = FileService.GetTempDirectory("scan-service-case-distinct-metadata");
        var initialResolution = await _provider
            .GetRequiredService<IFileSystemSemanticsResolver>()
            .ResolveAsync(root);
        Assert.Equal(
            FileSystemCaseSensitivity.Sensitive,
            initialResolution.Semantics.CaseSensitivity);

        var upperDirectory = Path.Join(root, "Metadata Book");
        var lowerDirectory = Path.Join(root, "metadata book");
        var upperFile = Path.Join(upperDirectory, "part-a.m4b");
        var lowerFile = Path.Join(lowerDirectory, "part-b.m4b");
        var metadata = new Mock<IMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(MatchingMetadata());
        Init(services => services.WithSingleton<IMetadataService>(metadata.Object));
        Directory.CreateDirectory(upperDirectory);
        Directory.CreateDirectory(lowerDirectory);
        await File.WriteAllTextAsync(upperFile, "audio");
        await File.WriteAllTextAsync(lowerFile, "audio");
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .Build());
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Expected Title")
                .WithAuthor("Expected Author")
                .Build());
        var authorization = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(root);
        Assert.True(authorization.IsAuthorized, authorization.Error);
        var pathIdentity = Assert.IsType<PathIdentitySnapshot>(authorization.Identity);
        var physicalIdentity = Assert.IsType<ScanPathPhysicalIdentity>(
            authorization.PhysicalIdentity);
        Assert.Equal(
            FileSystemCaseSensitivity.Sensitive,
            pathIdentity.CaseSensitivity);

        var result = await _provider
            .GetRequiredService<IAudiobookScanService>()
            .ScanAsync(new AudiobookScanCommand(
                audiobook.Id,
                root,
                pathIdentity,
                physicalIdentity));

        Assert.Empty(result.AttributedFiles);
        Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Null(result.Audiobook.BasePath);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "MetadataAttributionConflict");
        metadata.Verify(
            service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()),
            Times.Exactly(2));
    }

    private static AudioMetadata MatchingMetadata() => new()
    {
        Title = "Expected Title",
        Artist = "Expected Author",
        Duration = TimeSpan.FromSeconds(1),
        Format = "m4b"
    };
}
