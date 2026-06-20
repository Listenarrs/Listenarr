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

using System.Net.Http.Headers;
using System.Text.Json;

namespace Listenarr.Api.Features.Indexers
{
    public sealed class IndexerDebugSearchWorkflow(
        HttpClient httpClient,
        ILogger<IndexerDebugSearchWorkflow> logger)
    {
        public async Task<IndexerDebugSearchWorkflowResult> ExecuteMyAnonamouseAsync(
            Indexer indexer,
            int id,
            JsonElement body,
            HttpRequest requestContext,
            HttpContext httpContext)
        {
            try
            {
                var query = ExtractQuery(body);
                var mamId = ExtractMamId(indexer, id);

                if (string.IsNullOrEmpty(mamId))
                {
                    return IndexerDebugSearchWorkflowResult.BadRequest(new { success = false, message = "MAM ID missing in indexer settings" });
                }

                var testUrl = $"{indexer.Url.TrimEnd('/')}/tor/js/loadSearchJSONbasic.php";
                using var request = BuildMamSearchRequest(testUrl, query);
                using var client = BuildMamHttpClient(indexer, mamId);

                using var response = await client.SendAsync(request);
                var raw = await response.Content.ReadAsStringAsync();
                var parsed = await TryGetParsedResultsAsync(indexer, id, query, requestContext, httpContext);

                return IndexerDebugSearchWorkflowResult.Ok(new
                {
                    success = true,
                    status = (int)response.StatusCode,
                    raw,
                    parsedCount = parsed.Count,
                    parsed
                });
            }
            catch (HttpRequestException ex)
            {
                return BuildDebugFailure(id, ex);
            }
            catch (TaskCanceledException ex)
            {
                return BuildDebugFailure(id, ex);
            }
            catch (JsonException ex)
            {
                return BuildDebugFailure(id, ex);
            }
            catch (UriFormatException ex)
            {
                return BuildDebugFailure(id, ex);
            }
            catch (InvalidOperationException ex)
            {
                return BuildDebugFailure(id, ex);
            }
        }

        private static string ExtractQuery(JsonElement body)
        {
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("query", out var q))
            {
                return q.GetString() ?? "test";
            }

            return "test";
        }

        private string ExtractMamId(Indexer indexer, int id)
        {
            var mamId = string.Empty;
            if (string.IsNullOrEmpty(indexer.AdditionalSettings))
            {
                return mamId;
            }

            try
            {
                using var doc = JsonDocument.Parse(indexer.AdditionalSettings);
                if (doc.RootElement.TryGetProperty("mam_id", out var mamIdProperty))
                {
                    mamId = mamIdProperty.GetString() ?? string.Empty;
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Failed parsing AdditionalSettings JSON for indexer {Id} during debug search", id);
            }

            return mamId;
        }

        private static HttpRequestMessage BuildMamSearchRequest(string testUrl, string query)
        {
            var formData = new Dictionary<string, string>
            {
                ["tor[text]"] = query,
                ["tor[srchIn][]"] = "title",
                ["tor[searchType]"] = "all",
                ["tor[searchIn]"] = "torrents",
                ["tor[cat][]"] = "0",
                ["tor[browseFlagsHideVsShow]"] = "0",
                ["tor[startDate]"] = "",
                ["tor[endDate]"] = "",
                ["tor[hash]"] = "",
                ["tor[sortType]"] = "default",
                ["tor[startNumber]"] = "0",
                ["perpage"] = "100",
                ["thumbnail"] = "false",
                ["dlLink"] = "",
                ["description"] = ""
            };

            var request = new HttpRequestMessage(HttpMethod.Post, testUrl)
            {
                Content = new FormUrlEncodedContent(formData)
            };

            ApplyMamHeaders(request.Headers);
            request.Headers.Referrer = new Uri("https://www.myanonamouse.net/");
            return request;
        }

        private HttpClient BuildMamHttpClient(Indexer indexer, string mamId)
        {
            var cookieContainer = new System.Net.CookieContainer();
            var baseUrl = indexer.Url.TrimEnd('/');
            var baseUri = new Uri(baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? baseUrl : "https://" + baseUrl);
            cookieContainer.Add(baseUri, new System.Net.Cookie("mam_id", mamId));

            try
            {
                var host = baseUri.Host;
                if (!host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    var wwwUri = new Uri($"{baseUri.Scheme}://www.{host}");
                    cookieContainer.Add(wwwUri, new System.Net.Cookie("mam_id", mamId));
                }
            }
            catch (UriFormatException ex)
            {
                logger.LogDebug(ex, "Failed to add www host alias cookie for MyAnonamouse debug search request to {Host}", baseUri.Host);
            }
            catch (System.Net.CookieException ex)
            {
                logger.LogDebug(ex, "Failed to add www host alias cookie for MyAnonamouse debug search request to {Host}", baseUri.Host);
            }

            var handler = new HttpClientHandler { CookieContainer = cookieContainer, UseCookies = true };
            var client = new HttpClient(handler);
            ApplyMamHeaders(client.DefaultRequestHeaders);
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.myanonamouse.net/");
            return client;
        }

        private async Task<List<SearchResult>> TryGetParsedResultsAsync(
            Indexer indexer,
            int id,
            string query,
            HttpRequest requestContext,
            HttpContext httpContext)
        {
            try
            {
                var scheme = requestContext.Scheme;
                var hostVal = requestContext.Host.Value;
                var localSearchUrl = $"{scheme}://{hostVal}{HttpApiVersionUtils.BuildApiPath($"/search/{id}", httpContext)}?query={Uri.EscapeDataString(query)}";
                using var localResp = await httpClient.GetAsync(localSearchUrl);
                if (!localResp.IsSuccessStatusCode)
                {
                    return new List<SearchResult>();
                }

                var json = await localResp.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                return JsonSerializer.Deserialize<List<SearchResult>>(json, options) ?? new List<SearchResult>();
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
            }
            catch (TaskCanceledException ex)
            {
                logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
            }
            catch (UriFormatException ex)
            {
                logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
            }

            return new List<SearchResult>();
        }

        private IndexerDebugSearchWorkflowResult BuildDebugFailure(int id, Exception ex)
        {
            logger.LogWarning(ex, "MyAnonamouse debug search failed for indexer {Id}", id);
            return IndexerDebugSearchWorkflowResult.BadRequest(new { success = false, error = ex.Message });
        }

        private static void ApplyMamHeaders(HttpRequestHeaders headers)
        {
            headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        }
    }

    public sealed record IndexerDebugSearchWorkflowResult(int StatusCode, object Payload)
    {
        public static IndexerDebugSearchWorkflowResult Ok(object payload) => new(StatusCodes.Status200OK, payload);

        public static IndexerDebugSearchWorkflowResult BadRequest(object payload) => new(StatusCodes.Status400BadRequest, payload);
    }
}
