using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Area", "Persistence")]
[Trait("Name", "EfAudiobookFileRepositoryOwnershipTransferTests")]
[Trait("Category", "Persistence")]
public sealed class EfAudiobookFileRepositoryOwnershipTransferTests : BaseTests
{
    private static AudiobookFilePathIdentity IdentityFor(string path) =>
        AudiobookFilePathIdentity.CreateValid(
            path,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemCaseSensitivityMode.Auto,
            Path.GetPathRoot(Path.GetFullPath(path))!);

    private async Task<AudiobookFile> ClaimAsync(Audiobook owner, string fileName)
    {
        var path = Path.Join(owner.BasePath, fileName);
        var file = AudiobookFile.CreateUnresolved(path);
        file.AudiobookId = owner.Id;
        file.ApplyPathIdentity(path, IdentityFor(path));
        var claim = await _audiobookFileRepository.ClaimAsync(file);
        Assert.Equal(AudiobookFileClaimOutcome.Created, claim.Outcome);
        return claim.File!;
    }

    [Fact]
    public async Task TransferOwnershipAsync_ReHomesRow_WhenExpectedStateMatches()
    {
        var source = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithBasePath(FileService.GetTempDirectory("owner-a"))
            .Build());
        var target = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithBasePath(FileService.GetTempDirectory("owner-b"))
            .Build());
        var file = await ClaimAsync(source, "part1.m4b");
        var identity = IdentityFor(file.Path!);

        var transferred = await _audiobookFileRepository.TransferOwnershipAsync(
            file.Id,
            source.Id,
            file.CapturePathState(),
            target.Id,
            file.Path!,
            identity);

        Assert.True(transferred);
        var reloaded = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id));
        Assert.Equal(file.Id, reloaded.Id);
        Assert.Equal(identity.OwnershipKey, reloaded.PathOwnershipKey);
        Assert.Equal(PathIdentityState.Valid, reloaded.PathIdentityState);
        Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id));
    }

    [Fact]
    public async Task TransferOwnershipAsync_RefusesStaleExpectations()
    {
        var source = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithBasePath(FileService.GetTempDirectory("owner-c"))
            .Build());
        var target = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithBasePath(FileService.GetTempDirectory("owner-d"))
            .Build());
        var file = await ClaimAsync(source, "part1.m4b");
        var identity = IdentityFor(file.Path!);

        // Wrong current owner: the row is not re-homed.
        Assert.False(await _audiobookFileRepository.TransferOwnershipAsync(
            file.Id,
            target.Id,
            file.CapturePathState(),
            source.Id,
            file.Path!,
            identity));

        // Identity facts that no longer match: the row is not re-homed.
        var drifted = file.CapturePathState() with { OwnershipKey = "stale-key" };
        Assert.False(await _audiobookFileRepository.TransferOwnershipAsync(
            file.Id,
            source.Id,
            drifted,
            target.Id,
            file.Path!,
            identity));

        Assert.Equal(file.Id, Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(source.Id)).Id);
        Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(target.Id));
    }

    [Fact]
    public async Task TransferOwnershipAsync_RequiresValidIdentityAndDistinctOwner()
    {
        var source = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithBasePath(FileService.GetTempDirectory("owner-e"))
            .Build());
        var file = await ClaimAsync(source, "part1.m4b");
        var identity = IdentityFor(file.Path!);
        var unavailable = AudiobookFilePathIdentity.CreateUnavailable(
            file.Path!,
            identity.Syntax,
            identity.RequestedMode,
            identity.BoundaryPath,
            "unavailable");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _audiobookFileRepository.TransferOwnershipAsync(
                file.Id,
                source.Id,
                file.CapturePathState(),
                source.Id,
                file.Path!,
                identity));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _audiobookFileRepository.TransferOwnershipAsync(
                file.Id,
                source.Id,
                file.CapturePathState(),
                source.Id + 1,
                file.Path!,
                unavailable));
    }
}
