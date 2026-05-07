using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks
{
    public class FfmpegServiceMock : IFfmpegService, IAsyncDisposable
    {
        private TempFileService _fileService = new();
        private string _ffprobePath = "";

        public async ValueTask DisposeAsync()
        {
            await _fileService.DisposeAsync();
        }

        public async Task<string?> EnsureFfprobeInstalledAsync()
        {
            return _ffprobePath;
        }

        public async Task<string?> GetFfprobePathAsync()
        {
            if (string.IsNullOrWhiteSpace(_ffprobePath))
            {
                _ffprobePath = await _fileService.GetTempFileAsync("ffprobefake");
            }

            return _ffprobePath;
        }

        public async Task<string> GetLicenseAsync()
        {
            return "LICENSED Listenarr mock corp. V0";
        }

        public async Task<AudioMetadata> RunFfprobeAsync(string filePath)
        {
            if (filePath.Contains("withmetadata"))
            {
                return new AudioMetadataBuilder()
                    .WithTitle("Super Tag")
                    .WithAlbum("Awesome unrelated")
                    .WithArtist("Mister nobody")
                    .WithDisc(1)
                    .WithTrack(3)
                    .WithYear(2026)
                    .Build();
            }

            return new AudioMetadataBuilder().Build();
        }
    }
}
