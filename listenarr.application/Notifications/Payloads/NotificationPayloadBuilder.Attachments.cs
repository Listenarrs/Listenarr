/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Listenarr.Application.Common;

namespace Listenarr.Application.Notifications.Payloads
{
    public static partial class NotificationPayloadBuilder
    {
        public static async Task<(JsonObject payload, AttachmentInfo? attachment)> CreateDiscordPayloadWithAttachmentAsync(string trigger, object data, string? startupBaseUrl, HttpClient httpClient, IRequestContextAccessor? requestContextAccessor = null, Action<string>? logInfo = null, Action<Exception, string>? logDebug = null, string? apiVersion = null)
        {
            // Implementation mirrors previous CreateDiscordPayloadWithAttachmentAsync but kept here to centralize payload logic.
            JsonNode? node = data == null ? null : JsonSerializer.SerializeToNode(data);

            string title = string.Empty;
            string author = string.Empty;
            string? asin = null;
            string? publisher = null;
            string? year = null;
            string? imageUrl = null;
            string? narrators = null;
            string? description = null;

            if (node is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("title", out var t) && t != null) title = t.ToString();
                if (obj.TryGetPropertyValue("authors", out var a) && a != null)
                {
                    author = a is JsonArray arr && arr.Count > 0
                        ? arr[0]?.ToString() ?? string.Empty
                        : a.ToString() ?? string.Empty;
                }
                if (obj.TryGetPropertyValue("asin", out var s) && s != null) asin = s.ToString();
                if (obj.TryGetPropertyValue("publisher", out var p) && p != null) publisher = p.ToString();
                if (obj.TryGetPropertyValue("year", out var y) && y != null) year = y.ToString();
                if (obj.TryGetPropertyValue("publishedDate", out var pd) && pd != null)
                {
                    var pdStr = pd.ToString();
                    if (!string.IsNullOrWhiteSpace(pdStr))
                    {
                        year = DateTime.TryParse(pdStr, out var pdDate) ? pdDate.Year.ToString() : pdStr.Length >= 4 ? pdStr.Substring(0, 4) : year;
                    }
                }
                if (obj.TryGetPropertyValue("imageUrl", out var iu) && iu != null) imageUrl = iu.ToString();
                if (obj.TryGetPropertyValue("coverUrl", out var cu) && cu != null) imageUrl = imageUrl ?? cu.ToString();
                if (obj.TryGetPropertyValue("narrators", out var n) && n != null)
                {
                    narrators = n is JsonArray narrArr && narrArr.Count > 0
                        ? string.Join(", ", narrArr.Where(x => x != null && !string.IsNullOrEmpty(x.ToString())).Select(x => x!.ToString()))
                        : n.ToString();
                }
                if (obj.TryGetPropertyValue("description", out var d) && d != null) description = d.ToString();
            }

            title = DecodeHtml(title);
            author = DecodeHtml(author);
            publisher = DecodeHtml(publisher);
            narrators = DecodeHtml(narrators);
            description = DecodeHtml(description);

            // Use centralized constants declared at class scope

            static string Truncate(string? value, int max)
            {
                if (string.IsNullOrEmpty(value)) return string.Empty;
                return value.Length <= max ? value : value.Substring(0, max);
            }

            var embed = new JsonObject();
            if (!string.IsNullOrWhiteSpace(title)) embed["title"] = Truncate(title, MAX_TITLE);

            string? absoluteImageUrl = null;
            string? thumbnailUrl = null;
            if (!string.IsNullOrWhiteSpace(asin) && !string.IsNullOrWhiteSpace(startupBaseUrl))
            {
                thumbnailUrl = startupBaseUrl.TrimEnd('/') + ApiVersionUtils.BuildImagePath(asin, fallbackVersion: apiVersion);
            }

            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    absoluteImageUrl = imageUrl;
                    logInfo?.Invoke($"Image URL is already absolute: {absoluteImageUrl}");
                }
                else if (imageUrl.StartsWith("/") && !string.IsNullOrWhiteSpace(startupBaseUrl))
                {
                    absoluteImageUrl = startupBaseUrl.TrimEnd('/') + imageUrl;
                    logInfo?.Invoke($"Constructed absolute URL from relative path: {absoluteImageUrl}");
                }
                else if (imageUrl.StartsWith("/") && startupBaseUrl == null && requestContextAccessor?.Current != null)
                {
                    var derived = GetBaseUrlFromRequestContext(requestContextAccessor.Current);
                    if (!string.IsNullOrWhiteSpace(derived)) absoluteImageUrl = derived.TrimEnd('/') + imageUrl;
                }
            }

            AttachmentInfo? attachmentInfo = null;
            string? attachmentFilename = null;

            if (!string.IsNullOrWhiteSpace(absoluteImageUrl))
            {
                try
                {
                    logInfo?.Invoke($"Attempting to download image for attachment: {absoluteImageUrl}");
                    var imageResponse = await httpClient.GetAsync(absoluteImageUrl);
                    if (imageResponse.IsSuccessStatusCode)
                    {
                        var imageData = await imageResponse.Content.ReadAsByteArrayAsync();
                        if (imageData != null && imageData.Length > 0)
                        {
                            var contentType = imageResponse.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                            var sanitizedTitle = title?.Replace(" ", "_").Replace("/", "_") ?? "unknown";
                            if (string.IsNullOrWhiteSpace(sanitizedTitle)) sanitizedTitle = "unknown";
                            var filename = !string.IsNullOrWhiteSpace(asin)
                                ? $"{asin}.jpg"
                                : $"{sanitizedTitle.Substring(0, Math.Min(50, sanitizedTitle.Length))}.jpg";

                            attachmentInfo = new AttachmentInfo
                            {
                                ImageData = imageData,
                                Filename = filename,
                                ContentType = contentType
                            };

                            attachmentFilename = filename;
                            logInfo?.Invoke($"Successfully downloaded image for notification: {absoluteImageUrl}");
                        }
                        else
                        {
                            logInfo?.Invoke($"Downloaded image has no data: {absoluteImageUrl}");
                        }
                    }
                    else
                    {
                        logInfo?.Invoke($"Failed to download image for notification: {absoluteImageUrl} - HTTP {imageResponse.StatusCode}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logDebug?.Invoke(ex, $"Error downloading image for notification from {absoluteImageUrl}: {ex.Message}");
                }
            }

            bool thumbnailSet = false;

            if (attachmentInfo != null && !string.IsNullOrWhiteSpace(attachmentFilename))
            {
                embed["thumbnail"] = new JsonObject { ["url"] = $"attachment://{attachmentFilename}" };
                thumbnailSet = true;
            }
            else if (!string.IsNullOrWhiteSpace(absoluteImageUrl))
            {
                embed["thumbnail"] = new JsonObject { ["url"] = Truncate(absoluteImageUrl, 2000) };
                thumbnailSet = true;
            }
            else if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                embed["thumbnail"] = new JsonObject { ["url"] = thumbnailUrl };
                thumbnailSet = true;
            }

            if (!thumbnailSet)
            {
                // no-op: caller may log if needed
            }

            var embeds = new JsonArray();
            var fields = new JsonArray();

            if (!string.IsNullOrWhiteSpace(author))
            {
                var fa = new JsonObject();
                fa["name"] = Truncate("Author", MAX_FIELD_NAME);
                fa["value"] = Truncate(author, MAX_FIELD_VALUE);
                fa["inline"] = false;
                fields.Add(fa);
            }

            if (!string.IsNullOrWhiteSpace(publisher))
            {
                var f = new JsonObject();
                f["name"] = Truncate("Publisher", MAX_FIELD_NAME);
                f["value"] = Truncate(publisher, MAX_FIELD_VALUE);
                f["inline"] = true;
                fields.Add(f);
            }
            if (!string.IsNullOrWhiteSpace(year))
            {
                var f = new JsonObject();
                f["name"] = Truncate("Year", MAX_FIELD_NAME);
                f["value"] = Truncate(year, MAX_FIELD_VALUE);
                f["inline"] = true;
                fields.Add(f);
            }
            if (!string.IsNullOrWhiteSpace(narrators))
            {
                var f = new JsonObject();
                f["name"] = Truncate("Narrated by", MAX_FIELD_NAME);
                f["value"] = Truncate(narrators, MAX_FIELD_VALUE);
                f["inline"] = false;
                fields.Add(f);
            }
            if (!string.IsNullOrWhiteSpace(description))
            {
                var cleanedDescription = CleanHtml(description);
                var truncatedDesc = Truncate(cleanedDescription, Math.Min(MAX_FIELD_VALUE, 500));
                var f = new JsonObject();
                f["name"] = Truncate("Description", MAX_FIELD_NAME);
                f["value"] = truncatedDesc;
                f["inline"] = false;
                fields.Add(f);
            }

            if (embed.ContainsKey("title") || embed.ContainsKey("description") || fields.Count > 0)
            {
                if (fields.Count > 0) embed["fields"] = fields;
                embeds.Add(embed);
            }

            if (!string.IsNullOrEmpty(publisher) || !string.IsNullOrEmpty(year))
            {
                var footerText = string.Empty;
                if (!string.IsNullOrWhiteSpace(publisher) && !string.IsNullOrWhiteSpace(year)) footerText = $"{publisher} - {year}";
                else if (!string.IsNullOrWhiteSpace(publisher)) footerText = publisher;
                else footerText = year ?? string.Empty;

                if (embeds.Count > 0)
                {
                    var e = embeds[0]!.AsObject();
                    e["footer"] = new JsonObject { ["text"] = footerText };
                }
            }

            var payload = new JsonObject();
            var shortContent = BuildDiscordContent(trigger, title ?? string.Empty, author ?? string.Empty);
            payload["content"] = shortContent;
            payload["username"] = "Listenarr";
            payload["avatar_url"] = "https://raw.githubusercontent.com/Listenarrs/Listenarr/main/.github/logo-icon.png";
            if (embeds.Count > 0)
            {
                payload["embeds"] = embeds;
            }

            return (payload, attachmentInfo);
        }

        public static string? GetBaseUrlFromRequestContext(RequestContextSnapshot? ctx)
        {
            if (ctx == null) return null;
            var scheme = ctx.Scheme;
            var host = ctx.Host;
            if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(host)) return null;
            return scheme + "://" + host;
        }

        private static string BuildDiscordContent(string trigger, string title, string author)
        {
            if (string.Equals(trigger, "book-added", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(author))
                {
                    return $"{title} by {author} has been added";
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    return $"{title} has been added";
                }

                return "A new audiobook has been added";
            }

            if (string.Equals(trigger, "book-available", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(author))
                {
                    return $"{title} by {author} is now available";
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    return $"{title} is now available";
                }

                return "An audiobook is now available";
            }

            if (string.Equals(trigger, "book-downloading", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(author))
                {
                    return $"{title} by {author} is downloading";
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    return $"{title} is downloading";
                }

                return "An audiobook is downloading";
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                return $"[{trigger}] {title}";
            }

            return $"[{trigger}]";
        }

        private static string DecodeHtml(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return WebUtility.HtmlDecode(text);
        }

        private static string CleanHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            var cleaned = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", string.Empty);
            cleaned = WebUtility.HtmlDecode(cleaned);
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();

            return cleaned;
        }
    }
}
