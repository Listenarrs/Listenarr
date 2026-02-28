using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class LibraryController_IdentifierDeduplicationTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public LibraryController_IdentifierDeduplicationTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task GetAudiobookIdentifiers_SuppressesImportedLegacyDuplicate_WhenManualAsinExistsWithRegion()
        {
            int audiobookId;

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

                var audiobook = new Audiobook
                {
                    Title = "Identifier Dedup Test",
                    Monitored = true,
                    // Legacy primary ASIN mirrors the manual identifier but loses region.
                    Asin = "B0DQR9D4YG",
                    ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                    {
                        new AudiobookExternalIdentifier
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = "B0DQR9D4YG",
                            ValueNormalized = "B0DQR9D4YG",
                            Region = "us",
                            IsPrimary = true,
                            Source = AudiobookExternalIdentifierSource.Manual
                        },
                        new AudiobookExternalIdentifier
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = "B0DQR9D4YG",
                            ValueNormalized = "B0DQR9D4YG",
                            Region = null,
                            IsPrimary = true,
                            Source = AudiobookExternalIdentifierSource.Imported
                        },
                        new AudiobookExternalIdentifier
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = "B0DQR5KHHF",
                            ValueNormalized = "B0DQR5KHHF",
                            Region = null,
                            IsPrimary = false,
                            Source = AudiobookExternalIdentifierSource.Imported
                        }
                    }
                };

                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                audiobookId = audiobook.Id;
            }

            var client = _factory.CreateClient();
            var response = await client.GetAsync($"/api/library/{audiobookId}/identifiers");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);

            var identifiers = json.RootElement.GetProperty("identifiers").EnumerateArray().ToList();
            var duplicateCandidates = identifiers
                .Where(i =>
                    JsonPropertyStringEquals(i, "type", "Asin") &&
                    JsonPropertyStringEquals(i, "valueNormalized", "B0DQR9D4YG"))
                .ToList();

            Assert.Single(duplicateCandidates);
            Assert.True(JsonPropertyStringEquals(duplicateCandidates[0], "source", "Manual"));
            Assert.True(JsonPropertyStringEquals(duplicateCandidates[0], "region", "us"));

            // Distinct imported alternate ASIN should still be present.
            Assert.Contains(identifiers, i =>
                JsonPropertyStringEquals(i, "type", "Asin") &&
                JsonPropertyStringEquals(i, "valueNormalized", "B0DQR5KHHF"));
        }

        [Fact]
        public async Task ReplaceAudiobookIdentifiers_ForcesManualSourceForNewRows_AndPreservesExistingImportedRows()
        {
            int audiobookId;

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

                var audiobook = new Audiobook
                {
                    Title = "Identifier Source Security Test",
                    Monitored = true,
                    Asin = "B0KEEP0001",
                    ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                    {
                        new AudiobookExternalIdentifier
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = "B0KEEP0001",
                            ValueNormalized = "B0KEEP0001",
                            Region = null,
                            IsPrimary = true,
                            Source = AudiobookExternalIdentifierSource.Imported
                        }
                    }
                };

                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                audiobookId = audiobook.Id;
            }

            var client = _factory.CreateClient();
            var csrfToken = await GetAntiforgeryTokenAsync(client);

            var payload = """
                {
                  "identifiers": [
                    { "type": "Asin", "value": "B0KEEP0001", "isPrimary": true, "source": "Imported" },
                    { "type": "Asin", "value": "B0SPOOF001", "region": "us", "isPrimary": false, "source": "Provider" }
                  ]
                }
                """;

            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/library/{audiobookId}/identifiers")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var identifiers = json.RootElement.GetProperty("identifiers").EnumerateArray().ToList();

            var preservedImported = identifiers.Single(i =>
                JsonPropertyStringEquals(i, "type", "Asin") &&
                JsonPropertyStringEquals(i, "valueNormalized", "B0KEEP0001"));
            Assert.True(JsonPropertyStringEquals(preservedImported, "source", "Imported"));

            var spoofedProvider = identifiers.Single(i =>
                JsonPropertyStringEquals(i, "type", "Asin") &&
                JsonPropertyStringEquals(i, "valueNormalized", "B0SPOOF001"));
            Assert.True(JsonPropertyStringEquals(spoofedProvider, "source", "Manual"));
        }

        private static bool JsonPropertyStringEquals(JsonElement element, string propertyName, string expected)
        {
            if (!element.TryGetProperty(propertyName, out var property)) return false;
            if (property.ValueKind == JsonValueKind.Null) return false;
            var actual = property.ToString();
            return string.Equals(actual, expected, System.StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
        {
            var tokenResponse = await client.GetAsync("/api/antiforgery/token");
            tokenResponse.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            return token!;
        }
    }
}
