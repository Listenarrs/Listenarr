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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent
{
    /// <summary>
    /// The add workflow logs the download client's response body on both failure paths. Redaction
    /// masks known secret values and leaves everything else as the client sent it, newlines and
    /// all, so a response body could put arbitrary lines into the log or flood it. These pin that
    /// both call sites go through the sanitizer.
    /// </summary>
    [Trait("Area", "DownloadClients")]
    [Trait("Name", "QbittorrentAddWorkflowLoggingTests")]
    [Trait("Category", "Qbittorrent")]
    public sealed class QbittorrentAddWorkflowLoggingTests : BaseTests
    {
        // A body of the shape a hostile or merely broken client can return: a newline, then a
        // line that reads like a log entry of its own.
        private const string ForgedBody =
            "Fails.\nfail: Listenarr.Api[0] Download client removed every audiobook file";

        private async Task<IReadOnlyList<string>> LoggedLinesForAddStatus(HttpStatusCode status)
        {
            var logs = new AddLogRecorder();
            Init(builder => builder
                .WithSingleton<ILoggerProvider>(logs)
                .WithMocks(AddLogRecorder.CaptureEveryLevel));

            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.AddStatusCode = status;
            apiMock.AddResponseBody = ForgedBody;

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12"
            };

            var gateway = _provider.GetRequiredService<IDownloadClientGateway>();

            await Assert.ThrowsAnyAsync<DownloadClientSubmissionException>(
                () => gateway.AddAsync(client, PreparedSubmissionTestFactory.Torrent(searchResult)));

            return logs.EntriesContaining("Fails.");
        }

        [Fact]
        [Trait("Scenario", "A refusal body cannot forge a log line")]
        public async Task AddAsync_WhenTheClientRefusesTheRelease_SanitizesTheLoggedResponseBody()
        {
            var lines = await LoggedLinesForAddStatus(HttpStatusCode.Conflict);

            var line = Assert.Single(lines);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.Contains("Download client removed every audiobook file", line, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Scenario", "A failure body cannot forge a log line either")]
        public async Task AddAsync_WhenTheClientFails_SanitizesTheLoggedResponseBody()
        {
            // The second call site, three lines from the first. Sanitizing only the line this
            // change adds would leave the identical hole beside it.
            var lines = await LoggedLinesForAddStatus(HttpStatusCode.InternalServerError);

            var line = Assert.Single(lines);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.Contains("Download client removed every audiobook file", line, StringComparison.Ordinal);
        }

        private sealed class AddLogRecorder : ILoggerProvider
        {
            private readonly object _gate = new();
            private readonly List<string> _messages = [];

            // AddLogging() floors the factory at Information. The Error and Information lines
            // under test are above that, but pinning the level here keeps the recorder usable
            // if either call site is ever lowered.
            public static ServiceDescriptor CaptureEveryLevel { get; } =
                ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IConfigureOptions<LoggerFilterOptions>>(
                    new Microsoft.Extensions.Options.ConfigureOptions<LoggerFilterOptions>(
                        options => options.MinLevel = LogLevel.Trace));

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

            public IReadOnlyList<string> EntriesContaining(string fragment)
            {
                lock (_gate)
                {
                    return [.. _messages.Where(message => message.Contains(fragment, StringComparison.Ordinal))];
                }
            }

            public void Dispose()
            {
            }

            private void Record(string message)
            {
                lock (_gate)
                {
                    _messages.Add(message);
                }
            }

            private sealed class RecordingLogger(AddLogRecorder owner) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter)
                {
                    ArgumentNullException.ThrowIfNull(formatter);
                    owner.Record(formatter(state, exception));
                }
            }
        }
    }
}
