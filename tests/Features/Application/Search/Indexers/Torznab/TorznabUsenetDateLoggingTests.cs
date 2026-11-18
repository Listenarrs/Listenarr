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
using System.Reflection;
using Listenarr.Application.Search.Indexers.Torznab;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Indexers.Torznab
{
    [Trait("Area", "LogHygiene")]
    [Trait("Name", "TorznabUsenetDateLoggingTests")]
    [Trait("Category", "TorznabUsenetDate")]
    public sealed class TorznabUsenetDateLoggingTests : BaseTests
    {
        private static readonly Indexer TestIndexer = new() { Name = "test", Url = "https://example.com", Type = "Usenet", Implementation = "torznab" };

        // A usenetdate that long.TryParse accepts (it allows surrounding white space, including
        // line breaks) but that FromUnixTimeSeconds rejects as out of range.
        private const string OutOfRangeWithLineBreaks = "&#13;&#10;99999999999999&#13;&#10;";

        private static string FeedWithUsenetDate(string usenetDate) => $"""
            <?xml version="1.0"?>
            <rss>
              <channel>
                <item>
                  <title>Test Book</title>
                  <guid>abc123</guid>
                  <torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="usenetdate" value="{usenetDate}" />
                  <enclosure url="https://example.com/test.torrent" length="123456" />
                </item>
              </channel>
            </rss>
            """;

        public static TheoryData<string> Parsers => new() { "application", "infrastructure" };

        private static async Task<List<IndexerSearchResult>> ParseWith(string parser, string xml, RecordingLogger logger)
        {
            if (parser == "application")
            {
                return await new TorznabResponseParser(new HttpClient(), logger).ParseAsync(xml, TestIndexer);
            }

            var provider = new TorznabNewznabSearchProvider(new HttpClient(), new LoggerAdapter(logger));
            var method = typeof(TorznabNewznabSearchProvider).GetMethod("ParseTorznabResponseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return await (Task<List<IndexerSearchResult>>)method.Invoke(provider, [xml, TestIndexer])!;
        }

        [Theory]
        [MemberData(nameof(Parsers))]
        public async Task UsenetDateOutOfRange_WithLineBreaks_DoesNotWriteTheRawValueToTheLog(string parser)
        {
            var logger = new RecordingLogger();

            var results = await ParseWith(parser, FeedWithUsenetDate(OutOfRangeWithLineBreaks), logger);

            Assert.Single(results);
            var entry = Assert.Single(logger.Entries, e => e.Message.StartsWith("Could not convert the Torznab usenetdate"));
            Assert.DoesNotContain('\r', entry.Message);
            Assert.DoesNotContain('\n', entry.Message);
            // The exception stays attached: it is the framework's out-of-range error, which can only
            // echo the already-parsed number and not the raw string, so there is no text to forge.
            Assert.IsType<ArgumentOutOfRangeException>(entry.Exception);
        }

        [Theory]
        [MemberData(nameof(Parsers))]
        public async Task UsenetDateValid_ParsesTheDateAndLogsNoConversionFailure(string parser)
        {
            var logger = new RecordingLogger();

            var results = await ParseWith(parser, FeedWithUsenetDate("1750000000"), logger);

            var result = Assert.Single(results);
            Assert.Equal(
                new DateTime(2025, 6, 15, 15, 6, 40, DateTimeKind.Utc),
                DateTimeOffset.Parse(result.PublishedDate!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
            Assert.DoesNotContain(logger.Entries, e => e.Message.StartsWith("Could not convert the Torznab usenetdate"));
        }

        private sealed class LoggerAdapter(RecordingLogger inner) : ILogger<TorznabNewznabSearchProvider>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
