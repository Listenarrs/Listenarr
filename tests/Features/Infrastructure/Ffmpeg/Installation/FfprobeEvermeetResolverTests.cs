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
using System.IO.Compression;
using System.Net;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    [Trait("Name", "FfprobeEvermeetResolverTests")]
    [Trait("Category", "FfmpegService")]
    public class FfprobeEvermeetResolverTests : BaseTests
    {
        private const string Fallback = FfprobePlatformDefaults.MacOsFallbackUrl;

        private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                return respond(request, cancellationToken);
            }
        }

        private static StubHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

        private static string ReleaseInfo(string zipUrl) =>
            "{\"name\":\"ffprobe\",\"version\":\"1.2.3\",\"download\":{\"zip\":{\"url\":\"" + zipUrl + "\"}}}";

        private Task<string> ResolveAsync(StubHandler handler) =>
            FfprobeEvermeetResolver.ResolveAsync(
                new HttpClient(handler), Fallback, new Mock<ILogger>().Object);

        [Fact]
        public async Task ResolveAsync_ReturnsTheZipUrlNamedByTheReleaseInfo()
        {
            var handler = Json(ReleaseInfo("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip"));

            var url = await ResolveAsync(handler);

            Assert.Equal("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip", url);
            Assert.Equal(FfprobeEvermeetResolver.ReleaseInfoUrl, Assert.Single(handler.Requests).ToString());
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.InternalServerError)]
        public async Task ResolveAsync_WhenTheApiFails_UsesTheFallback(HttpStatusCode status)
        {
            Assert.Equal(Fallback, await ResolveAsync(Json("{}", status)));
        }

        [Fact]
        public async Task ResolveAsync_WhenTheRequestThrows_UsesTheFallback()
        {
            var handler = new StubHandler((_, _) => throw new HttpRequestException("unreachable"));

            Assert.Equal(Fallback, await ResolveAsync(handler));
        }

        [Fact]
        public async Task ResolveAsync_WhenTheLookupHangs_GivesUpAtTheTimeoutAndUsesTheFallback()
        {
            var handler = new StubHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage();
            });

            var task = ResolveAsync(handler);

            Assert.Equal(Fallback, await task.WaitAsync(FfprobeEvermeetResolver.LookupTimeout + TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public async Task ResolveAsync_WhenTheCallerCancels_Propagates()
        {
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            var handler = new StubHandler((_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage());
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                FfprobeEvermeetResolver.ResolveAsync(
                    new HttpClient(handler), Fallback, new Mock<ILogger>().Object, cancellation.Token));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"download\":{}}")]
        [InlineData("{\"download\":{\"zip\":{}}}")]
        [InlineData("{\"download\":{\"zip\":{\"url\":null}}}")]
        [InlineData("{\"download\":{\"zip\":{\"url\":42}}}")]
        public async Task ResolveAsync_WhenTheResponseIsMalformed_UsesTheFallback(string body)
        {
            Assert.Equal(Fallback, await ResolveAsync(Json(body)));
        }

        [Theory]
        [InlineData("http://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip")]
        [InlineData("https://example.invalid/ffmpeg/ffprobe-1.2.3.zip")]
        [InlineData("https://evermeet.cx/ffmpeg/get/ffprobe")]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.7z")]
        [InlineData("/ffmpeg/ffprobe-1.2.3.zip")]
        [InlineData("https://evermeet.cx.evil.com/ffmpeg/ffprobe-1.2.3.zip")]
        [InlineData("https://evermeet.cx@evil.com/x.zip")]
        [InlineData("https://evil.evermeet.cx/ffmpeg/ffprobe-1.2.3.zip")]
        [InlineData("https://notevermeet.cx/ffmpeg/ffprobe-1.2.3.zip")]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip?download=1")]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip#download")]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip ")]
        public async Task ResolveAsync_WhenTheUrlIsUnsupported_UsesTheFallback(string zipUrl)
        {
            Assert.Equal(Fallback, await ResolveAsync(Json(ReleaseInfo(zipUrl))));
        }

        [Fact]
        public async Task ResolveAsync_AcceptsAnUppercaseZipExtension()
        {
            const string Url = "https://evermeet.cx/ffmpeg/ffprobe-1.2.3.ZIP";

            Assert.Equal(Url, await ResolveAsync(Json(ReleaseInfo(Url))));
        }

        [Theory]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip", true)]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.ZIP", true)]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip?download=1", false)]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip#download", false)]
        [InlineData("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip ", false)]
        [InlineData("https://evermeet.cx/ffmpeg/get/ffprobe", false)]
        public void IsZipArchiveUrl_MatchesOnlyAUrlThatEndsInZip(string url, bool expected)
        {
            Assert.Equal(expected, FfprobeArchiveExtractor.IsZipArchiveUrl(url));
        }

        [Fact]
        public async Task ResolveIfPinnedAsync_WithAnotherUrl_MakesNoRequest()
        {
            var handler = Json(ReleaseInfo("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip"));
            const string Override = "https://example.invalid/custom-ffprobe.zip";

            var url = await FfprobeEvermeetResolver.ResolveIfPinnedAsync(
                Override, new HttpClient(handler), new Mock<ILogger>().Object);

            Assert.Equal(Override, url);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task ResolveIfPinnedAsync_WithThePinnedUrl_LooksUpOnce()
        {
            var handler = Json(ReleaseInfo("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip"));

            var url = await FfprobeEvermeetResolver.ResolveIfPinnedAsync(
                Fallback, new HttpClient(handler), new Mock<ILogger>().Object);

            Assert.Equal("https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip", url);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void MacOsFallbackUrl_IsAVersionedZip()
        {
            Assert.Matches(@"ffprobe-\d+\.\d+(\.\d+)?\.zip$", Fallback);
        }

        [Fact]
        public async Task ExtractAsync_ForAnEvermeetZip_WritesTheFfprobeBinary()
        {
            var baseDir = FileService.GetTempDirectory("ffprobe-extract");
            var archivePath = Path.Join(baseDir, "ffprobe-download.tmp");
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("ffprobe");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("binary");
            }
            var ffprobePath = Path.Join(baseDir, "ffprobe");

            var extracted = await FfprobeArchiveExtractor.ExtractAsync(
                Fallback, archivePath, baseDir, ffprobePath, new Mock<ILogger>().Object);

            Assert.True(extracted);
            Assert.Equal("binary", await File.ReadAllTextAsync(ffprobePath));
        }

        [Fact]
        public async Task EnsureFfprobeInstalledAsync_WhenTheLookupUrlHasAQuery_InstallsFromThePinnedArchiveNotTheRawDownload()
        {
            const string QueryUrl = "https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip?download=1";
            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-query");
            var zipBytes = BuildZip("ffprobe", "binary");
            var releases = "[{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"ffprobe.zip\",\"browser_download_url\":\"" + Fallback + "\"}]}]";
            // Both archive URLs serve a real ZIP, so a raw install of either would "succeed";
            // only the extraction path leaves the extracted binary rather than the archive itself.
            var handler = new StubHandler((request, _) =>
            {
                var uri = request.RequestUri!.ToString();
                HttpContent? content = uri switch
                {
                    _ when request.RequestUri.Host == "api.github.com" => new StringContent(releases),
                    FfprobeEvermeetResolver.ReleaseInfoUrl => new StringContent(ReleaseInfo(QueryUrl)),
                    Fallback or QueryUrl => new ByteArrayContent(zipBytes),
                    _ => null,
                };
                return Task.FromResult(content is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            var startupConfig = Mock.Of<IStartupConfigService>(config => config.GetConfig() == new StartupConfig
            {
                Ffmpeg = new FfmpegConfig { Provider = "github:example/ffprobe-builds" },
            });
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(handler),
                startupConfig,
                _provider.GetRequiredService<IProcessRunner>(),
                Mock.Of<IApplicationPathService>(paths => paths.FfmpegRootPath == ffmpegDirectory));

            var installed = await service.EnsureFfprobeInstalledAsync();

            var requested = handler.Requests.Select(uri => uri.ToString()).ToList();
            Assert.DoesNotContain(QueryUrl, requested);
            Assert.Contains(Fallback, requested);
            Assert.NotNull(installed);
            Assert.Equal("binary", await File.ReadAllTextAsync(installed!));
        }

        private static byte[] BuildZip(string entryName, string content)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
                writer.Write(content);
            }
            return stream.ToArray();
        }

        [Fact]
        public async Task EnsureFfprobeInstalledAsync_WithAnExistingInstall_KeepsItWithoutAnyRequest()
        {
            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-existing");
            var ffprobePath = Path.Join(ffmpegDirectory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            await File.WriteAllTextAsync(ffprobePath, "existing");
            var handler = new StubHandler((_, _) => throw new InvalidOperationException("no request expected"));
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(handler),
                _provider.GetRequiredService<IStartupConfigService>(),
                _provider.GetRequiredService<IProcessRunner>(),
                Mock.Of<IApplicationPathService>(paths => paths.FfmpegRootPath == ffmpegDirectory));

            var installed = await service.EnsureFfprobeInstalledAsync();

            Assert.Equal(ffprobePath, installed);
            Assert.Empty(handler.Requests);
            Assert.Equal("existing", await File.ReadAllTextAsync(ffprobePath));
        }

        [Fact]
        public async Task EnsureFfprobeInstalledAsync_ForThePinnedMacOsUrl_DownloadsTheLookedUpArchive()
        {
            const string LookedUp = "https://evermeet.cx/ffmpeg/ffprobe-1.2.3.zip";
            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-pinned");
            // A provider override is the only way to hand the installer the pinned macOS URL on a
            // host that is not macOS: the stubbed releases listing names it as the asset.
            var releases = "[{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"ffprobe.zip\",\"browser_download_url\":\"" + Fallback + "\"}]}]";
            var handler = new StubHandler((request, _) =>
            {
                var body = request.RequestUri!.Host switch
                {
                    "api.github.com" => releases,
                    _ when request.RequestUri.ToString() == FfprobeEvermeetResolver.ReleaseInfoUrl => ReleaseInfo(LookedUp),
                    _ => null,
                };
                return Task.FromResult(body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            });
            var startupConfig = Mock.Of<IStartupConfigService>(config => config.GetConfig() == new StartupConfig
            {
                Ffmpeg = new FfmpegConfig { Provider = "github:example/ffprobe-builds" },
            });
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(handler),
                startupConfig,
                _provider.GetRequiredService<IProcessRunner>(),
                Mock.Of<IApplicationPathService>(paths => paths.FfmpegRootPath == ffmpegDirectory));

            await service.EnsureFfprobeInstalledAsync();

            var requested = handler.Requests.Select(uri => uri.ToString()).ToList();
            Assert.Contains(FfprobeEvermeetResolver.ReleaseInfoUrl, requested);
            Assert.Contains(LookedUp, requested);
            Assert.DoesNotContain(Fallback, requested);
        }

        // Opt-in because it needs the network and the live release; the unit tests above cover the
        // same parsing against stubbed responses.
        [LiveFfprobeFact]
        [Trait("Category", "Live")]
        public async Task ResolveAsync_AgainstTheLiveApi_ReturnsAVersionedEvermeetZip()
        {
            var url = await FfprobeEvermeetResolver.ResolveAsync(
                new HttpClient(), Fallback, new Mock<ILogger>().Object);

            Assert.StartsWith("https://evermeet.cx/ffmpeg/ffprobe-", url);
            Assert.EndsWith(".zip", url);
        }
    }
}
