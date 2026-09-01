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
using Listenarr.Infrastructure.DownloadClients.Qbittorrent;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent
{
    /// <summary>
    /// The Advanced Settings section saved four values that never reached the client, because the
    /// plan record carried no field for any of them.
    ///
    /// The spellings asserted here were checked against qBittorrent 5.2.3, Web API 2.15.1, by
    /// adding a torrent with each parameter and reading the state back. Two of them cannot be
    /// taken from the documentation alone: "paused" is ignored from Web API 2.11 onward in favour
    /// of "stopped", and "contentLayout" is case sensitive.
    /// </summary>
    [Trait("Area", "DownloadClients")]
    [Trait("Name", "QbittorrentAdvancedSettingsTests")]
    [Trait("Category", "Qbittorrent")]
    public class QbittorrentAdvancedSettingsTests : BaseTests
    {
        private static PreparedTorrentSubmission Submission() => new(
            Title: "Book Title",
            Artist: "Author",
            Album: "Album",
            Source: "Indexer",
            Quality: null,
            Language: null,
            Size: 1024,
            OriginalLocator: "magnet:?xt=urn:btih:hash-1",
            InfoHash: "hash-1",
            TorrentBytes: null,
            MagnetUri: "magnet:?xt=urn:btih:hash-1",
            FileName: null,
            TrackerUrls: []);

        private static DownloadClientConfiguration Client(params (string Key, object Value)[] settings)
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qbit",
                Name = "qBittorrent",
                DownloadPath = "/downloads"
            };
            client.Settings = settings.ToDictionary(entry => entry.Key, entry => entry.Value);
            return client;
        }

        private static async Task<string> AddBodyAsync(DownloadClientConfiguration client)
        {
            var plan = QbittorrentTorrentAddPlanner.Create(client, Submission());
            using var content = QbittorrentAddRequestContentBuilder.Build(plan);
            return await content.ReadAsStringAsync();
        }

        [Fact]
        [Trait("Scenario", "Nothing is sent for a client with no advanced settings")]
        public async Task Build_WithoutAdvancedSettings_SendsNoneOfThem()
        {
            // The control. Default has to mean "leave the client alone", so these tests would be
            // worthless if the parameters were always present.
            var body = await AddBodyAsync(Client());

            Assert.DoesNotContain("stopped", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sequentialDownload", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("firstLastPiecePrio", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contentLayout", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Scenario", "Pause sends both the old and the current parameter name")]
        public async Task Build_InitialStatePause_SendsStoppedAndPaused()
        {
            var body = await AddBodyAsync(Client(("initialState", "pause")));

            // "stopped" is what 5.x reads. "paused" is what 4.x reads and 5.x ignores. Sending
            // both pauses on either without asking the client its version first.
            Assert.Contains("stopped=true", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("paused=true", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Scenario", "Start does not pause")]
        public async Task Build_InitialStateStart_DoesNotPause()
        {
            var body = await AddBodyAsync(Client(("initialState", "start")));

            Assert.DoesNotContain("stopped", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("paused", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Scenario", "Force start is not attempted on the add call")]
        public async Task Build_InitialStateForceStart_IsNotSentOnAdd()
        {
            // qBittorrent accepts forceStart on the add call and ignores it. The workflow makes a
            // setForceStart call afterwards instead, so putting it here would only look right.
            var plan = QbittorrentTorrentAddPlanner.Create(Client(("initialState", "forceStart")), Submission());
            using var content = QbittorrentAddRequestContentBuilder.Build(plan);
            var body = await content.ReadAsStringAsync();

            Assert.True(plan.ForceStart);
            Assert.DoesNotContain("forceStart", body, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("original", "Original")]
        [InlineData("subfolder", "Subfolder")]
        [InlineData("nosubfolder", "NoSubfolder")]
        [Trait("Scenario", "Content layout is sent in the casing qBittorrent accepts")]
        public async Task Build_ContentLayout_IsSentCapitalised(string stored, string expected)
        {
            var body = await AddBodyAsync(Client(("contentLayout", stored)));

            Assert.Contains($"contentLayout={expected}", Uri.UnescapeDataString(body), StringComparison.Ordinal);
            // The stored lowercase spelling is silently ignored by the client, so it must not
            // be what goes on the wire.
            Assert.DoesNotContain($"contentLayout={stored}", Uri.UnescapeDataString(body), StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Scenario", "A content layout of default is left to the client")]
        public async Task Build_ContentLayoutDefault_IsOmitted()
        {
            var body = await AddBodyAsync(Client(("contentLayout", "default")));

            Assert.DoesNotContain("contentLayout", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Scenario", "The two piece-priority toggles reach the client")]
        public async Task Build_PieceOptions_AreSentWhenEnabled()
        {
            var body = await AddBodyAsync(Client(
                ("sequentialOrder", true),
                ("firstAndLastFirst", true)));

            Assert.Contains("sequentialDownload=true", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("firstLastPiecePrio=true", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Scenario", "The toggles are omitted when off")]
        public async Task Build_PieceOptions_AreOmittedWhenDisabled()
        {
            var body = await AddBodyAsync(Client(
                ("sequentialOrder", false),
                ("firstAndLastFirst", false)));

            Assert.DoesNotContain("sequentialDownload", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("firstLastPiecePrio", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
