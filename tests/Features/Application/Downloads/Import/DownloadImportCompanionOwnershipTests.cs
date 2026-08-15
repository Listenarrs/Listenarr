using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Import;

[Trait("Name", "DownloadImportCompanionOwnershipTests")]
[Trait("Category", "DownloadProcessingJob")]
public sealed class DownloadImportCompanionOwnershipTests : BaseTests
{
    public override async Task InitializeAsync()
    {
        Init();
        await AddAuthorizedRootAsync(FileService.GetTempPath());
    }

    [Fact]
    public async Task ImportDownloadFilesAsync_CompanionDestinationOwnedByAnotherAudiobook_IsNotWritten()
    {
        var sourceDirectory = FileService.GetTempDirectory(
            $"download-import-owned-companion-{Guid.NewGuid():N}");
        var destinationDirectory = FileService.GetTempDirectory(
            $"download-import-owned-library-{Guid.NewGuid():N}");
        var audioSource = await FileService.GetFileAsync(sourceDirectory, "book.mp3");
        var companionSource = await FileService.GetFileAsync(sourceDirectory, "cover.jpg");
        var companionDestination = Path.Join(destinationDirectory, "cover.jpg");
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

        var targetAudiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Target Book")
                .WithBasePath(destinationDirectory)
                .Build());
        var otherAudiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Other Book")
                .WithBasePath(destinationDirectory)
                .Build());
        var identityResolver = _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var identity = await identityResolver.ResolveAsync(
            otherAudiobook,
            companionDestination);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var ownedFile = AudiobookFile.CreateUnresolved(companionDestination);
        ownedFile.AudiobookId = otherAudiobook.Id;
        ownedFile.ApplyPathIdentity(companionDestination, identity);
        var claim = await _audiobookFileRepository.ClaimAsync(ownedFile);
        Assert.Equal(AudiobookFileClaimOutcome.Created, claim.Outcome);

        var results = await _provider
            .GetRequiredService<IDownloadImportService>()
            .ImportDownloadFilesAsync(
                targetAudiobook,
                [audioSource, companionSource]);

        Assert.Contains(results, result =>
            result.Success
            && string.Equals(result.SourcePath, audioSource, StringComparison.Ordinal));
        Assert.False(File.Exists(companionDestination));
        Assert.True(File.Exists(companionSource));
        var retained = await _audiobookFileRepository.GetByAudiobookIdAsync(
            otherAudiobook.Id);
        Assert.Contains(retained, file =>
            string.Equals(file.CanonicalPath, identity.CanonicalPath, StringComparison.Ordinal));
    }
}
