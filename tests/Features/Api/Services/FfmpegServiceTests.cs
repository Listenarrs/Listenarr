using Listenarr.Infrastructure.Ffmpeg;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "FfmpegServiceTests")]
    [Trait("Category", "FfmpegService")]
    public class FfmpegServiceTests : BaseTests
    {
        // FIXME: This is too longo for unit tests
        //[Fact]
        [Trait("Method", "EnsureFfprobeInstalledAsync")]
        [Trait("Category", "Release")]
        public async Task EnsureFfprobeInstalledAsync()
        {
            var ffmpegDirectory = Path.Combine(FileService.GetTempPath(), "ffmpeg");

            Assert.False(Path.Exists(ffmpegDirectory));

            var ffmpegService = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                _provider.GetRequiredService<IStartupConfigService>(),
                _provider.GetRequiredService<IProcessRunner>(),
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == ffmpegDirectory));

            var ffprobePath = await ffmpegService.EnsureFfprobeInstalledAsync();

            Assert.NotNull(ffprobePath);
            Assert.True(Path.Exists(ffprobePath));
            Assert.True(Path.Exists(ffmpegDirectory));
        }
    }
}
