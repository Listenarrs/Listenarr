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
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Notification
{
    /// <summary>
    /// Service for sending webhook notifications.
    /// Refactored to delegate payload construction and attachment handling to NotificationPayloadBuilder.
    /// Provides static compatibility shims used by tests.
    /// </summary>
    public class NotificationService : INotificationService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<NotificationService> _logger;
        private readonly IConfigurationService _configurationService;
        private readonly IRequestContextAccessor? _requestContextAccessor;
        private readonly INotificationPayloadBuilder _payloadBuilder;
        private readonly NotificationHttpSender _httpSender;

        public NotificationService(HttpClient httpClient, ILogger<NotificationService> logger, IConfigurationService configurationService, INotificationPayloadBuilder payloadBuilder, IRequestContextAccessor? requestContextAccessor = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configurationService = configurationService;
            _payloadBuilder = payloadBuilder ?? throw new ArgumentNullException(nameof(payloadBuilder));
            _requestContextAccessor = requestContextAccessor;
            _httpSender = new NotificationHttpSender(httpClient, httpClient, logger, AllowPrivateWebhookTargetsForCurrentRequest);
        }

        // INotificationService interface stubs — webhook dispatch goes through SendNotificationAsync;
        // these typed convenience methods delegate to the main webhook loop or no-op.
        public async Task OnDownloadImportedAsync(Download download)
        {
            try
            {
                var webhooks = await _configurationService.GetWebhookConfigurationsAsync();
                foreach (var wh in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Imported")))
                    await SendNotificationAsync("Imported", new { AudiobookTitle = download.Title, Timestamp = DateTime.UtcNow }, wh.Url, wh.Triggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SendDownloadCompletedNotificationAsync failed for {Id}", download.Id);
            }
        }

        public async Task OnDownloadFailedAsync(Download download)
        {
            try
            {
                var webhooks = await _configurationService.GetWebhookConfigurationsAsync();
                foreach (var wh in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Failed")))
                    await SendNotificationAsync("Failed", new { AudiobookTitle = download.Title, Error = download.ErrorMessage, Timestamp = DateTime.UtcNow }, wh.Url, wh.Triggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SendDownloadFailedNotificationAsync failed for {Id}", download.Id);
            }
        }

        public async Task SendSystemNotificationAsync(string title, string message)
        {
            try
            {
                var webhooks = await _configurationService.GetWebhookConfigurationsAsync();
                foreach (var wh in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("System")))
                    await SendNotificationAsync("System", new { Title = title, Message = message, Timestamp = DateTime.UtcNow }, wh.Url, wh.Triggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SendSystemNotificationAsync failed");
            }
        }

        // Compatibility shims removed — callers/tests should use NotificationPayloadBuilder directly.

        private bool AllowPrivateWebhookTargetsForCurrentRequest()
        {
            var context = _requestContextAccessor?.Current;
            if (context == null)
            {
                return true;
            }

            return context.RemoteIpAddress == null
                   || SecurityRequestUtils.IsLoopback(context.RemoteIpAddress)
                   || context.IsAuthenticatedAdminOrApiKey;
        }

        private async Task<HttpResponseMessage> PostValidatedAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
        {
            return await _httpSender.PostValidatedAsync(url, content, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendValidatedAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            return await _httpSender.SendValidatedAsync(request, cancellationToken);
        }

        /// <summary>
        /// Sends a notification to the webhook URL if configured.
        /// </summary>
        public async Task SendNotificationAsync(string trigger, object data, string webhookUrl, List<string> enabledTriggers)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl) || enabledTriggers == null || !enabledTriggers.Contains(trigger))
                return;
            var allowPrivateWebhookTargets = AllowPrivateWebhookTargetsForCurrentRequest();
            if (!NotificationDiagnostics.TryValidateWebhookTarget(webhookUrl, out var validationReason, allowPrivateWebhookTargets))
            {
                _logger.LogWarning("Blocked outbound notification target: {Reason}", validationReason);
                return;
            }

            // Discord-specific handling
            if (webhookUrl.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var payloadContext = await NotificationPayloadContextResolver.ResolveAsync(_configurationService, _requestContextAccessor, _logger, validateImageBaseUrl: true);

                    var (payloadObj, attachment) = await _payloadBuilder.CreateDiscordPayloadWithAttachmentAsync(
                        trigger, data, payloadContext.BaseUrl, _httpClient, _requestContextAccessor,
                        logInfo: msg => _logger.LogInformation(msg),
                        logDebug: (ex, msg) => _logger.LogDebug(ex, msg),
                        apiVersion: payloadContext.ApiVersion
                    );

                    _logger.LogDebug("Discord payload attachment present? {HasAttachment}", attachment != null);
                    if (attachment != null)
                    {
                        _logger.LogDebug("Attachment filename: {Filename}, size={Size}", attachment.Filename, attachment.ImageData?.Length ?? 0);
                        using var multipartContent = new MultipartFormDataContent();
                        var jsonContent = new System.Net.Http.StringContent(payloadObj.ToJsonString(), Encoding.UTF8, "application/json");
                        multipartContent.Add(jsonContent, "payload_json");

                        var imageContent = new ByteArrayContent(attachment.ImageData ?? Array.Empty<byte>());
                        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(attachment.ContentType);
                        multipartContent.Add(imageContent, "files[0]", attachment.Filename);

                        _logger.LogDebug("Posting multipart to {WebhookUrl} (attachment filename={Filename}, size={Size})", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()), attachment.Filename, attachment.ImageData?.Length ?? 0);
                        var response = await PostValidatedAsync(webhookUrl, multipartContent);
                        if (!response.IsSuccessStatusCode) await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                    }
                    else
                    {
                        var discordJson = payloadObj.ToJsonString();
                        using var discordContent = new System.Net.Http.StringContent(discordJson, Encoding.UTF8, "application/json");
                        var response = await PostValidatedAsync(webhookUrl, discordContent);
                        if (!response.IsSuccessStatusCode) await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP error sending Discord notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // Intentional broad catch: notification delivery failures must never propagate to callers.
                // OperationCanceledException is already handled above. All other failures are logged and swallowed.
#pragma warning disable CA1031
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error sending Discord notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                }
#pragma warning restore CA1031

            }

            // NTFY-specific handling (https://docs.ntfy.sh/publish/)
            if (webhookUrl.IndexOf("ntfy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    // Use the payload builder to create a concise message/title
                    var payloadContext = await NotificationPayloadContextResolver.ResolveAsync(_configurationService, _requestContextAccessor, _logger);
                    var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, payloadContext.BaseUrl, payloadContext.ApiVersion);
                    var title = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;
                    var message = title;

                    using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
                    {
                        Content = new StringContent(message ?? string.Empty, Encoding.UTF8, "text/plain")
                    };

                    // Helpful ntfy headers per docs: Title, Priority, Tags
                    if (!string.IsNullOrWhiteSpace(title)) request.Headers.TryAddWithoutValidation("Title", title);
                    request.Headers.TryAddWithoutValidation("Priority", "3");
                    request.Headers.TryAddWithoutValidation("Tags", trigger);

                    var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                    var headers = string.Join(", ", request.Headers.Select(h => $"{h.Key}={string.Join(';', h.Value)}"));
                    var requestBody = request.Content != null ? await request.Content.ReadAsStringAsync() : string.Empty;
                    var redactedRequestBody = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(requestBody, LogRedaction.GetSensitiveValuesFromEnvironment()));

                    _logger.LogInformation("Sending NTFY POST to {WebhookUrl} with headers {Headers} and body: {Body}", redactedUrl, headers, redactedRequestBody);

                    var response = await SendValidatedAsync(request);

                    if (!response.IsSuccessStatusCode)
                    {
                        var respText = await NotificationDiagnostics.TryReadContentAsync(response.Content, _logger);
                        var redactedResp = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        _logger.LogWarning("NTFY response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                        await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP error sending NTFY notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // OperationCanceledException is handled above (re-thrown). No TaskCanceledException handler here.
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "JSON error while building NTFY notification payload for {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "Invalid operation while sending NTFY notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                }

            }

            // Pushover (https://pushover.net/api)
            // Expect webhookUrl like: https://api.pushover.net/1/messages.json?token=<app_token>&user=<user_key>
            if (webhookUrl.IndexOf("api.pushover.net/1/messages.json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    var uri = new Uri(webhookUrl);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var token = query["token"];
                    var user = query["user"];

                    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(user))
                    {
                        _logger.LogWarning("Pushover webhook URL missing 'token' or 'user' query parameter: {WebhookUrl}", LogRedaction.SanitizeUrl(webhookUrl));
                        // Fall through to generic webhook behaviour below
                    }
                    else
                    {
                        var payloadContext = await NotificationPayloadContextResolver.ResolveAsync(_configurationService, _requestContextAccessor, _logger);
                        var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, payloadContext.BaseUrl, payloadContext.ApiVersion);
                        var message = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;

                        var values = new List<KeyValuePair<string, string>>
                        {
                            new("token", token),
                            new("user", user),
                            new("message", message ?? string.Empty),
                            new("title", "Listenarr")
                        };

                        using var content = new FormUrlEncodedContent(values);
                        var requestBody = await NotificationDiagnostics.TryReadContentAsync(content, _logger);
                        var redactedRequestBody = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(requestBody, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                        _logger.LogInformation("Sending Pushover POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedRequestBody);

                        // Post to the base path (without query) to comply with Pushover API expectations
                        var postUrl = uri.GetLeftPart(UriPartial.Path);
                        var response = await PostValidatedAsync(postUrl, content);
                        if (!response.IsSuccessStatusCode)
                        {
                            var respText = await NotificationDiagnostics.TryReadContentAsync(response.Content, _logger);
                            var redactedResp = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                            _logger.LogWarning("Pushover response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                            await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                        }
                        return;
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP error sending Pushover notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException
                                            && ex is not StackOverflowException
                                            && ex is not ThreadAbortException)
                {
                    _logger.LogError(ex, "Error sending Pushover notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
            }

            // Telegram (https://core.telegram.org/bots/api#sendmessage)
            // Expect webhookUrl like: https://api.telegram.org/bot<token>/sendMessage?chat_id=12345
            if (webhookUrl.IndexOf("api.telegram.org/bot", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    var uri = new Uri(webhookUrl);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var chatId = query["chat_id"];

                    if (string.IsNullOrWhiteSpace(chatId))
                    {
                        _logger.LogWarning("Telegram webhook URL missing 'chat_id' query parameter: {WebhookUrl}", LogRedaction.SanitizeUrl(webhookUrl));
                        // Fall through to generic webhook behaviour below
                    }
                    else
                    {
                        var payloadContext = await NotificationPayloadContextResolver.ResolveAsync(_configurationService, _requestContextAccessor, _logger);
                        var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, payloadContext.BaseUrl, payloadContext.ApiVersion);
                        var text = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;

                        var telegramBody = new { chat_id = chatId, text = text ?? string.Empty, disable_notification = true, parse_mode = "Markdown" };
                        var json = JsonSerializer.Serialize(telegramBody);
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");

                        var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                        _logger.LogInformation("Sending Telegram POST to {WebhookUrl} with body: {Body}", redactedUrl, NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(json, LogRedaction.GetSensitiveValuesFromEnvironment())));

                        var response = await PostValidatedAsync(webhookUrl, content);
                        if (!response.IsSuccessStatusCode)
                        {
                            var respText = await NotificationDiagnostics.TryReadContentAsync(response.Content, _logger);
                            var redactedResp = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                            _logger.LogWarning("Telegram response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                            await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                        }
                        return;
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP error sending Telegram notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // Intentional broad catch: notification delivery failures must never propagate to callers.
                // OperationCanceledException is already handled above. All other failures are logged and swallowed.
#pragma warning disable CA1031
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error sending Telegram notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
#pragma warning restore CA1031
            }

            // Pushbullet (https://docs.pushbullet.com/#pushes)
            // Expect webhookUrl like: https://api.pushbullet.com/v2/pushes?token=<access_token>
            if (webhookUrl.IndexOf("api.pushbullet.com/v2/pushes", StringComparison.OrdinalIgnoreCase) >= 0 || webhookUrl.StartsWith("pushbullet://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string? token = null;
                    Uri? uri = null;
                    try
                    {
                        uri = new Uri(webhookUrl);
                        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                        token = query["token"] ?? query["access_token"];
                    }
                    catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                    {
                        // support pushbullet://TOKEN format
                        if (webhookUrl.StartsWith("pushbullet://", StringComparison.OrdinalIgnoreCase))
                        {
                            token = webhookUrl.Substring("pushbullet://".Length);
                            uri = new Uri("https://api.pushbullet.com/v2/pushes");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(token))
                    {
                        _logger.LogWarning("Pushbullet webhook URL missing access token: {WebhookUrl}", LogRedaction.SanitizeUrl(webhookUrl));
                        // Fall through to generic webhook behaviour below
                    }
                    else
                    {
                        var payloadContext = await NotificationPayloadContextResolver.ResolveAsync(_configurationService, _requestContextAccessor, _logger);
                        var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, payloadContext.BaseUrl, payloadContext.ApiVersion);
                        var message = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;

                        var pushObj = new JsonObject
                        {
                            ["type"] = "note",
                            ["title"] = "Listenarr",
                            ["body"] = message ?? string.Empty
                        };

                        var json = pushObj.ToJsonString();
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");

                        var postUrl = uri != null ? uri.GetLeftPart(UriPartial.Path) : "https://api.pushbullet.com/v2/pushes";

                        using var request = new HttpRequestMessage(HttpMethod.Post, postUrl)
                        {
                            Content = content
                        };
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                        var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                        var redactedBody = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(json, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        _logger.LogInformation("Sending Pushbullet POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedBody);

                        var response = await SendValidatedAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            var respText = await NotificationDiagnostics.TryReadContentAsync(response.Content, _logger);
                            var redactedResp = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                            _logger.LogWarning("Pushbullet response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                            await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                        }
                        return;
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP error sending Pushbullet notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // Intentional broad catch: notification delivery failures must never propagate to callers.
                // OperationCanceledException is already handled above. All other failures are logged and swallowed.
#pragma warning disable CA1031
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error sending Pushbullet notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
#pragma warning restore CA1031
            }

            // Slack Incoming Webhooks (https://api.slack.com/messaging/webhooks)
            // Expect webhookUrl like: https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXXXXXX
            if (webhookUrl.IndexOf("hooks.slack.com/services", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    var payloadContext = await NotificationPayloadContextResolver.ResolveAsync(_configurationService, _requestContextAccessor, _logger);
                    var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, payloadContext.BaseUrl, payloadContext.ApiVersion);
                    var message = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;

                    var slackObj = new JsonObject
                    {
                        ["text"] = message ?? string.Empty
                    };

                    var json = slackObj.ToJsonString();
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                    var redactedBody = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(json, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    _logger.LogInformation("Sending Slack POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedBody);

                    var response = await PostValidatedAsync(webhookUrl, content);
                    if (!response.IsSuccessStatusCode)
                    {
                        var respText = await NotificationDiagnostics.TryReadContentAsync(response.Content, _logger);
                        var redactedResp = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        _logger.LogWarning("Slack response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                        await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                    }
                    return;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "HTTP error sending Slack notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // Intentional broad catch: notification delivery failures must never propagate to callers.
                // OperationCanceledException is already handled above. All other failures are logged and swallowed.
#pragma warning disable CA1031
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error sending Slack notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return;
                }
#pragma warning restore CA1031
            }

            // Generic webhook fallback: send the full JSON payload produced by the payload builder
            try
            {
                // Prefer rich payload created by the static helper (includes content, embeds, image links, etc.)
                var payloadContext = await NotificationPayloadContextResolver.ResolveAsync(_configurationService, _requestContextAccessor, _logger);
                var payloadObj = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, payloadContext.BaseUrl, payloadContext.ApiVersion);
                string defaultJson = payloadObj != null ? payloadObj.ToJsonString() : JsonSerializer.Serialize(new { @event = trigger, data = data, timestamp = DateTime.UtcNow }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

                using var defaultContent = new StringContent(defaultJson, Encoding.UTF8, "application/json");

                var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                var redactedBody = NotificationDiagnostics.AggressiveRedact(LogRedaction.RedactText(defaultJson, LogRedaction.GetSensitiveValuesFromEnvironment()));
                _logger.LogInformation("Sending Generic POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedBody);

                var response = await PostValidatedAsync(webhookUrl, defaultContent);
                if (!response.IsSuccessStatusCode)
                {
                    await NotificationDiagnostics.LogFailedResponseAsync(response, webhookUrl, _logger);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error sending Generic notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // Intentional broad catch: notification delivery failures must never propagate to callers.
            // OperationCanceledException is already handled above. All other failures are logged and swallowed.
#pragma warning disable CA1031
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error sending notification to {WebhookUrl}", LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment()));
            }
#pragma warning restore CA1031
        }

    }
}
