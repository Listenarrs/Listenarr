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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/diagnostics")]
    [Tags("Notifications")]
    public class DiagnosticsController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly NotificationService _notificationService;
        private readonly ILogger<DiagnosticsController> _logger;

        public DiagnosticsController(IConfigurationService configurationService, NotificationService notificationService, ILogger<DiagnosticsController> logger)
        {
            _configurationService = configurationService;
            _notificationService = notificationService;
            _logger = logger;
        }

        public class TestNotificationRequest
        {
            public string? Trigger { get; set; }
            public object? Data { get; set; }
            public string? WebhookId { get; set; }
            public string? WebhookUrl { get; set; }
        }

        /// <summary>
        /// Send a test webhook notification to verify the configured webhook URL works correctly.
        /// </summary>
        /// <param name="req">Optional trigger type, data payload, and target webhook ID or URL.</param>
        /// <remarks>Restricted to local or admin callers. The webhook URL must match a configured target.</remarks>
        [HttpPost("test-notification")]
        [IgnoreAntiforgeryToken]
        public async Task<ActionResult<object>> TestNotification([FromBody] TestNotificationRequest req)
        {
            var gate = SensitiveEndpointAccessGuard.RequireLocalOrAdmin(HttpContext, _logger, "diagnostics/test-notification");
            if (gate is ObjectResult gateResult)
            {
                return StatusCode(gateResult.StatusCode ?? Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, gateResult.Value);
            }

            try
            {
                if (req == null) return BadRequest(new { success = false, message = "Missing request body" });
                if (string.IsNullOrWhiteSpace(req.Trigger)) return BadRequest(new { success = false, message = "Missing trigger" });

                var settings = await _configurationService.GetApplicationSettingsAsync();
                if (settings == null)
                {
                    return StatusCode(500, new { success = false, message = "Application settings unavailable" });
                }

                // Prefer explicit webhook URL supplied by the UI (avoids race with concurrent saves)
                string? targetUrl = null;
                if (!string.IsNullOrWhiteSpace(req.WebhookUrl))
                {
                    var configuredUrls = (settings.Webhooks ?? new List<WebhookConfiguration>())
                        .Select(w => w.Url)
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(settings.WebhookUrl))
                    {
                        configuredUrls.Add(settings.WebhookUrl);
                    }

                    if (configuredUrls.Contains(req.WebhookUrl))
                    {
                        targetUrl = req.WebhookUrl;
                    }
                    else
                    {
                        return BadRequest(new { success = false, message = "Webhook URL must match a configured webhook target" });
                    }
                }
                else if (!string.IsNullOrWhiteSpace(req.WebhookId) && settings.Webhooks != null)
                {
                    var match = settings.Webhooks.FirstOrDefault(w => string.Equals(w.Id, req.WebhookId, StringComparison.OrdinalIgnoreCase));
                    if (match != null) targetUrl = match.Url;
                }

                // Fallback to legacy WebhookUrl from settings
                if (string.IsNullOrWhiteSpace(targetUrl)) targetUrl = settings.WebhookUrl;

                if (string.IsNullOrWhiteSpace(targetUrl))
                {
                    return BadRequest(new { success = false, message = "No webhook URL configured or webhook id not found" });
                }

                await _notificationService.SendNotificationAsync(req.Trigger, req.Data ?? new { }, targetUrl, new List<string> { req.Trigger });

                return Ok(new { success = true, message = "Test notification sent" });
            }
            catch (OperationCanceledException)
            {
                // Let request cancellations propagate; don't treat them as server errors.
                throw;
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex, "Operation timed out in DiagnosticsController.TestNotification");
                // Do not leak internal exception messages to callers. Details are available in server logs.
                return StatusCode(500, new { success = false, message = "Failed to send test notification" });
            }
        }
    }
}
// Diagnostics controller removed because Playwright support is no longer available.
