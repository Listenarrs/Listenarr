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
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Domain.Common;

[Trait("Name", "NaturalSortTests")]
[Trait("Category", "Domain")]
public sealed class NaturalSortTests
{
    private static int Compare(string left, string right) =>
        string.CompareOrdinal(NaturalSort.ToKey(left), NaturalSort.ToKey(right));

    [Theory]
    [InlineData("Disc 2", "Disc 10")]
    [InlineData("Chapter 9", "Chapter 10")]
    [InlineData("Part 1", "Part 1a")]
    [InlineData("Track 2147483647", "Track 2147483648")]
    [InlineData("Part 9000000000", "Part 30000000000")]
    [InlineData("1234567890123 Part 2", "1234567890123 Part 10")]
    [InlineData("Book 1 Disc 2", "Book 1 Disc 10")]
    [InlineData("Book 1 Disc 10", "Book 2 Disc 1")]
    public void ToKey_OrdersNumericRunsByMagnitude(string first, string second)
    {
        Assert.True(Compare(first, second) < 0, $"'{first}' should sort before '{second}'");
    }

    [Theory]
    [InlineData("disc 2", "DISC 2")]
    [InlineData("Chapter 01", "chapter 1")]
    public void ToKey_NaturalPortionIgnoresCaseAndZeroPadding(string left, string right)
    {
        var leftKey = NaturalSort.ToKey(left);
        var rightKey = NaturalSort.ToKey(right);

        Assert.Equal(NaturalPortion(leftKey), NaturalPortion(rightKey));
        Assert.NotEqual(leftKey, rightKey);
    }

    [Theory]
    [InlineData("Part 01", "Part 1")]
    [InlineData("Part 001", "Part 01")]
    [InlineData("Part 1", "part 1")]
    public void ToKey_BreaksTiesByOrdinalOriginal(string first, string second)
    {
        Assert.True(string.CompareOrdinal(first, second) < 0, "test fixture must be in ordinal order");
        Assert.True(Compare(first, second) < 0, $"'{first}' should sort before '{second}'");
        Assert.True(Compare(second, first) > 0, $"'{second}' should sort after '{first}'");
    }

    [Theory]
    [InlineData("000", "0")]
    [InlineData("007", "7")]
    public void ToKey_LeadingZerosDoNotChangeMagnitude(string padded, string bare)
    {
        Assert.Equal(
            NaturalPortion(NaturalSort.ToKey(padded)),
            NaturalPortion(NaturalSort.ToKey(bare)));
    }

    [Fact]
    public void ToKey_SortsAMixedPaddingDiscSetDeterministically()
    {
        var names = new[] { "Disc 10", "Disc 2", "Disc 02", "disc 1", "Disc 1", "Disc 01" };

        var sorted = names.OrderBy(NaturalSort.ToKey, StringComparer.Ordinal).ToArray();

        Assert.Equal(["Disc 01", "Disc 1", "disc 1", "Disc 02", "Disc 2", "Disc 10"], sorted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToKey_EmptyInputYieldsEmptyKey(string? value)
    {
        Assert.Equal(string.Empty, NaturalSort.ToKey(value));
    }

    [Fact]
    public void InNaturalOrder_SortsByFileNameOnly_AndSinksPathlessFiles()
    {
        var disc10 = new AudiobookFileBuilder().WithPath("/lib/a/Disc 10.mp3").Build();
        var disc2 = new AudiobookFileBuilder().WithPath("/lib/z/Disc 2.mp3").Build();
        var disc1 = new AudiobookFileBuilder().WithPath(@"C:\lib\m\Disc 1.mp3").Build();
        var pathless = new AudiobookFileBuilder().Build();
        pathless.Path = null;
        pathless.Id = 99;

        var ordered = AudiobookFileOrdering.InNaturalOrder([pathless, disc10, disc2, disc1]).ToArray();

        Assert.Equal([disc1, disc2, disc10, pathless], ordered);
    }

    [Fact]
    public void InNaturalOrder_NullSource_YieldsEmpty()
    {
        Assert.Empty(AudiobookFileOrdering.InNaturalOrder(null));
    }

    private static string NaturalPortion(string key)
    {
        var separator = key.IndexOf('\u0001');
        return separator < 0 ? key : key[..separator];
    }
}
