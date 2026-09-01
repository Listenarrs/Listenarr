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
using Listenarr.Infrastructure.DownloadClients.Nzbget;
using Listenarr.Tests.Common;
using Listenarr.Infrastructure.DownloadClients.Sabnzbd;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients
{
    /// <summary>
    /// The Priority dropdown and the planners have to agree on a vocabulary.
    ///
    /// They did not. The dropdown offered default, last and first; the planners switch on force,
    /// high, normal and low with a fallback of zero. The two sets did not intersect, so every
    /// choice but Default resolved to normal priority and the control looked operable while doing
    /// nothing. These tests pin the option values the form now offers.
    /// </summary>
    [Trait("Area", "DownloadClients")]
    [Trait("Name", "DownloadClientPriorityTests")]
    [Trait("Category", "Priority")]
    public class DownloadClientPriorityTests : BaseTests
    {
        /// <summary>The exact values rendered by the Priority select in DownloadClientFormModal.</summary>
        public static TheoryData<string, int> NzbgetPriorities => new()
        {
            { "low", -50 },
            { "normal", 0 },
            { "high", 50 },
            { "force", 100 },
        };

        private static DownloadClientConfiguration ClientWithPriority(string? priority)
        {
            var client = new DownloadClientConfiguration { Id = "client", Name = "Client" };
            client.Settings = priority == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { ["recentPriority"] = priority };
            return client;
        }

        [Theory]
        [MemberData(nameof(NzbgetPriorities))]
        [Trait("Scenario", "Every offered value reaches NZBGet as its own priority")]
        public void NzbgetResolvePriority_MapsEveryOfferedValue(string offered, int expected)
        {
            Assert.Equal(expected, NzbgetRequestPlanner.ResolvePriority(ClientWithPriority(offered)));
        }

        [Theory]
        [InlineData("first")]
        [InlineData("last")]
        [Trait("Scenario", "The values the form used to offer resolve to normal")]
        public void NzbgetResolvePriority_OldFormValues_FallThroughToNormal(string retired)
        {
            // The control, and the bug itself. These are what the dropdown sent before. They match
            // no arm of the switch and land on the default, which is why the setting never did
            // anything. Kept as a test so a future edit cannot quietly reintroduce the vocabulary.
            Assert.Equal(0, NzbgetRequestPlanner.ResolvePriority(ClientWithPriority(retired)));
        }

        [Fact]
        [Trait("Scenario", "Default leaves the priority to NZBGet")]
        public void NzbgetResolvePriority_Default_IsNormal()
        {
            Assert.Equal(0, NzbgetRequestPlanner.ResolvePriority(ClientWithPriority("default")));
            Assert.Equal(0, NzbgetRequestPlanner.ResolvePriority(ClientWithPriority(null)));
        }

        [Theory]
        [InlineData("low", "-1")]
        [InlineData("normal", "0")]
        [InlineData("high", "1")]
        [InlineData("force", "2")]
        [Trait("Scenario", "Every offered value reaches SABnzbd as its own priority")]
        public void SabnzbdFileQueryParams_MapsEveryOfferedValue(string offered, string expected)
        {
            var parameters = SabnzbdAddRequestPlanner.BuildFileQueryParams(ClientWithPriority(offered), "Title");

            Assert.True(parameters.ContainsKey("priority"));
            Assert.Equal(expected, parameters["priority"]);
        }

        [Fact]
        [Trait("Scenario", "Default omits the priority so the SABnzbd category decides")]
        public void SabnzbdFileQueryParams_Default_OmitsPriority()
        {
            // Sending 0 would override whatever the category carries, which is not what Default
            // asks for. BuildQueryParams already skipped it; this path did not.
            Assert.False(SabnzbdAddRequestPlanner.BuildFileQueryParams(ClientWithPriority("default"), "Title")
                .ContainsKey("priority"));
            Assert.False(SabnzbdAddRequestPlanner.BuildFileQueryParams(ClientWithPriority(null), "Title")
                .ContainsKey("priority"));
        }
    }
}
