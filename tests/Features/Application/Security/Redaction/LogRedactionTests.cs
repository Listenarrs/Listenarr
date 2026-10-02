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
namespace Listenarr.Tests.Features.Application.Security.Redaction
{
    public class LogRedactionTests
    {
        [Fact]
        public void RedactText_ReplacesSensitiveEnvironmentValues()
        {
            var key = "LISTENARR_API_KEY";
            var secret = "supersecret-TEST-123";
            try
            {
                Environment.SetEnvironmentVariable(key, secret);

                var inputs = new[]
                {
                    $"This is a log line containing the secret: {secret}",
                    $"Multiple {secret} occurrences {secret}"
                };

                foreach (var input in inputs)
                {
                    var redacted = LogRedaction.RedactText(input, LogRedaction.GetSensitiveValuesFromEnvironment());
                    Assert.DoesNotContain(secret, redacted);
                    Assert.Contains("<redacted>", redacted);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        [Fact]
        public void GetSensitiveValuesFromEnvironment_ReturnsSetVariables()
        {
            var key = "LISTENARR_API_KEY";
            var secret = "env-secret-XYZ";
            try
            {
                Environment.SetEnvironmentVariable(key, secret);
                var vals = LogRedaction.GetSensitiveValuesFromEnvironment();
                Assert.Contains(secret, vals);
            }
            finally
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        [Fact]
        public void SanitizeWebhookUrl_Telegram_MasksTokenSegment_KeepsMethodAndQueryDropped()
        {
            var token = "123456789:AAExampleBotTokenValueNotReal";
            var url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id=987654321";

            var sanitized = LogRedaction.SanitizeWebhookUrl(url);

            Assert.DoesNotContain(token, sanitized);
            Assert.DoesNotContain("987654321", sanitized);
            Assert.Equal("https://api.telegram.org/bot<redacted>/sendMessage", sanitized);
        }

        [Fact]
        public void SanitizeWebhookUrl_Discord_MasksIdAndTokenSegments_KeepsApiWebhooksPath()
        {
            var id = "111222333444555666";
            var token = "DiscordWebhookTokenValueNotReal";
            var url = $"https://discord.com/api/webhooks/{id}/{token}";

            var sanitized = LogRedaction.SanitizeWebhookUrl(url);

            Assert.DoesNotContain(id, sanitized);
            Assert.DoesNotContain(token, sanitized);
            Assert.Contains("api/webhooks", sanitized);
            Assert.Equal("https://discord.com/api/webhooks/<redacted>/<redacted>", sanitized);
        }

        [Fact]
        public void SanitizeWebhookUrl_Slack_MasksEverythingAfterServices()
        {
            var team = "T00000000";
            var bot = "B00000000";
            var secret = "XXXXXXXXXXXXXXXXXXXXXXXX";
            var url = $"https://hooks.slack.com/services/{team}/{bot}/{secret}";

            var sanitized = LogRedaction.SanitizeWebhookUrl(url);

            Assert.DoesNotContain(team, sanitized);
            Assert.DoesNotContain(bot, sanitized);
            Assert.DoesNotContain(secret, sanitized);
            Assert.StartsWith("https://hooks.slack.com/services/", sanitized);
        }

        [Fact]
        public void SanitizeWebhookUrl_Pushover_DropsQueryString_KeepsHostAndPath()
        {
            var token = "app-token-abc123";
            var user = "user-key-xyz789";
            var url = $"https://api.pushover.net/1/messages.json?token={token}&user={user}";

            var sanitized = LogRedaction.SanitizeWebhookUrl(url);

            Assert.DoesNotContain(token, sanitized);
            Assert.DoesNotContain(user, sanitized);
            Assert.DoesNotContain("?", sanitized);
            Assert.Equal("https://api.pushover.net/1/messages.json", sanitized);
        }

        [Fact]
        public void SanitizeWebhookUrl_ControlCase_NonCredentialUrl_PathSurvivesIntact()
        {
            // No query string, no known-provider host, no credential-shaped path segments.
            // A version that just blanks everything would fail this assertion, not just the ones above.
            var url = "https://example.com/some/harmless/path";

            var sanitized = LogRedaction.SanitizeWebhookUrl(url);

            Assert.Equal("https://example.com/some/harmless/path", sanitized);
        }
    }
}
