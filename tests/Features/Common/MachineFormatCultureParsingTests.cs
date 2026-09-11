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
using System.Globalization;
using System.Text.Json;
using Listenarr.Application.Search.Indexers.Torznab;
using Listenarr.Infrastructure.Ffmpeg.Metadata;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Common
{
    /// <summary>
    /// ffprobe, MyAnonamouse, SABnzbd and Torznab all emit numbers in machine format: '.' is the
    /// decimal separator, whatever locale the emitting process runs under. Parsing those strings
    /// with the ambient culture reads them as a different number, or fails to read them at all.
    ///
    /// A default container runs under the invariant culture and is unaffected. It takes a real
    /// culture reaching the process, which happens when LANG or LC_ALL is set, and on a desktop
    /// install that inherits the operating system's locale:
    ///
    ///   de-DE: '.' is the GROUP separator, so "43200.250" parsed as 43200250.
    ///   fr-FR: ',' is the decimal separator, so "43200.250" did not parse at all.
    /// </summary>
    [Trait("Name", "MachineFormatCultureParsingTests")]
    [Trait("Category", "Unit")]
    public class MachineFormatCultureParsingTests : BaseTests
    {
        // '' is the invariant culture: what a container with no LANG gets.
        // de-DE treats '.' as the group separator; fr-FR treats ',' as the decimal separator.
        // tr-TR is here for the case folding rather than the separator: 'i' uppercases to 'I'
        // with a dot (U+0130), so "GiB" under ToUpper() matches no binary unit arm.
        public static TheoryData<string> ServerCultures => new() { "", "en-US", "de-DE", "fr-FR", "tr-TR" };

        private static void InCulture(string culture, Action body)
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                body();
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        private static AudioMetadata MapFfprobeDuration(string duration)
        {
            using var document = JsonDocument.Parse(
                "{\"format\":{\"duration\":\"" + duration + "\"}}");
            return FfprobeMetadataMapper.Map(document.RootElement, "book.m4b");
        }

        // FfprobeMetadataMapper.cs:48-52. ffprobe emits format.duration as a string and always
        // uses '.'. Under de-DE a twelve hour book was recorded as 43,200,250 seconds.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void FfprobeDuration_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                Assert.Equal(TimeSpan.FromSeconds(43200.250), MapFfprobeDuration("43200.250").Duration);
                Assert.Equal(TimeSpan.FromSeconds(3600.5), MapFfprobeDuration("3600.5").Duration);
            });
        }

        // MyAnonamouseSizeParser.cs:49. The description carries a formatted size and no byte
        // count, so the size is whatever this parse makes of "1.5".
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void MyAnonamouseFormattedSize_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                var size = MyAnonamouseSizeParser.ExtractFromDescription(
                    "Total Size: 1.5 GB",
                    Mock.Of<ILogger>());

                Assert.Equal(1610612736L, size);
            });
        }

        // MyAnonamouseSizeParser.cs:36. Same parse on the branch that has a byte count, reached
        // when the byte count itself is not a usable long. Harder to hit than the branch above,
        // and pinned for the same reason.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void MyAnonamouseSizeBesideUnusableByteCount_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                var size = MyAnonamouseSizeParser.ExtractFromDescription(
                    "Total Size: 1.5 GB (99999999999999999999999 bytes)",
                    Mock.Of<ILogger>());

                Assert.Equal(1610612736L, size);
            });
        }

        // TorznabNewznabValueParser.cs:40. The regex pulls "1.5" out of "1.5 GB" and the bare
        // parse then decides whether the release is 1.5 GB, 15 GB, or zero.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void TorznabSize_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                Assert.Equal(1610612736L, TorznabNewznabValueParser.ParseSize("1.5 GB"));
                Assert.Equal(1610612736L, TorznabNewznabValueParser.ParseSize("1.5 GiB"));
            });
        }

        // SabnzbdResponseMapper.cs:201. SABnzbd reports the queue speed as a formatted string.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void SabnzbdSpeed_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                Assert.Equal(1.5 * 1024 * 1024, SabnzbdResponseMapper.ParseSpeed("1.5 M"));
            });
        }

        // SabnzbdResponseMapper.cs:320. GetDouble reads mb/mbleft/percentage out of a queue slot.
        // ParseJsonDouble at :224 already pins the same read; this is its unpinned twin.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void SabnzbdQueueSlotSize_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                using var document = JsonDocument.Parse(
                    """
                    {
                      "nzo_id": "SABnzbd_nzo_test",
                      "filename": "Some Release",
                      "status": "Downloading",
                      "cat": "audiobooks",
                      "mb": "1.5",
                      "mbleft": "0.5",
                      "percentage": "66.6"
                    }
                    """);

                var item = SabnzbdResponseMapper.MapQueueSlotToQueueItem(
                    new DownloadClientConfiguration { Name = "sab", Type = "sabnzbd" },
                    document.RootElement,
                    configuredCategory: string.Empty,
                    speed: 0);

                Assert.NotNull(item);
                Assert.Equal((long)(1.5 * 1024 * 1024), item!.Size);
                Assert.Equal((long)(1.0 * 1024 * 1024), item.Downloaded);
                Assert.Equal(66.6, item.Progress);
            });
        }

        // SabnzbdResponseMapper.cs:224, already pinned before this change. Asserted so it stays
        // pinned, and so the expected behaviour of the sites above is stated against a site that
        // was already right.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void SabnzbdJsonDouble_StaysPinnedUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                using var document = JsonDocument.Parse("\"1.5\"");
                Assert.Equal(1.5, SabnzbdResponseMapper.ParseJsonDouble(document.RootElement));
            });
        }

        // --------------------------------------------------------------------------
        // Case folding. Pinning the number parse and leaving the unit match to the
        // ambient culture fixes half of the same function.
        // --------------------------------------------------------------------------

        // TorznabNewznabValueParser.cs:46. Under tr-TR the 'i' of "GiB" uppercases to U+0130,
        // no binary arm of the switch matches, and the release is recorded as zero bytes:
        // a larger error than any of the six number parses this PR pins.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void TorznabBinaryUnits_MatchUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                Assert.Equal(1610612736L, TorznabNewznabValueParser.ParseSize("1.5 GiB"));
                Assert.Equal(1572864L, TorznabNewznabValueParser.ParseSize("1.5 MiB"));
                Assert.Equal(1536L, TorznabNewznabValueParser.ParseSize("1.5 KiB"));
                Assert.Equal(1649267441664L, TorznabNewznabValueParser.ParseSize("1.5 TiB"));

                // Not zero: the failure mode being closed is the unmatched switch arm.
                Assert.NotEqual(0L, TorznabNewznabValueParser.ParseSize("1.5 GiB"));
            });
        }

        // TorznabResponseParser.SizeParsing.cs:32, the other Torznab size parser. Its number
        // parse was already invariant on canary, so only the unit match was exposed; under
        // tr-TR "1.5 GiB" fell through the switch to the (long)value default, i.e. 1 byte.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void TorznabResponseParserBinaryUnits_MatchUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                using var httpClient = new HttpClient();
                var parser = new TorznabResponseParser(httpClient, Mock.Of<ILogger>());

                Assert.Equal(1610612736L, parser.ParseSizeString("1.5 GiB"));
                Assert.Equal(1572864L, parser.ParseSizeString("1.5 MiB"));

                // The decimal units were never at risk and are asserted as the control.
                Assert.Equal(1500000000L, parser.ParseSizeString("1.5 GB"));
            });
        }

        // --------------------------------------------------------------------------
        // NumberStyles.Float across the batch: a value that is not machine format must
        // fail the parse rather than come back as a different number.
        // --------------------------------------------------------------------------

        // SabnzbdResponseMapper.cs:204 and :325 were pinned with NumberStyles.Any, which
        // carries AllowThousands, AllowCurrencySymbol and AllowParentheses: under the
        // invariant culture "1,5" reads as 15 and "(1.5)" as -1.5. Float rejects both.
        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void SabnzbdSpeed_RejectsAValueThatIsNotMachineFormat(string culture)
        {
            InCulture(culture, () =>
            {
                Assert.Equal(0, SabnzbdResponseMapper.ParseSpeed("1,5 M"));
                Assert.Equal(0, SabnzbdResponseMapper.ParseSpeed("(1.5) M"));

                // Control: the machine-format value still parses.
                Assert.Equal(1.5 * 1024 * 1024, SabnzbdResponseMapper.ParseSpeed("1.5 M"));
            });
        }

        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void SabnzbdQueueSlotSize_RejectsAValueThatIsNotMachineFormat(string culture)
        {
            InCulture(culture, () =>
            {
                using var document = JsonDocument.Parse(
                    """
                    {
                      "nzo_id": "SABnzbd_nzo_test",
                      "filename": "Some Release",
                      "status": "Downloading",
                      "cat": "audiobooks",
                      "mb": "1,5",
                      "mbleft": "0.5",
                      "percentage": "66.6"
                    }
                    """);

                var item = SabnzbdResponseMapper.MapQueueSlotToQueueItem(
                    new DownloadClientConfiguration { Name = "sab", Type = "sabnzbd" },
                    document.RootElement,
                    configuredCategory: string.Empty,
                    speed: 0);

                Assert.NotNull(item);

                // Zero, not 15 MB. A size that cannot be read is better than one read as ten
                // times itself, which is what NumberStyles.Any returned here.
                Assert.Equal(0L, item!.Size);
            });
        }

        // --------------------------------------------------------------------------
        // MyAnonamousePublishDateParser.cs:77, :82 and :95. No damage was measured at
        // these three: the ages seen in practice are whole numbers, which read the same
        // under every culture. They are pinned for uniformity, and asserted so the
        // uniformity is a property of the build rather than of the reviewer's memory.
        // --------------------------------------------------------------------------

        private static DateTime? ParsePublishDate(string json)
        {
            using var document = JsonDocument.Parse(json);
            return MyAnonamousePublishDateParser.Parse(
                document.RootElement,
                "Some Release",
                Mock.Of<ILogger>());
        }

        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void MyAnonamouseAgeHours_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                var withFraction = ParsePublishDate("{\"ageHours\":\"2.5\"}");
                var whole = ParsePublishDate("{\"ageHours\":\"2\"}");

                Assert.NotNull(withFraction);
                Assert.NotNull(whole);

                // Half an hour apart, not twenty five hours or nothing at all.
                var gap = whole!.Value - withFraction!.Value;
                Assert.InRange(gap.TotalMinutes, 29, 31);
            });
        }

        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void MyAnonamouseAgeMinutes_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                var withFraction = ParsePublishDate("{\"ageMinutes\":\"90.5\"}");
                var whole = ParsePublishDate("{\"ageMinutes\":\"90\"}");

                Assert.NotNull(withFraction);
                Assert.NotNull(whole);

                var gap = whole!.Value - withFraction!.Value;
                Assert.InRange(gap.TotalSeconds, 29, 31);
            });
        }

        [Theory]
        [MemberData(nameof(ServerCultures))]
        public void MyAnonamouseAge_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                // 1.5 is below the 48 hour threshold, so it is read as hours.
                var withFraction = ParsePublishDate("{\"age\":\"1.5\"}");
                var whole = ParsePublishDate("{\"age\":\"1\"}");

                Assert.NotNull(withFraction);
                Assert.NotNull(whole);

                var gap = whole!.Value - withFraction!.Value;
                Assert.InRange(gap.TotalMinutes, 29, 31);
            });
        }
    }
}
