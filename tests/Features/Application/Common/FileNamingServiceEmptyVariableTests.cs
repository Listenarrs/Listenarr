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
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Common
{
    [Trait("Area", "Naming")]
    [Trait("Name", "FileNamingServiceEmptyVariableTests")]
    [Trait("Category", "Naming")]
    public class FileNamingServiceEmptyVariableTests : BaseTests
    {
        private static string Apply(string pattern, bool treatAsFilename, params (string Name, object? Value)[] values)
        {
            var service = new FileNamingService(Mock.Of<IConfigurationService>(), NullLogger<FileNamingService>.Instance);
            var variables = values.ToDictionary(v => v.Name, v => v.Value!);
            return service.ApplyNamingPattern(pattern, variables, treatAsFilename);
        }

        [Theory]
        [InlineData("{SeriesNumber:00} {Series} - {Title} - {Author}")]
        [InlineData("{Series} {SeriesNumber:00} - {Title} - {Author}")]
        [InlineData("{Series}{SeriesNumber} - {Title} - {Author}")]
        [InlineData("{Series} - {SeriesNumber:00} - {Title} - {Author}")]
        public void Drops_adjacent_empty_variables_without_leaving_sentinel_fragments(string pattern)
        {
            var result = Apply(pattern, true,
                ("SeriesNumber", null), ("Series", null),
                ("Title", "L'art subtil de s'en foutre"), ("Author", "Mark Manson"));

            Assert.Equal("L'art subtil de s'en foutre - Mark Manson", result);
        }

        [Fact]
        public void Keeps_series_tokens_when_they_have_values()
        {
            var result = Apply("{SeriesNumber:00} {Series} - {Title} - {Author}", true,
                ("SeriesNumber", "6"), ("Series", "Primal Hunter"), ("Title", "Primal Hunter"), ("Author", "Zogarth"));

            Assert.Equal("06 Primal Hunter - Primal Hunter - Zogarth", result);
        }

        [Fact]
        public void Drops_adjacent_empty_variables_in_folder_patterns()
        {
            var result = Apply("{Author}/{Series} {SeriesNumber:00}/{Title}", false,
                ("Author", "Mark Manson"), ("Series", null), ("SeriesNumber", null), ("Title", "L'art subtil de s'en foutre"));

            Assert.Equal(Path.Join("Mark Manson", "L'art subtil de s'en foutre"), result);
        }
    }
}
