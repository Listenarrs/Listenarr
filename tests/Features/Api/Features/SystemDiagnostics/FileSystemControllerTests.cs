using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.SystemDiagnostics;

[Trait("Name", "FileSystemControllerTests")]
[Trait("Category", "Api")]
public sealed class FileSystemControllerTests : BaseTests
{
    [Fact]
    public async Task CheckVolume_UnavailableNativeComparison_AssumesCopyBehavior()
    {
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var volumeResolver = new Mock<IFileSystemVolumeResolver>(MockBehavior.Strict);
        volumeResolver.Setup(resolver => resolver.Compare(
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(new FileSystemVolumeComparison(
                IsAvailable: false,
                SameVolume: false,
                SourceBoundary: null,
                DestinationBoundary: null,
                Reason: "Unavailable"));
        var controller = new FileSystemController(
            NullLogger<FileSystemController>.Instance,
            fileSystem.Object,
            volumeResolver.Object);

        var result = await controller.CheckVolume(
            Path.GetFullPath("source"),
            Path.GetFullPath("destination"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<VolumeCheckResponse>(ok.Value);
        Assert.False(response.SameVolume);
        Assert.True(response.WillBreakHardlinks);
        Assert.Contains("copy behavior", response.Message, StringComparison.OrdinalIgnoreCase);
        volumeResolver.VerifyAll();
    }
}
