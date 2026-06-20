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
using System.Text.RegularExpressions;

namespace Listenarr.Api.Middleware
{
    /// <summary>
    /// Middleware to log incoming request bodies for debugging purposes.
    /// Only logs for HTTP methods that typically carry request bodies (POST/PUT/PATCH).
    /// Body is redacted using LogRedaction and truncated to a safe maximum length.
    /// </summary>
    public class RequestBodyLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestBodyLoggingMiddleware> _logger;
        private readonly bool _enabled;
        private const int MaxLogBodySize = 64 * 1024; // 64KB

        public RequestBodyLoggingMiddleware(
            RequestDelegate next,
            ILogger<RequestBodyLoggingMiddleware> logger,
            IHostEnvironment hostEnvironment,
            IConfiguration configuration)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // Disable request body logging by default outside Development.
            _enabled = hostEnvironment.IsDevelopment() || configuration.GetValue<bool>("Listenarr:EnableRequestBodyLogging");
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_enabled)
            {
                await _next(context);
                return;
            }

            var method = context.Request.Method?.ToUpperInvariant() ?? string.Empty;
            if (method == HttpMethods.Post || method == HttpMethods.Put || method == "PATCH")
            {
                try
                {
                    var path = context.Request.Path.Value ?? string.Empty;
                    if (IsSensitivePath(path))
                    {
                        await _next(context);
                        return;
                    }

                    context.Request.EnableBuffering();
                    context.Request.Body.Seek(0, SeekOrigin.Begin);
                    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                    var body = await reader.ReadToEndAsync();

                    if (!string.IsNullOrEmpty(body))
                    {
                        var truncated = body.Length > MaxLogBodySize ? body.Substring(0, MaxLogBodySize) + "..." : body;
                        var redacted = RedactSensitiveJsonFields(LogRedaction.RedactText(truncated, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        _logger.LogInformation("Incoming {Method} {Path} body: {Body}", method, context.Request.Path, redacted);
                    }

                    context.Request.Body.Seek(0, SeekOrigin.Begin);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to log request body for {Method} {Path}", method, context.Request.Path);
                }
            }

            await _next(context);
        }

        private static bool IsSensitivePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.Contains("/account/login", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/configuration/startupconfig", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/apikey/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/download-clients", StringComparison.OrdinalIgnoreCase);
        }

        internal static string RedactSensitiveJsonFields(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var redacted = input;
            var patterns = new[]
            {
                "(?i)(\"password\"\\s*:\\s*)\"[^\"]*\"",
                "(?i)(\"passwordHash\"\\s*:\\s*)\"[^\"]*\"",
                "(?i)(\"apiKey\"\\s*:\\s*)\"[^\"]*\"",
                "(?i)(\"token\"\\s*:\\s*)\"[^\"]*\"",
                "(?i)(\"authorization\"\\s*:\\s*)\"[^\"]*\"",
                "(?i)(\"secret\"\\s*:\\s*)\"[^\"]*\""
            };

            foreach (var pattern in patterns)
            {
                redacted = Regex.Replace(redacted, pattern, "$1\"<redacted>\"");
            }

            redacted = Regex.Replace(
                redacted,
                "(?i)(\"(?:torrentUrl|nzbUrl|resultUrl|originalUrl|magnetLink)\"\\s*:\\s*)\"([^\"]*)\"",
                match =>
                {
                    var sanitizedUrl = LogRedaction.SanitizeUrl(match.Groups[2].Value);
                    return $"{match.Groups[1].Value}\"{sanitizedUrl}\"";
                });

            return redacted;
        }
    }
}
