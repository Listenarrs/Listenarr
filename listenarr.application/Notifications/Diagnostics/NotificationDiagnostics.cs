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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Notifications.Diagnostics
{
    public static class NotificationDiagnostics
    {
        public static string AggressiveRedact(string input)
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
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine($"NotificationService.AggressiveRedact regex replace failed: {ex.Message}");
                    }
                }

                return result;
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException) { return input; }
        }

        public static async Task<string> TryReadContentAsync(HttpContent? content, ILogger logger)
        {
            if (content == null) return string.Empty;
            try
            {
                return await content.ReadAsStringAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Could not read HTTP content for diagnostic logging");
                return string.Empty;
            }
        }

        public static async Task LogFailedResponseAsync(HttpResponseMessage response, string webhookUrl, ILogger logger)
        {
            string body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to read notification response body for diagnostic logging");
            }

            var redactedUrl = LogRedaction.RedactText(webhookUrl, LogRedaction.GetSensitiveValuesFromEnvironment());
            var redactedBody = LogRedaction.RedactText(body, LogRedaction.GetSensitiveValuesFromEnvironment());
            redactedBody = AggressiveRedact(redactedBody);
            if (string.IsNullOrEmpty(redactedBody)) redactedBody = "<redacted>";

            logger.LogWarning("Failed to send notification to {WebhookUrl}: {Status} - {Body}", redactedUrl, response.StatusCode, redactedBody);
            logger.LogWarning("BodyRedacted: {Body}", "<redacted>");
        }

        public static bool TryValidateWebhookTarget(string webhookUrl, out string reason, bool allowPrivateTargets = false)
        {
            return OutboundRequestSecurity.TryValidateExternalHttpUrl(webhookUrl, out reason, allowPrivateTargets);
        }
    }
}
