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
using System.Text.Json;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Indexers
{
    [Trait("Area", "LogHygiene")]
    [Trait("Name", "MyAnonamousePublishDateParserLoggingTests")]
    [Trait("Category", "MyAnonamousePublishDateParser")]
    public sealed class MyAnonamousePublishDateParserLoggingTests : BaseTests
    {
        private static JsonElement ItemWithAdded(string added)
            => JsonDocument.Parse(JsonSerializer.Serialize(new { added })).RootElement;

        [Fact]
        public void Parse_InvalidAddedWithLineBreaks_DoesNotWriteTheUpstreamValueToTheLog()
        {
            var logger = new RecordingLogger();
            var added = "not-a-date\r\n[forged] 2000-01-01 00:00:00 Error injected line";

            var result = MyAnonamousePublishDateParser.Parse(ItemWithAdded(added), "Title", logger);

            Assert.Null(result);
            var output = logger.AllOutput();
            Assert.DoesNotContain("\r", output);
            Assert.DoesNotContain("\n[forged]", output);
            Assert.Single(logger.Entries);
            Assert.DoesNotContain('\n', logger.Entries[0].Message);
        }

        [Fact]
        public void Parse_InvalidAddedValue_StillLogsThatTheTimestampDidNotParse()
        {
            var logger = new RecordingLogger();

            MyAnonamousePublishDateParser.Parse(ItemWithAdded("not-a-date"), "Title", logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Contains("did not parse", entry.Message);
            Assert.Null(entry.Exception);
        }

        [Fact]
        public void Parse_ValidAddedValue_ParsesTheDateAndLogsNothing()
        {
            var logger = new RecordingLogger();

            var result = MyAnonamousePublishDateParser.Parse(ItemWithAdded("2025-06-12 09:35:05"), "Title", logger);

            Assert.Equal(
                new DateTime(2025, 6, 12, 9, 35, 5, DateTimeKind.Utc).ToLocalTime(),
                result);
            Assert.Empty(logger.Entries);
        }
    }
}
