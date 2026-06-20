/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal sealed class TransmissionRpcClient
    {
        private static readonly JsonSerializerOptions s_rpcJsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _clientType;
        private readonly ILogger _logger;

        public TransmissionRpcClient(IHttpClientFactory httpClientFactory, string clientType, ILogger logger)
        {
            _httpClientFactory = httpClientFactory;
            _clientType = clientType;
            _logger = logger;
        }

        public async Task<JsonElement> InvokeAsync(DownloadClientConfiguration client, object payload, CancellationToken ct)
        {
            var httpClient = _httpClientFactory.CreateClient(_clientType);
            var baseUrl = BuildBaseUrl(client);
            var serializedPayload = JsonSerializer.Serialize(payload, s_rpcJsonOptions);
            string? sessionId = null;

            _logger.LogDebug("Transmission RPC request to {Url}: {Payload}", LogRedaction.SanitizeUrl(baseUrl), LogRedaction.SanitizeText(serializedPayload, 500));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
                {
                    Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrEmpty(sessionId))
                {
                    request.Headers.Add("X-Transmission-Session-Id", sessionId);
                    _logger.LogDebug("Using X-Transmission-Session-Id: {SessionId}", LogRedaction.SanitizeText(sessionId));
                }

                var authHeader = BuildAuthHeader(client);
                if (authHeader != null)
                {
                    request.Headers.Authorization = authHeader;
                }

                var response = await httpClient.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode == HttpStatusCode.Conflict && attempt == 0 && response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
                {
                    sessionId = values.FirstOrDefault();
                    _logger.LogDebug("Received 409 Conflict, retrying with session ID: {SessionId}", LogRedaction.SanitizeText(sessionId));
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var sensitiveValues = LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty });
                    var redacted = LogRedaction.RedactText(body, sensitiveValues);
                    _logger.LogWarning("Transmission returned {StatusCode}: {Body}", response.StatusCode, redacted);
                    throw new HttpRequestException($"Transmission returned {response.StatusCode}: {redacted}", null, response.StatusCode);
                }

                _logger.LogDebug("Transmission RPC response ({StatusCode}): {Body}", response.StatusCode, body);

                if (string.IsNullOrWhiteSpace(body))
                {
                    _logger.LogWarning("Transmission returned empty response body");
                    using var emptyDoc = JsonDocument.Parse("{}");
                    return emptyDoc.RootElement.Clone();
                }

                var trimmedBody = body.TrimStart();
                if (trimmedBody.Length > 0 && trimmedBody[0] != '{' && trimmedBody[0] != '[')
                {
                    var preview = trimmedBody.Length > 100 ? trimmedBody[..100] + "..." : trimmedBody;
                    _logger.LogWarning("Transmission RPC returned non-JSON response: {Preview}", LogRedaction.SanitizeText(preview));
                    throw new HttpRequestException("Transmission RPC endpoint returned a non-JSON response. Verify the host and port point to the Transmission RPC endpoint (default port 9091).");
                }

                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }

            throw new InvalidOperationException("Transmission did not supply a session identifier after retrying.");
        }

        private static string BuildBaseUrl(DownloadClientConfiguration client)
        {
            var rpcPath = "/transmission/rpc";
            if (client.Settings?.TryGetValue("urlBase", out var urlBaseObj) is true)
            {
                var custom = urlBaseObj?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(custom))
                {
                    rpcPath = custom.StartsWith('/') ? custom : "/" + custom;
                }
            }
            return DownloadClientUriBuilder.BuildUri(client, rpcPath).ToString();
        }

        private static AuthenticationHeaderValue? BuildAuthHeader(DownloadClientConfiguration client)
        {
            if (string.IsNullOrWhiteSpace(client.Username))
            {
                return null;
            }

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
            return new AuthenticationHeaderValue("Basic", credentials);
        }
    }
}
