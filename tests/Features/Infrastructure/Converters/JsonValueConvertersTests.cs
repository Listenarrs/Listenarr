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
// csharp
using Listenarr.Infrastructure.Persistence.Converters;

namespace Listenarr.Tests.Features.Infrastructure.Converters
{
    public class JsonValueConvertersTests
    {
        [Fact]
        public void JsonValueConverter_SerializesNullToEmptyAndDeserializesToNewInstance()
        {
            var conv = new JsonValueConverter<List<string>>();
            var toProvider = conv.ConvertToProviderExpression.Compile();
            var fromProvider = conv.ConvertFromProviderExpression.Compile();

            string serialized = toProvider(null);
            Assert.Equal(string.Empty, serialized);

            var deserialized = fromProvider(serialized);
            Assert.NotNull(deserialized);
            Assert.Empty(deserialized);
        }

        [Fact]
        public void JsonValueConverter_RoundTripsDictionary()
        {
            var conv = new JsonValueConverter<Dictionary<string, int>>();
            var toProvider = conv.ConvertToProviderExpression.Compile();
            var fromProvider = conv.ConvertFromProviderExpression.Compile();

            var original = new Dictionary<string, int>
            {
                ["one"] = 1,
                ["two"] = 2
            };

            var serialized = toProvider(original);
            Assert.False(string.IsNullOrWhiteSpace(serialized));
            Assert.Contains("\"one\"", serialized);
            Assert.Contains("\"two\"", serialized);

            var deserialized = fromProvider(serialized);
            Assert.NotNull(deserialized);
            Assert.Equal(2, deserialized.Count);
            Assert.Equal(1, deserialized["one"]);
            Assert.Equal(2, deserialized["two"]);
        }
    }
}
