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
using System.Net;
using Listenarr.Infrastructure.Images.Validation;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Logging
{
    [Trait("Area", "LogHygiene")]
    [Trait("Name", "UpstreamUrlFailureLoggingTests")]
    [Trait("Category", "UpstreamUrlFailureLogging")]
    public sealed class UpstreamUrlFailureLoggingTests : BaseTests
    {
        private const string SignedUrl = "https://downloads.example.com/assets/checksums-sha256.txt?X-Signature=abc123";

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(respond(request));
        }

        private static string ReleaseFeedWithChecksumAsset(string assetUrl)
            => $$"""[{"tag_name":"v1","assets":[{"name":"checksums-sha256.txt","browser_download_url":"{{assetUrl}}"}]}]""";

        private static HttpClient FeedThenFailure(string assetUrl) => new(new StubHandler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseFeedWithChecksumAsset(assetUrl)) }
                : throw new HttpRequestException("connection refused")));

        [Fact]
        public async Task FfprobeChecksumDownloadFailure_DoesNotLogTheSignedQueryOrLineBreaks()
        {
            var logger = new RecordingLogger();
            var discoverer = new FfprobeGithubAssetDiscoverer(FeedThenFailure(SignedUrl + "\\r\\n[forged]"), logger);

            await discoverer.TryDiscoverAsync("example/repo", null, null);

            var entry = Assert.Single(logger.Entries, e => e.Message.StartsWith("Could not download the checksum asset"));
            Assert.Contains("https://downloads.example.com/assets/checksums-sha256.txt", entry.Message);
            Assert.DoesNotContain("X-Signature", entry.Message);
            Assert.DoesNotContain("forged", entry.Message);
            Assert.DoesNotContain('\r', entry.Message);
            Assert.DoesNotContain('\n', entry.Message);
        }

        [Fact]
        public async Task CoverProbeFailure_DoesNotLogTheSignedQueryOrLineBreaks()
        {
            var logger = new RecordingLogger();
            var probe = new ImageSharpCoverImageProbe(
                new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused"))),
                new TypedLogger<ImageSharpCoverImageProbe>(logger));

            await probe.ProbeAsync("https://covers.example.com/cover.jpg?X-Signature=abc123");

            var entry = Assert.Single(logger.Entries);
            Assert.Contains("https://covers.example.com/cover.jpg", entry.Message);
            Assert.DoesNotContain("X-Signature", entry.Message);
            Assert.IsType<HttpRequestException>(entry.Exception);
        }

        [Fact]
        public async Task CoverProbeSuccess_LogsNothing()
        {
            var logger = new RecordingLogger();
            var probe = new ImageSharpCoverImageProbe(
                new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                new TypedLogger<ImageSharpCoverImageProbe>(logger));

            var dimensions = await probe.ProbeAsync("https://covers.example.com/cover.jpg?X-Signature=abc123");

            Assert.Null(dimensions);
            Assert.Empty(logger.Entries);
        }

        private sealed class TypedLogger<T>(RecordingLogger inner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
