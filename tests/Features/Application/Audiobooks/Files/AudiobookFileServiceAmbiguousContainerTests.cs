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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files;

// Listenarr#890 / #995. Registration holds the one content gate for ambiguous containers: the
// extension check in front of it is a pre-filter that only decides whether a probe is worth
// doing. Manual import refuses a film before any filesystem mutation, but registration has to
// refuse it again on its own, because a gate whose only enforcement lives in one controller is a
// gate that the next caller forgets. Each test here is paired with a control that differs in one
// input and comes out the other way.
[Trait("Name", "AudiobookFileServiceAmbiguousContainerTests")]
[Trait("Category", "Application")]
[Trait("Scenario", "AmbiguousContainerRegistration")]
public sealed class AudiobookFileServiceAmbiguousContainerTests : BaseTests, IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception.Message);
            }
        }

        _tempDirectories.Clear();
    }

    private sealed record RegistrationOutcome(
        bool Registered,
        int ClaimCalls,
        int ProbeCalls);

    private async Task<RegistrationOutcome> RegisterAsync(
        string fileName,
        AudioMetadata? probedMetadata)
    {
        var basePath = Path.Join(
            Path.GetTempPath(),
            $"listenarr-registration-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        _tempDirectories.Add(basePath);
        var physicalPath = Path.GetFullPath(Path.Join(basePath, fileName));
        await File.WriteAllTextAsync(physicalPath, "container bytes");
        var audiobook = new Audiobook
        {
            Id = 7701,
            Title = "Registration Gate Book",
            BasePath = basePath
        };

        var audiobookRepository = new Mock<IAudiobookRepository>();
        audiobookRepository.Setup(repository => repository.GetByIdSnapshotAsync(
                audiobook.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(audiobook);
        var claimCalls = 0;
        var fileRepository = new Mock<IAudiobookFileRepository>();
        fileRepository.Setup(repository => repository.ClaimAsync(
                It.IsAny<AudiobookFile>(),
                It.IsAny<CancellationToken>()))
            .Returns<AudiobookFile, CancellationToken>((file, _) =>
            {
                claimCalls++;
                return Task.FromResult(new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.Created,
                    file));
            });

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(system => system.FileExists(physicalPath)).Returns(true);
        fileSystem.Setup(system => system.IsReparsePoint(physicalPath)).Returns(false);
        var validatedPath = physicalPath;
        var validationReason = string.Empty;
        fileSystem.Setup(system => system.TryValidateMutationTarget(
                physicalPath,
                It.IsAny<IEnumerable<string?>>(),
                out validatedPath,
                out validationReason))
            .Returns(true);

        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
        semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                semantics,
                PathIdentityState.Valid,
                basePath,
                CanonicalPath: basePath));

        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>();
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AudiobookFilePathIdentity.CreateValid(
                physicalPath,
                semantics,
                FileSystemCaseSensitivityMode.Auto,
                basePath));

        var rootFolderService = new Mock<IRootFolderService>();
        rootFolderService.Setup(service => service.GetAllAsync()).ReturnsAsync([]);
        var moveQueueService = new Mock<IMoveQueueService>();
        moveQueueService.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var probeCalls = 0;
        var metadataService = new Mock<IMetadataService>();
        metadataService.Setup(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(() =>
            {
                probeCalls++;
                return probedMetadata;
            });

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new AudiobookFileService(
            memoryCache,
            new MetadataExtractionLimiter(),
            audiobookRepository.Object,
            fileRepository.Object,
            Mock.Of<IHistoryRepository>(),
            metadataService.Object,
            Mock.Of<IToastService>(),
            Mock.Of<IFfmpegService>(),
            fileSystem.Object,
            semanticsResolver.Object,
            identityResolver.Object,
            rootFolderService.Object,
            NullLogger<AudiobookFileService>.Instance,
            new FilesystemMutationCoordinator(),
            new AudiobookOperationCoordinator(),
            moveQueueService.Object);

        var registered = await service.EnsureAudiobookFileAsync(
            audiobook,
            physicalPath,
            "manual-import");

        return new RegistrationOutcome(registered, claimCalls, probeCalls);
    }

    [Fact]
    public async Task EnsureAudiobookFileAsync_AudioOnlyMp4_RegistersTheFile()
    {
        var outcome = await RegisterAsync(
            "Registration Gate Book.mp4",
            new AudioMetadata
            {
                Format = "aac",
                Duration = TimeSpan.FromHours(1),
                HasAudioStream = true,
                HasVideoStream = false
            });

        Assert.True(outcome.Registered);
        Assert.Equal(1, outcome.ClaimCalls);
    }

    // The control. Same extension, same wiring, one extra probed stream, opposite outcome, and
    // nothing was claimed in the catalog. Without this the change would admit films.
    [Fact]
    public async Task EnsureAudiobookFileAsync_VideoBearingMp4_RefusesAndClaimsNothing()
    {
        var outcome = await RegisterAsync(
            "Registration Gate Film.mp4",
            new AudioMetadata
            {
                Format = "aac",
                Duration = TimeSpan.FromHours(1),
                HasAudioStream = true,
                HasVideoStream = true
            });

        Assert.False(outcome.Registered);
        Assert.Equal(0, outcome.ClaimCalls);
    }

    [Fact]
    public async Task EnsureAudiobookFileAsync_Mp4WithNoAudioStream_RefusesAndClaimsNothing()
    {
        var outcome = await RegisterAsync(
            "Registration Gate Silence.mp4",
            new AudioMetadata
            {
                Format = "mov,mp4,m4a,3gp,3g2,mj2",
                Duration = TimeSpan.FromHours(1),
                HasAudioStream = false,
                HasVideoStream = false
            });

        Assert.False(outcome.Registered);
        Assert.Equal(0, outcome.ClaimCalls);
    }

    // A probe that came back empty is not evidence of audio. Refusing here is what keeps a
    // missing or broken ffprobe from silently widening what the library accepts.
    [Fact]
    public async Task EnsureAudiobookFileAsync_Mp4WithUnavailableProbe_RefusesAndClaimsNothing()
    {
        var outcome = await RegisterAsync("Registration Gate Unprobed.mp4", probedMetadata: null);

        Assert.False(outcome.Registered);
        Assert.Equal(0, outcome.ClaimCalls);
    }

    // The regression control for the accepted extensions. The probe result is the one that
    // refuses an .mp4 above, and an .m4b registers on it anyway, because an always-audio
    // extension short-circuits the content gate and never has to prove anything.
    [Fact]
    public async Task EnsureAudiobookFileAsync_AcceptedExtensionWithNoProbedStreams_StillRegisters()
    {
        var outcome = await RegisterAsync(
            "Registration Gate Book.m4b",
            new AudioMetadata
            {
                Format = "m4b",
                Duration = TimeSpan.FromHours(1),
                HasAudioStream = false,
                HasVideoStream = false
            });

        Assert.True(outcome.Registered);
        Assert.Equal(1, outcome.ClaimCalls);
    }

    // The pre-filter did not widen past .mp4, and the proof that costs the scanner nothing: a
    // video extension is refused before the probe runs at all, so ProbeCalls is zero. An
    // extension outside both tiers never reaches ffprobe no matter what its content is.
    [Fact]
    public async Task EnsureAudiobookFileAsync_VideoExtension_RefusesWithoutProbing()
    {
        var outcome = await RegisterAsync(
            "Registration Gate Film.mkv",
            new AudioMetadata
            {
                Format = "matroska",
                Duration = TimeSpan.FromHours(1),
                HasAudioStream = true,
                HasVideoStream = false
            });

        Assert.False(outcome.Registered);
        Assert.Equal(0, outcome.ClaimCalls);
        Assert.Equal(0, outcome.ProbeCalls);
    }
}
