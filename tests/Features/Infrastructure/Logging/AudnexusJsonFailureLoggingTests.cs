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
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Logging
{
    [Trait("Area", "LogHygiene")]
    [Trait("Name", "AudnexusJsonFailureLoggingTests")]
    [Trait("Category", "AudnexusJsonFailureLogging")]
    public sealed class AudnexusJsonFailureLoggingTests : BaseTests
    {
        private const string Asin = "B008DFUGCQ";

        private sealed class StubHandler(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }

        private sealed class TypedLogger(RecordingLogger inner) : ILogger<AudnexusService>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => inner.Log(logLevel, eventId, state, exception, formatter);
        }

        [Fact]
        public async Task MalformedBodyWithLineBreaks_LogsTheErrorOnASingleLine()
        {
            var logger = new RecordingLogger();
            var service = new AudnexusService(new HttpClient(new StubHandler("{\r\n[forged] not json")), new TypedLogger(logger));

            var result = await service.GetBookMetadataAsync(Asin);

            Assert.Null(result);
            var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
            Assert.StartsWith("JSON deserialization failed", entry.Message);
            Assert.DoesNotContain('\r', entry.Message);
            Assert.DoesNotContain('\n', entry.Message);
        }

        [Fact]
        public async Task ValidBody_ParsesAndLogsNoError()
        {
            var logger = new RecordingLogger();
            var body = $$"""{"asin":"{{Asin}}","title":"A Princess of Mars"}""";
            var service = new AudnexusService(new HttpClient(new StubHandler(body)), new TypedLogger(logger));

            var result = await service.GetBookMetadataAsync(Asin);

            Assert.Equal("A Princess of Mars", result?.Title);
            Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
        }
    }
}
