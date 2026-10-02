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
using System.Text;
using System.Text.Json;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    /// <summary>
    /// The cutoff rule has to refuse the three bad shapes at the API, accept a good one, and
    /// leave reads alone. The last part matters more in Listenarr than it does in Readarr: for a
    /// DEFAULT profile, QualityProfileService.GetAllAsync, GetByIdAsync and GetDefaultAsync each
    /// write back through EnsureProfileHasRequiredQualitiesAsync
    /// (listenarr.application/Audiobooks/Quality/QualityProfileService.cs:41,53,66), so a rule in
    /// the service or repository write path would have turned a GET of an existing bad default
    /// profile into a failure. Non-default profiles take a plain read.
    ///
    /// Reading a stored bad profile works, and so does writing one back when the profile says
    /// upgrades are off, which is what every profile that used to record that with a blank cutoff
    /// now says after the UpgradeAllowed migration. The round-trip tests below carry both answers
    /// and each other's controls.
    /// </summary>
    [Trait("Name", "QualityProfileController_CutoffValidationTests")]
    [Trait("Category", "Api")]
    public sealed class QualityProfileController_CutoffValidationTests
        : BaseTests, IClassFixture<ListenarrWebApplicationFactory>
    {
        private const string CutoffMessage = "Cutoff must be an allowed quality or group";

        private readonly ListenarrWebApplicationFactory _factory;

        public QualityProfileController_CutoffValidationTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Create_WithACutoffNamingAnAllowedQuality_IsAccepted()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var response = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload("Cutoff control: accepted", cutoffQuality: "AAC 256kbps"));

            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Expected 201 for a valid cutoff, got {(int)response.StatusCode}: {body}");
            Assert.DoesNotContain(CutoffMessage, body, StringComparison.Ordinal);

            using var created = JsonDocument.Parse(body);
            Assert.Equal(
                "AAC 256kbps",
                created.RootElement.GetProperty("cutoffQuality").GetString());
        }

        [Fact]
        public async Task Create_WithABlankCutoff_IsRefused()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var nullCutoff = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload("Cutoff refused: null", cutoffQuality: null));
            using var emptyCutoff = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload("Cutoff refused: empty", cutoffQuality: string.Empty));

            await AssertRefusedForCutoffAsync(nullCutoff);
            await AssertRefusedForCutoffAsync(emptyCutoff);
        }

        [Fact]
        public async Task Create_WithACutoffTheProfileDoesNotCarry_IsRefused()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var response = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload("Cutoff refused: off the ladder", cutoffQuality: "OPUS 96kbps"));

            await AssertRefusedForCutoffAsync(response);
        }

        [Fact]
        public async Task Create_WithACutoffOnAPresentButDisallowedQuality_IsRefused()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var response = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload(
                    "Cutoff refused: disabled rung",
                    cutoffQuality: "MP3 128kbps",
                    disallow: "MP3 128kbps"));

            await AssertRefusedForCutoffAsync(response);
        }

        [Fact]
        public async Task Update_CannotMoveAGoodProfileOntoADisallowedCutoff()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var createResponse = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload("Cutoff refused on update", cutoffQuality: "AAC 256kbps"));
            var createdBody = await createResponse.Content.ReadAsStringAsync();
            Assert.True(
                createResponse.StatusCode == HttpStatusCode.Created,
                $"Setup expected 201, got {(int)createResponse.StatusCode}: {createdBody}");

            using var created = JsonDocument.Parse(createdBody);
            var id = created.RootElement.GetProperty("id").GetInt32();

            var payload = BuildProfilePayload(
                "Cutoff refused on update",
                cutoffQuality: "MP3 128kbps",
                disallow: "MP3 128kbps");
            payload["id"] = id;

            using var updateResponse = await SendProfileAsync(
                client,
                csrfToken,
                HttpMethod.Put,
                $"{ProfilesRoute}/{id}",
                payload);
            await AssertRefusedForCutoffAsync(updateResponse);

            // The stored profile is untouched by the refused update.
            using var reread = await client.GetAsync($"{ProfilesRoute}/{id}");
            using var rereadBody = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
            Assert.Equal(
                "AAC 256kbps",
                rereadBody.RootElement.GetProperty("cutoffQuality").GetString());
        }

        [Fact]
        public async Task AlreadyStoredProfileWithABlankCutoff_StillLoadsAndIsReturned()
        {
            // Written straight to the database, the way an install upgrading into this rule
            // already holds it. This is also the control for the refusals above: the same shape
            // the API now rejects is still perfectly storable underneath it.
            var id = await StoreProfileDirectlyAsync(
                "Stored blank cutoff",
                cutoffQuality: string.Empty,
                isDefault: true);

            using var client = _factory.CreateClient();

            using var byId = await client.GetAsync($"{ProfilesRoute}/{id}");
            var byIdBody = await byId.Content.ReadAsStringAsync();
            Assert.True(
                byId.StatusCode == HttpStatusCode.OK,
                $"Expected 200 reading a stored bad profile, got {(int)byId.StatusCode}: {byIdBody}");

            using var parsed = JsonDocument.Parse(byIdBody);
            Assert.Equal(string.Empty, parsed.RootElement.GetProperty("cutoffQuality").GetString());
            Assert.Equal("Stored blank cutoff", parsed.RootElement.GetProperty("name").GetString());

            // GetAllAsync and GetDefaultAsync both write back through
            // EnsureProfileHasRequiredQualitiesAsync for a default profile; neither may fail.
            using var all = await client.GetAsync(ProfilesRoute);
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            using var allBody = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
            Assert.Contains(
                allBody.RootElement.EnumerateArray(),
                profile => profile.GetProperty("id").GetInt32() == id);

            using var byDefault = await client.GetAsync($"{ProfilesRoute}/default");
            Assert.Equal(HttpStatusCode.OK, byDefault.StatusCode);
        }

        [Fact]
        public async Task AlreadyStoredProfileWithADisallowedCutoff_StillLoadsAndIsReturned()
        {
            var id = await StoreProfileDirectlyAsync(
                "Stored disallowed cutoff",
                cutoffQuality: "MP3 128kbps",
                isDefault: false,
                disallow: "MP3 128kbps");

            using var client = _factory.CreateClient();

            using var byId = await client.GetAsync($"{ProfilesRoute}/{id}");
            var byIdBody = await byId.Content.ReadAsStringAsync();
            Assert.True(
                byId.StatusCode == HttpStatusCode.OK,
                $"Expected 200 reading a stored bad profile, got {(int)byId.StatusCode}: {byIdBody}");

            using var parsed = JsonDocument.Parse(byIdBody);
            Assert.Equal("MP3 128kbps", parsed.RootElement.GetProperty("cutoffQuality").GetString());
        }

        /// <summary>
        /// The regression this branch exists for. Before UpgradeAllowed, a profile with upgrades
        /// off was stored with a blank cutoff, so GET then PUT of the same bytes came back 400 and
        /// there was no way out through the UI: the cutoff select is hidden behind the very
        /// checkbox that blanked it
        /// (fe/src/components/settings/QualityProfileFormModal.vue:215-219). The round trip is
        /// what the star button at fe/src/views/settings/QualityProfilesTab.vue:549-553 does and
        /// what the edit modal does on save.
        /// </summary>
        [Fact]
        public async Task StoredProfileWithUpgradesOff_RoundTripsUnchanged()
        {
            var id = await StoreProfileDirectlyAsync(
                "Upgrades off, round trip",
                cutoffQuality: string.Empty,
                isDefault: false);

            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var read = await client.GetAsync($"{ProfilesRoute}/{id}");
            var storedBytes = await read.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            using var stored = JsonDocument.Parse(storedBytes);
            Assert.False(stored.RootElement.GetProperty("upgradeAllowed").GetBoolean());

            using var writeBack = await SendRawAsync(
                client,
                csrfToken,
                HttpMethod.Put,
                $"{ProfilesRoute}/{id}",
                storedBytes);
            var writeBackBody = await writeBack.Content.ReadAsStringAsync();
            Assert.True(
                writeBack.StatusCode == HttpStatusCode.OK,
                $"A profile with upgrades off must round trip, got {(int)writeBack.StatusCode}: {writeBackBody}");

            // And it comes back the same, rather than merely being accepted. The repository copies
            // scalars by hand, so a missed assignment would show up right here.
            using var reread = await client.GetAsync($"{ProfilesRoute}/{id}");
            using var rereadBody = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());
            Assert.False(rereadBody.RootElement.GetProperty("upgradeAllowed").GetBoolean());
            Assert.Equal(
                string.Empty,
                rereadBody.RootElement.GetProperty("cutoffQuality").GetString());
        }

        /// <summary>
        /// The control for the round trip above. A stored profile that says upgrades are ON while
        /// naming no cutoff is still refused, so what excused the first one is the flag and not a
        /// weakening of the cutoff rule. That shape cannot be reached through the API or left
        /// behind by the migration, so it is written straight to the database here.
        /// </summary>
        [Fact]
        public async Task StoredProfileWithUpgradesOnAndNoCutoff_IsStillRefusedOnWriteBack()
        {
            var id = await StoreProfileDirectlyAsync(
                "Upgrades on, no cutoff, round trip",
                cutoffQuality: string.Empty,
                isDefault: false,
                upgradeAllowed: true);

            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var read = await client.GetAsync($"{ProfilesRoute}/{id}");
            var storedBytes = await read.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            using var writeBack = await SendRawAsync(
                client,
                csrfToken,
                HttpMethod.Put,
                $"{ProfilesRoute}/{id}",
                storedBytes);
            await AssertRefusedForCutoffAsync(writeBack);
        }

        /// <summary>
        /// The other half of the control: the same round trip on a profile with a good cutoff
        /// succeeds, so neither result above is about the round trip itself.
        /// </summary>
        [Fact]
        public async Task StoredProfileWithAGoodCutoff_RoundTripsUnchanged()
        {
            var id = await StoreProfileDirectlyAsync(
                "Good cutoff, round trip",
                cutoffQuality: "AAC 256kbps",
                isDefault: false);

            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var read = await client.GetAsync($"{ProfilesRoute}/{id}");
            var storedBytes = await read.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            using var writeBack = await SendRawAsync(
                client,
                csrfToken,
                HttpMethod.Put,
                $"{ProfilesRoute}/{id}",
                storedBytes);
            var body = await writeBack.Content.ReadAsStringAsync();
            Assert.True(
                writeBack.StatusCode == HttpStatusCode.OK,
                $"Control round trip should succeed, got {(int)writeBack.StatusCode}: {body}");
        }

        [Fact]
        public async Task Create_WithUpgradesOff_AndNoCutoff_IsAccepted()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var response = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload(
                    "Upgrades off, no cutoff",
                    cutoffQuality: null,
                    upgradeAllowed: false));

            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Expected 201 with upgrades off, got {(int)response.StatusCode}: {body}");

            using var created = JsonDocument.Parse(body);
            Assert.False(created.RootElement.GetProperty("upgradeAllowed").GetBoolean());
        }

        /// <summary>
        /// Turning upgrades off no longer throws the cutoff away, which is the whole reason the
        /// modal's checkbox was destructive. The saved profile keeps what it was given.
        /// </summary>
        [Fact]
        public async Task Create_WithUpgradesOff_KeepsTheCutoffItWasGiven()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var response = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload(
                    "Upgrades off, cutoff kept",
                    cutoffQuality: "AAC 256kbps",
                    upgradeAllowed: false));

            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Expected 201, got {(int)response.StatusCode}: {body}");

            using var created = JsonDocument.Parse(body);
            var id = created.RootElement.GetProperty("id").GetInt32();

            using var reread = await client.GetAsync($"{ProfilesRoute}/{id}");
            using var stored = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());
            Assert.False(stored.RootElement.GetProperty("upgradeAllowed").GetBoolean());
            Assert.Equal(
                "AAC 256kbps",
                stored.RootElement.GetProperty("cutoffQuality").GetString());
        }

        /// <summary>
        /// QualityProfileRepository.UpdateAsync copies scalars onto the tracked entity one by one,
        /// so a field it forgets is silently dropped on every PUT while the response still looks
        /// right. This turns the flag off through the API and reads it back from a fresh request.
        /// </summary>
        [Fact]
        public async Task Update_TurningUpgradesOff_IsPersisted()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var createResponse = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload("Upgrades toggled off", cutoffQuality: "AAC 256kbps"));
            var createdBody = await createResponse.Content.ReadAsStringAsync();
            Assert.True(
                createResponse.StatusCode == HttpStatusCode.Created,
                $"Setup expected 201, got {(int)createResponse.StatusCode}: {createdBody}");

            using var created = JsonDocument.Parse(createdBody);
            var id = created.RootElement.GetProperty("id").GetInt32();
            Assert.True(created.RootElement.GetProperty("upgradeAllowed").GetBoolean());

            var payload = BuildProfilePayload(
                "Upgrades toggled off",
                cutoffQuality: "AAC 256kbps",
                upgradeAllowed: false);
            payload["id"] = id;

            using var updateResponse = await SendProfileAsync(
                client,
                csrfToken,
                HttpMethod.Put,
                $"{ProfilesRoute}/{id}",
                payload);
            var updateBody = await updateResponse.Content.ReadAsStringAsync();
            Assert.True(
                updateResponse.StatusCode == HttpStatusCode.OK,
                $"Expected 200, got {(int)updateResponse.StatusCode}: {updateBody}");

            using var reread = await client.GetAsync($"{ProfilesRoute}/{id}");
            using var stored = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());
            Assert.False(stored.RootElement.GetProperty("upgradeAllowed").GetBoolean());
            Assert.Equal(
                "AAC 256kbps",
                stored.RootElement.GetProperty("cutoffQuality").GetString());
        }

        /// <summary>
        /// A client that never heard of the flag still gets what it always got. Omitting the field
        /// leaves upgrades on, so an old integration posting a profile with a valid cutoff keeps
        /// working and does not quietly acquire a profile that has stopped upgrading.
        /// </summary>
        [Fact]
        public async Task Create_WithoutTheFlag_LeavesUpgradesOn()
        {
            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var response = await PostProfileAsync(
                client,
                csrfToken,
                BuildProfilePayload("No flag sent", cutoffQuality: "AAC 256kbps"));

            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Expected 201, got {(int)response.StatusCode}: {body}");

            using var created = JsonDocument.Parse(body);
            Assert.True(created.RootElement.GetProperty("upgradeAllowed").GetBoolean());
        }

        /// <summary>
        /// PUT on this controller is a whole-document replace: the body binds straight onto the
        /// domain entity (listenarr.api/Features/Library/QualityProfileController.cs:132), so a
        /// field the client leaves out comes back as that property's initialiser rather than as
        /// what was stored. UpgradeAllowed inherits that, and for this field the direction is the
        /// unpleasant one, because the initialiser is true and the value being discarded is a
        /// user's decision to stop upgrading.
        ///
        /// The control is MinimumSeeders, which has behaved exactly this way since long before
        /// this branch: stored as 5, omitted from the PUT, back to its initialiser of 1. So this
        /// is the contract of the endpoint and not something the flag introduced. The family does
        /// not solve it either: Readarr and Sonarr do separate a resource type from the model, but
        /// both declare a non-nullable <c>public bool UpgradeAllowed</c> on it
        /// (src/Readarr.Api.V1/Profiles/Quality/QualityProfileResource.cs:13,
        /// src/Sonarr.Api.V3/Profiles/Quality/QualityProfileResource.cs:13), so an omitted field
        /// lands on false there for the same reason it lands on true here. The real fix is
        /// nullable fields on an inbound resource, which touches every field at once.
        ///
        /// The UI sends back the whole object it read, on save
        /// (fe/src/views/settings/QualityProfilesTab.vue:535) and from the set-default button
        /// (:563), so nothing in the application hits this.
        /// </summary>
        [Fact]
        public async Task Update_OmittingAField_ResetsItToItsDefault_ForTheFlagAndForItsNeighbour()
        {
            var id = await StoreProfileDirectlyAsync(
                "Partial update",
                cutoffQuality: "AAC 256kbps",
                isDefault: false,
                upgradeAllowed: false);

            using (var seedScope = _factory.Services.CreateScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var seeded = db.QualityProfiles.Single(profile => profile.Id == id);
                seeded.MinimumSeeders = 5;
                await db.SaveChangesAsync();
            }

            using var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            using var read = await client.GetAsync($"{ProfilesRoute}/{id}");
            var storedBytes = await read.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(storedBytes)!;
            Assert.False(body["upgradeAllowed"].GetBoolean());
            Assert.Equal(5, body["minimumSeeders"].GetInt32());
            body.Remove("upgradeAllowed");
            body.Remove("minimumSeeders");

            using var writeBack = await SendRawAsync(
                client,
                csrfToken,
                HttpMethod.Put,
                $"{ProfilesRoute}/{id}",
                JsonSerializer.Serialize(body));
            var writeBackBody = await writeBack.Content.ReadAsStringAsync();
            Assert.True(
                writeBack.StatusCode == HttpStatusCode.OK,
                $"Expected 200, got {(int)writeBack.StatusCode}: {writeBackBody}");

            using var reread = await client.GetAsync($"{ProfilesRoute}/{id}");
            using var stored = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());

            Assert.True(stored.RootElement.GetProperty("upgradeAllowed").GetBoolean());
            Assert.Equal(1, stored.RootElement.GetProperty("minimumSeeders").GetInt32());
        }

        private string ProfilesRoute => $"{TestUtils.ResolveApiBasePath(_factory.Services)}/qualityprofile";

        private static async Task<HttpResponseMessage> SendRawAsync(
            HttpClient client,
            string csrfToken,
            HttpMethod method,
            string route,
            string json)
        {
            using var request = new HttpRequestMessage(method, route)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        private Task<HttpResponseMessage> PostProfileAsync(
            HttpClient client,
            string csrfToken,
            Dictionary<string, object?> payload) =>
            SendProfileAsync(client, csrfToken, HttpMethod.Post, ProfilesRoute, payload);

        private static async Task<HttpResponseMessage> SendProfileAsync(
            HttpClient client,
            string csrfToken,
            HttpMethod method,
            string route,
            Dictionary<string, object?> payload)
        {
            using var request = new HttpRequestMessage(method, route)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        private string AntiforgeryRoute =>
            $"{TestUtils.ResolveApiBasePath(_factory.Services)}/antiforgery/token";

        private async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
        {
            using var response = await client.GetAsync(AntiforgeryRoute);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            return token!;
        }

        /// <summary>
        /// Writes a profile the way the database holds one after the UpgradeAllowed migration. The
        /// flag defaults to what that migration derives from the cutoff, so a caller that does not
        /// name it gets the row an upgraded install would actually have.
        /// </summary>
        private async Task<int> StoreProfileDirectlyAsync(
            string name,
            string? cutoffQuality,
            bool isDefault,
            string? disallow = null,
            bool? upgradeAllowed = null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

            var profile = new QualityProfile
            {
                Name = name,
                CutoffQuality = cutoffQuality,
                UpgradeAllowed = upgradeAllowed ?? !string.IsNullOrWhiteSpace(cutoffQuality),
                IsDefault = isDefault,
                Qualities = BuildLadder(disallow)
            };

            db.QualityProfiles.Add(profile);
            await db.SaveChangesAsync();
            return profile.Id;
        }

        private static List<QualityDefinition> BuildLadder(string? disallow)
        {
            var ladder = new (string Quality, string Codec, int? Bitrate)[]
            {
                ("AAC 320kbps", "AAC", 320),
                ("AAC 256kbps", "AAC", 256),
                ("MP3 320kbps", "MP3", 320),
                ("MP3 128kbps", "MP3", 128)
            };

            return ladder
                .Select((rung, index) => new QualityDefinition
                {
                    Quality = rung.Quality,
                    Codec = rung.Codec,
                    Bitrate = rung.Bitrate,
                    Priority = index,
                    Allowed = !string.Equals(rung.Quality, disallow, StringComparison.Ordinal)
                })
                .ToList();
        }

        private static Dictionary<string, object?> BuildProfilePayload(
            string name,
            string? cutoffQuality,
            string? disallow = null,
            bool? upgradeAllowed = null)
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["cutoffQuality"] = cutoffQuality,
                ["isDefault"] = false,
                ["qualities"] = BuildLadder(disallow)
                    .Select(quality => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["quality"] = quality.Quality,
                        ["allowed"] = quality.Allowed,
                        ["priority"] = quality.Priority,
                        ["codec"] = quality.Codec,
                        ["bitrate"] = quality.Bitrate,
                        ["isLossless"] = quality.IsLossless
                    })
                    .ToList()
            };

            // Left out entirely when not named, so the omitted-field default is what gets
            // exercised rather than an explicit true.
            if (upgradeAllowed.HasValue)
            {
                payload["upgradeAllowed"] = upgradeAllowed.Value;
            }

            return payload;
        }

        private static async Task AssertRefusedForCutoffAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"Expected 400 for a bad cutoff, got {(int)response.StatusCode}: {body}");
            Assert.Contains(CutoffMessage, body, StringComparison.Ordinal);

            using var problem = JsonDocument.Parse(body);
            var errors = problem.RootElement.GetProperty("errors");
            var cutoffErrors = errors
                .EnumerateObject()
                .Where(property => property.Name.EndsWith(
                    "cutoffQuality",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            var cutoffError = Assert.Single(cutoffErrors);
            Assert.Contains(
                cutoffError.Value.EnumerateArray(),
                message => string.Equals(message.GetString(), CutoffMessage, StringComparison.Ordinal));
        }
    }
}
