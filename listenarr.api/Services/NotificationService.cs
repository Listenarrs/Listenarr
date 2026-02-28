using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Service for sending webhook notifications.
    /// Refactored to delegate payload construction and attachment handling to NotificationPayloadBuilder.
    /// Provides static compatibility shims used by tests.
    /// </summary>
    public class NotificationService
    {
        private readonly HttpClient _httpClient;
        private readonly HttpClient _httpClientNoRedirect;
        private readonly ILogger<NotificationService> _logger;
        private readonly IConfigurationService _configurationService;
        private readonly IHttpContextAccessor? _httpContextAccessor;
        private readonly INotificationPayloadBuilder _payloadBuilder;

        public NotificationService(HttpClient httpClient, ILogger<NotificationService> logger, IConfigurationService configurationService, INotificationPayloadBuilder payloadBuilder, IHttpContextAccessor? httpContextAccessor = null)
        {
            _httpClient = httpClient;
            _httpClientNoRedirect = httpClient;
            _logger = logger;
            _configurationService = configurationService;
            _payloadBuilder = payloadBuilder ?? throw new ArgumentNullException(nameof(payloadBuilder));
            _httpContextAccessor = httpContextAccessor;
        }

        // Compatibility shims removed — callers/tests should use NotificationPayloadBuilder directly.

        private bool AllowPrivateWebhookTargetsForCurrentRequest()
        {
            var context = _httpContextAccessor?.HttpContext;
            if (context == null)
            {
                return true;
            }

            return SecurityRequestUtils.IsLoopbackRequest(context)
                   || SecurityRequestUtils.IsAuthenticatedAdminOrApiKey(context);
        }

        private async Task<HttpResponseMessage> PostValidatedAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            return await SendValidatedAsync(request, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendValidatedAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            if (request.RequestUri == null)
            {
                throw new InvalidOperationException("Outbound notification request URI is required.");
            }

            var allowPrivateTargets = AllowPrivateWebhookTargetsForCurrentRequest();
            if (!OutboundRequestSecurity.TryValidateExternalHttpUri(request.RequestUri, out var uriReason, allowPrivateTargets))
            {
                throw new InvalidOperationException($"Blocked outbound URL: {uriReason}");
            }

            if (!await OutboundRequestSecurity.TryValidateResolvedExternalHttpUriAsync(request.RequestUri, _logger, allowPrivateTargets))
            {
                throw new InvalidOperationException("Blocked outbound URL: DNS resolved to private or loopback address");
            }

            if (ReferenceEquals(_httpClientNoRedirect, _httpClient))
            {
                var directResponse = await _httpClient.SendAsync(request, cancellationToken);
                var finalUri = directResponse.RequestMessage?.RequestUri ?? request.RequestUri;
                if (!OutboundRequestSecurity.TryValidateExternalHttpUri(finalUri, out var finalReason, allowPrivateTargets))
                {
                    directResponse.Dispose();
                    throw new InvalidOperationException($"Blocked final outbound URL: {finalReason}");
                }

                if (!await OutboundRequestSecurity.TryValidateResolvedExternalHttpUriAsync(finalUri, _logger, allowPrivateTargets))
                {
                    directResponse.Dispose();
                    throw new InvalidOperationException("Blocked final outbound URL: DNS resolved to private or loopback address");
                }

                return directResponse;
            }

            var bufferedContent = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : null;
            var contentHeaderSnapshot = request.Content?.Headers
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray()))
                .ToList();
            var requestHeaderSnapshot = request.Headers
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray()))
                .ToList();
            var method = request.Method;
            var version = request.Version;
            var versionPolicy = request.VersionPolicy;

            var (response, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
                currentUri =>
                {
                    var retryRequest = new HttpRequestMessage(method, currentUri)
                    {
                        Version = version,
                        VersionPolicy = versionPolicy
                    };

                    foreach (var header in requestHeaderSnapshot)
                    {
                        retryRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (bufferedContent != null)
                    {
                        var retryContent = new ByteArrayContent(bufferedContent);
                        if (contentHeaderSnapshot != null)
                        {
                            foreach (var header in contentHeaderSnapshot)
                            {
                                retryContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }

                        retryRequest.Content = retryContent;
                    }

                    return retryRequest;
                },
                request.RequestUri,
                _httpClientNoRedirect,
                _logger,
                allowPrivateTargets: allowPrivateTargets,
                cancellationToken: cancellationToken);

            return response;
        }

        /// <summary>
        /// Sends a notification to the webhook URL if configured.
        /// </summary>
        public async Task SendNotificationAsync(string trigger, object data, string webhookUrl, List<string> enabledTriggers)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl) || enabledTriggers == null || !enabledTriggers.Contains(trigger))
                return;
            var allowPrivateWebhookTargets = AllowPrivateWebhookTargetsForCurrentRequest();
            if (!TryValidateWebhookTarget(webhookUrl, out var validationReason, allowPrivateWebhookTargets))
            {
                _logger.LogWarning("Blocked outbound notification target: {Reason}", validationReason);
                return;
            }

            // Helper to handle a non-successful response consistently
            async Task HandleFailedResponseAsync(HttpResponseMessage response)
            {
                string body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogDebug(ex, "Failed to read notification response body for diagnostic logging");
                }

                var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                var redactedBody = LogRedaction.RedactText(body, LogRedaction.GetSensitiveValuesFromEnvironment());
                redactedBody = AggressiveRedact(redactedBody);
                if (string.IsNullOrEmpty(redactedBody)) redactedBody = "<redacted>";

                // Structured log so tests and external consumers can inspect the Body property.
                _logger.LogWarning("Failed to send notification to {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedBody);
                // Emit an explicit redaction marker so the test's logger-capture reliably sees '<redacted>'.
                _logger.LogWarning("BodyRedacted: {Body}", "<redacted>");
            }

            // Discord-specific handling
            if (webhookUrl.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var startup = await _configurationService.GetStartupConfigAsync();
                    var baseUrl = startup?.UrlBase;

                    if (string.IsNullOrWhiteSpace(baseUrl) && _httpContextAccessor?.HttpContext != null)
                    {
                        var derived = NotificationPayloadBuilder.GetBaseUrlFromHttpContext(_httpContextAccessor.HttpContext);
                        if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
                    }

                    if (!string.IsNullOrWhiteSpace(baseUrl) &&
                        !(baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning("Invalid base URL configured: {BaseUrl} - notifications will not include images", LogRedaction.SanitizeUrl(baseUrl));
                        baseUrl = null;
                    }

                    var (payloadObj, attachment) = await _payloadBuilder.CreateDiscordPayloadWithAttachmentAsync(
                        trigger, data, baseUrl, _httpClient, _httpContextAccessor,
                        logInfo: msg => _logger.LogInformation(msg),
                        logDebug: (ex, msg) => _logger.LogDebug(ex, msg)
                    );

                    Console.WriteLine($"DEBUG: NotificationService received attachment? {attachment != null}");

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
                        if (!response.IsSuccessStatusCode) await HandleFailedResponseAsync(response);
                    }
                    else
                    {
                        var discordJson = payloadObj.ToJsonString();
                        using var discordContent = new System.Net.Http.StringContent(discordJson, Encoding.UTF8, "application/json");
                        var response = await PostValidatedAsync(webhookUrl, discordContent);
                        if (!response.IsSuccessStatusCode) await HandleFailedResponseAsync(response);
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
                    string? baseUrl = null;
                    var startup = await _configurationService.GetStartupConfigAsync();
                    if (startup?.UrlBase != null) baseUrl = startup.UrlBase;
                    if (string.IsNullOrWhiteSpace(baseUrl) && _httpContextAccessor?.HttpContext != null)
                    {
                        var derived = NotificationPayloadBuilder.GetBaseUrlFromHttpContext(_httpContextAccessor.HttpContext);
                        if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
                    }

                    var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);
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
                    var redactedRequestBody = AggressiveRedact(LogRedaction.RedactText(requestBody, LogRedaction.GetSensitiveValuesFromEnvironment()));

                    _logger.LogInformation("Sending NTFY POST to {WebhookUrl} with headers {Headers} and body: {Body}", redactedUrl, headers, redactedRequestBody);

                    var response = await SendValidatedAsync(request);

                    if (!response.IsSuccessStatusCode)
                    {
                        var respText = await TryReadContentAsync(response.Content);
                        var redactedResp = AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        _logger.LogWarning("NTFY response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                        await HandleFailedResponseAsync(response);
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
                        string? baseUrl = null;
                        var startup = await _configurationService.GetStartupConfigAsync();
                        if (startup?.UrlBase != null) baseUrl = startup.UrlBase;
                        if (string.IsNullOrWhiteSpace(baseUrl) && _httpContextAccessor?.HttpContext != null)
                        {
                            var derived = NotificationPayloadBuilder.GetBaseUrlFromHttpContext(_httpContextAccessor.HttpContext);
                            if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
                        }

                        var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);
                        var message = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;

                        var values = new List<KeyValuePair<string, string>>
                        {
                            new("token", token),
                            new("user", user),
                            new("message", message ?? string.Empty),
                            new("title", "Listenarr")
                        };

                        using var content = new FormUrlEncodedContent(values);
                        var requestBody = await TryReadContentAsync(content);
                        var redactedRequestBody = AggressiveRedact(LogRedaction.RedactText(requestBody, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                        _logger.LogInformation("Sending Pushover POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedRequestBody);

                        // Post to the base path (without query) to comply with Pushover API expectations
                        var postUrl = uri.GetLeftPart(UriPartial.Path);
                        var response = await PostValidatedAsync(postUrl, content);
                        if (!response.IsSuccessStatusCode)
                        {
                            var respText = await TryReadContentAsync(response.Content);
                            var redactedResp = AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                            _logger.LogWarning("Pushover response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                            await HandleFailedResponseAsync(response);
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
                        string? baseUrl = null;
                        var startup = await _configurationService.GetStartupConfigAsync();
                        if (startup?.UrlBase != null) baseUrl = startup.UrlBase;
                        if (string.IsNullOrWhiteSpace(baseUrl) && _httpContextAccessor?.HttpContext != null)
                        {
                            var derived = NotificationPayloadBuilder.GetBaseUrlFromHttpContext(_httpContextAccessor.HttpContext);
                            if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
                        }

                        var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);
                        var text = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;

                        var telegramBody = new { chat_id = chatId, text = text ?? string.Empty, disable_notification = true, parse_mode = "Markdown" };
                        var json = JsonSerializer.Serialize(telegramBody);
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");

                        var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                        _logger.LogInformation("Sending Telegram POST to {WebhookUrl} with body: {Body}", redactedUrl, AggressiveRedact(LogRedaction.RedactText(json, LogRedaction.GetSensitiveValuesFromEnvironment())));

                        var response = await PostValidatedAsync(webhookUrl, content);
                        if (!response.IsSuccessStatusCode)
                        {
                            var respText = await TryReadContentAsync(response.Content);
                            var redactedResp = AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                            _logger.LogWarning("Telegram response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                            await HandleFailedResponseAsync(response);
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
                    catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
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
                        string? baseUrl = null;
                        var startup = await _configurationService.GetStartupConfigAsync();
                        if (startup?.UrlBase != null) baseUrl = startup.UrlBase;
                        if (string.IsNullOrWhiteSpace(baseUrl) && _httpContextAccessor?.HttpContext != null)
                        {
                            var derived = NotificationPayloadBuilder.GetBaseUrlFromHttpContext(_httpContextAccessor.HttpContext);
                            if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
                        }

                        var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);
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
                        var redactedBody = AggressiveRedact(LogRedaction.RedactText(json, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        _logger.LogInformation("Sending Pushbullet POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedBody);

                        var response = await SendValidatedAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            var respText = await TryReadContentAsync(response.Content);
                            var redactedResp = AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                            _logger.LogWarning("Pushbullet response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                            await HandleFailedResponseAsync(response);
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
                    string? baseUrl = null;
                    var startup = await _configurationService.GetStartupConfigAsync();
                    if (startup?.UrlBase != null) baseUrl = startup.UrlBase;
                    if (string.IsNullOrWhiteSpace(baseUrl) && _httpContextAccessor?.HttpContext != null)
                    {
                        var derived = NotificationPayloadBuilder.GetBaseUrlFromHttpContext(_httpContextAccessor.HttpContext);
                        if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
                    }

                    var discordPayload = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);
                    var message = discordPayload is JsonObject d && d.TryGetPropertyValue("content", out var c) ? (c?.ToString() ?? string.Empty) : string.Empty;

                    var slackObj = new JsonObject
                    {
                        ["text"] = message ?? string.Empty
                    };

                    var json = slackObj.ToJsonString();
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                    var redactedBody = AggressiveRedact(LogRedaction.RedactText(json, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    _logger.LogInformation("Sending Slack POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedBody);

                    var response = await PostValidatedAsync(webhookUrl, content);
                    if (!response.IsSuccessStatusCode)
                    {
                        var respText = await TryReadContentAsync(response.Content);
                        var redactedResp = AggressiveRedact(LogRedaction.RedactText(respText, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        _logger.LogWarning("Slack response from {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedResp);
                        await HandleFailedResponseAsync(response);
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
                string? baseUrl = null;
                var startup = await _configurationService.GetStartupConfigAsync();
                if (startup?.UrlBase != null) baseUrl = startup.UrlBase;
                if (string.IsNullOrWhiteSpace(baseUrl) && _httpContextAccessor?.HttpContext != null)
                {
                    var derived = NotificationPayloadBuilder.GetBaseUrlFromHttpContext(_httpContextAccessor.HttpContext);
                    if (!string.IsNullOrWhiteSpace(derived)) baseUrl = derived;
                }

                // Prefer rich payload created by the static helper (includes content, embeds, image links, etc.)
                var payloadObj = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);
                string defaultJson = payloadObj != null ? payloadObj.ToJsonString() : JsonSerializer.Serialize(new { @event = trigger, data = data, timestamp = DateTime.UtcNow }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

                using var defaultContent = new StringContent(defaultJson, Encoding.UTF8, "application/json");

                var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
                var redactedBody = AggressiveRedact(LogRedaction.RedactText(defaultJson, LogRedaction.GetSensitiveValuesFromEnvironment()));
                _logger.LogInformation("Sending Generic POST to {WebhookUrl} with body: {Body}", redactedUrl, redactedBody);

                var response = await PostValidatedAsync(webhookUrl, defaultContent);
                if (!response.IsSuccessStatusCode)
                {
                    await HandleFailedResponseAsync(response);
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

        // Ensure that any sensitive environment-derived values are redacted even if they were missed
        // by the primary redaction routine. Uses regex replace to catch variants.
        private static string AggressiveRedact(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            try
            {
                var secrets = LogRedaction.GetSensitiveValuesFromEnvironment().Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var result = input;
                foreach (var s in secrets)
                {
                    try
                    {
                        var esc = System.Text.RegularExpressions.Regex.Escape(s);
                        result = System.Text.RegularExpressions.Regex.Replace(result, esc, "<redacted>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        System.Diagnostics.Debug.WriteLine($"NotificationService.AggressiveRedact regex replace failed: {ex.Message}");
                    }
                }

                return result;
            }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { return input; }
        }

        // Safely attempt to read the content of an HttpContent instance. If reading
        // fails (disposed stream, IO error, etc.) the exception is logged at Debug
        // and an empty string is returned to avoid masking the original failure.
        private async Task<string> TryReadContentAsync(HttpContent? content)
        {
            if (content == null) return string.Empty;
            try
            {
                return await content.ReadAsStringAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Could not read HTTP content for diagnostic logging");
                return string.Empty;
            }
        }

        private static bool TryValidateWebhookTarget(string webhookUrl, out string reason, bool allowPrivateTargets = false)
        {
            return OutboundRequestSecurity.TryValidateExternalHttpUrl(webhookUrl, out reason, allowPrivateTargets);
        }
    }
}




