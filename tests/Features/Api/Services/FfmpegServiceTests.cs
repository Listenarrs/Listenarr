using Listenarr.Application.Interfaces;
using Listenarr.Infrastructure.Ffmpeg;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

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
            var ffmpegDirectory = Path.Join(AppContext.BaseDirectory, "config", "ffmpeg");

            try
            {
                Directory.Delete(ffmpegDirectory, true);
            }
            catch (DirectoryNotFoundException)
            {
            }

            Assert.False(Path.Exists(ffmpegDirectory));

            var ffmpegService = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                _provider.GetRequiredService<IStartupConfigService>(),
                _provider.GetRequiredService<IProcessRunner>());

            var ffprobePath = await ffmpegService.EnsureFfprobeInstalledAsync();

            Assert.NotNull(ffprobePath);
            Assert.True(Path.Exists(ffprobePath));
            Assert.True(Path.Exists(ffmpegDirectory));

            // Cleanup
            Directory.Delete(ffmpegDirectory, true);
            Assert.False(Path.Exists(ffmpegDirectory));
        }
    }
}
