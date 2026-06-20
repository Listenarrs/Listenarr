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

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Indexers.MyAnonamouse
{
    public static partial class MyAnonamouseResponseParser
    {
        public static List<IndexerSearchResult> Parse(string jsonResponse, Indexer indexer, ILogger logger)
        {
            var results = new List<IndexerSearchResult>();

            if (indexer == null)
            {
                logger.LogError("ParseMyAnonamouseResponse called with null indexer");
                return results;
            }

            try
            {
                logger.LogDebug("Parsing MyAnonamouse response, length: {Length}", jsonResponse.Length);

                if (!MyAnonamouseJsonResultExtractor.TryExtractResultArray(jsonResponse, indexer, logger, out var doc, out var dataArrayElement))
                {
                    return results;
                }

                logger.LogDebug("Found {Count} MyAnonamouse results", dataArrayElement.GetArrayLength());
                try
                {
                    if (dataArrayElement.GetArrayLength() > 0)
                    {
                        var firstRaw = dataArrayElement[0].ToString();
                        var preview = firstRaw.Length > 400 ? firstRaw.Substring(0, 400) + "..." : firstRaw;
                        logger.LogDebug("First MyAnonamouse item preview: {Preview}", LogRedaction.RedactText(preview, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty })));

                        // Log full property list for the first item to aid debugging field names
                        try
                        {
                            var firstItem = dataArrayElement[0];
                            var fields = string.Join(", ", firstItem.EnumerateObject().Select(p => $"{p.Name}={p.Value}"));
                            logger.LogInformation("First MyAnonamouse result fields: {Fields}", LogRedaction.RedactText(fields, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty })));
                        }
                        catch (Exception exFields) when (exFields is not OperationCanceledException && exFields is not OutOfMemoryException && exFields is not StackOverflowException)
                        {
                            logger.LogDebug(exFields, "Failed to enumerate fields of first MyAnonamouse item");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to produce preview of first MyAnonamouse item");
                }

                int _mamDebugIndex = 0;
                foreach (var item in dataArrayElement.EnumerateArray())
                {
                    try
                    {
                        // Log property names for first few items to aid debugging
                        if (_mamDebugIndex < 3)
                        {
                            try
                            {
                                var propertyNames = item.EnumerateObject().Select(p => p.Name).ToList();
                                logger.LogInformation("MyAnonamouse result #{Index} has properties: {Properties}", _mamDebugIndex, string.Join(", ", propertyNames));
                            }
                            catch (Exception exNames) when (exNames is not OperationCanceledException && exNames is not OutOfMemoryException && exNames is not StackOverflowException)
                            {
                                logger.LogDebug(exNames, "Failed to enumerate property names for MyAnonamouse result #{Index}", _mamDebugIndex);
                            }
                        }

                        var id = item.TryGetProperty("id", out var idElem)
                            ? idElem.ValueKind == JsonValueKind.String ? idElem.GetString() ?? string.Empty : idElem.ToString()
                            : Guid.NewGuid().ToString();

                        // MyAnonamouse uses "title" in responses; fall back to "name" if needed
                        var title = "";
                        if (item.TryGetProperty("title", out var titleElem))
                        {
                            title = titleElem.ValueKind == JsonValueKind.String ? titleElem.GetString() ?? "" : titleElem.ToString();
                        }
                        else if (item.TryGetProperty("name", out titleElem))
                        {
                            title = titleElem.ValueKind == JsonValueKind.String ? titleElem.GetString() ?? "" : titleElem.ToString();
                        }
                        var sizeStr = "";
                        if (item.TryGetProperty("size", out var sizeElem))
                        {
                            if (sizeElem.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                sizeStr = sizeElem.GetString() ?? "0";
                            }
                            else if (sizeElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                            {
                                sizeStr = sizeElem.GetInt64().ToString();
                            }
                            else
                            {
                                sizeStr = "0";
                            }
                        }
                        var seeders = item.TryGetProperty("seeders", out var seedElem) ? seedElem.GetInt32() : 0;
                        var leechers = item.TryGetProperty("leechers", out var leechElem) ? leechElem.GetInt32() : 0;
                        string dlHash = string.Empty;
                        if (item.TryGetProperty("dl", out var dlElem))
                        {
                            dlHash = dlElem.ValueKind == JsonValueKind.String ? dlElem.GetString() ?? string.Empty : dlElem.ToString();
                        }

                        // New: explicit downloadUrl / infoUrl / fileName fields commonly provided by Prowlarr
                        string? downloadUrlField = null;
                        string? infoUrlField = null;
                        string? fileNameField = null;
                        // Use case-insensitive property lookup for robustness against differing casing in tracker responses
                        foreach (var prop in item.EnumerateObject())
                        {
                            var name = prop.Name;
                            if (downloadUrlField == null && string.Equals(name, "downloadUrl", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                                downloadUrlField = prop.Value.GetString();
                            if (infoUrlField == null && string.Equals(name, "infoUrl", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                                infoUrlField = prop.Value.GetString();
                            if (fileNameField == null && string.Equals(name, "fileName", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                                fileNameField = prop.Value.GetString();
                        }

                        string category = string.Empty;
                        if (item.TryGetProperty("catname", out var catElem))
                        {
                            category = catElem.ValueKind == JsonValueKind.String ? catElem.GetString() ?? string.Empty : catElem.ToString();
                        }

                        string tags = string.Empty;
                        if (item.TryGetProperty("tags", out var tagsElem))
                        {
                            tags = tagsElem.ValueKind == JsonValueKind.String ? tagsElem.GetString() ?? string.Empty : tagsElem.ToString();
                        }

                        string description = string.Empty;
                        if (item.TryGetProperty("description", out var descElem))
                        {
                            description = descElem.ValueKind == JsonValueKind.String ? descElem.GetString() ?? string.Empty : descElem.ToString();
                        }

                        // Parse grabs/files when present (Prowlarr exposes these directly for MyAnonamouse)
                        var grabs = 0;
                        var grabKeys = new[] { "grabs", "snatches", "snatched", "snatched_count", "snatches_count", "numgrabs", "num_grabs", "grab_count", "times_completed", "completed", "downloaded", "times_downloaded" };
                        foreach (var prop in item.EnumerateObject().Where(prop => grabKeys.Any(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase))))
                        {
                            var ge = prop.Value;
                            logger.LogInformation("Found grabs candidate field '{Field}' (kind={Kind}) for '{Title}': {Value}", prop.Name, ge.ValueKind, ge.ToString(), title);
                            if (ge.ValueKind == JsonValueKind.Number)
                            {
                                grabs = ge.GetInt32();
                                logger.LogInformation("Parsed grabs for '{Title}' from field '{Field}': {Grabs}", title, prop.Name, grabs);
                                break;
                            }
                            else if (ge.ValueKind == JsonValueKind.String && int.TryParse(ge.GetString(), out var gtmp))
                            {
                                grabs = gtmp;
                                logger.LogInformation("Parsed grabs (string) for '{Title}' from field '{Field}': {Grabs}", title, prop.Name, grabs);
                                break;
                            }
                        }

                        var files = 0;
                        foreach (var prop in item.EnumerateObject().Where(prop =>
                            string.Equals(prop.Name, "files", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "numfiles", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "num_files", StringComparison.OrdinalIgnoreCase)))
                        {
                            var fe = prop.Value;
                            logger.LogInformation("Found files candidate field '{Field}' (kind={Kind}) for '{Title}': {Value}", prop.Name, fe.ValueKind, fe.ToString(), title);
                            if (fe.ValueKind == JsonValueKind.Number)
                            {
                                files = fe.GetInt32();
                                logger.LogInformation("Parsed files for '{Title}' from field '{Field}': {Files}", title, prop.Name, files);
                            }
                            else if (fe.ValueKind == JsonValueKind.String && int.TryParse(fe.GetString(), out var ftmp))
                            {
                                files = ftmp;
                                logger.LogInformation("Parsed files (string) for '{Title}' from field '{Field}': {Files}", title, prop.Name, files);
                            }

                            break;
                        }

                        var publishDate = MyAnonamousePublishDateParser.Parse(item, title, logger);

                        if (string.IsNullOrEmpty(title))
                            continue;

                        // (debug log moved later after we build the result so all fields exist)

                        // Parse size - handle various formats
                        long size = 0;
                        if (!string.IsNullOrEmpty(sizeStr) && sizeStr != "0")
                        {
                            size = MyAnonamouseSizeParser.ParseSizeString(sizeStr, logger);
                            logger.LogDebug("Parsed size for MyAnonamouse result '{Title}': {Size} bytes from size field '{SizeStr}'", title, size, sizeStr);
                        }
                        else
                        {
                            // Try to extract size from description when size field is 0
                            size = MyAnonamouseSizeParser.ExtractFromDescription(description, logger);
                            if (size > 0)
                            {
                                logger.LogDebug("Parsed size for MyAnonamouse result '{Title}': {Size} bytes from description", title, size);
                            }
                            else
                            {
                                logger.LogWarning("MyAnonamouse result '{Title}' has no size information in size field or description", title);
                            }
                        }

                        // Extract author from author_info JSON
                        string? author = null;
                        if (item.TryGetProperty("author_info", out var authorInfo))
                        {
                            try
                            {
                                author = MyAnonamouseContributorParser.ParseContributorList(authorInfo.GetString());
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                logger.LogWarning(ex, "Failed to parse author JSON for search result");
                            }
                        }

                        // Extract narrator from narrator_info JSON
                        string? narrator = null;
                        if (item.TryGetProperty("narrator_info", out var narratorInfo))
                        {
                            try
                            {
                                narrator = MyAnonamouseContributorParser.ParseContributorList(narratorInfo.GetString());
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                logger.LogWarning(ex, "Failed to parse narrator JSON for search result");
                            }
                        }

                        // Detect quality and format with robust fallbacks:
                        // 1) Prefer explicit format/filetype fields when present
                        // 2) Use tags when available
                        // 3) Fallback to description and title (filename) parsing

                        // Try to read explicit format/filetype fields from the item (case-insensitive)
                        var rawFormatField = item.EnumerateObject()
                            .Where(prop => prop.Value.ValueKind == JsonValueKind.String &&
                                           (string.Equals(prop.Name, "format", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(prop.Name, "filetype", StringComparison.OrdinalIgnoreCase)))
                            .Select(prop => prop.Value.GetString() ?? string.Empty)
                            .FirstOrDefault() ?? string.Empty;

                        // Detect format from tags and from explicit field
                        var formatFromTags = SearchResultAttributeParser.DetectFormatFromTags(tags ?? "");
                        var formatFromField = !string.IsNullOrEmpty(rawFormatField) ? SearchResultAttributeParser.DetectFormatFromTags(rawFormatField) : null;
                        var finalFormat = (formatFromField != null && formatFromField != "MP3") ? formatFromField : formatFromTags;

                        // Log explicit filetype when present
                        if (!string.IsNullOrEmpty(rawFormatField))
                        {
                            logger.LogDebug("MyAnonamouse: found explicit filetype '{Filetype}' for item {Id}", rawFormatField, id);
                        }

                        // Detect quality: prefer tags, then explicit format field, then description/title
                        var qualityFromTags = SearchResultAttributeParser.DetectQualityFromTags(tags ?? "");
                        var finalQuality = qualityFromTags != "Unknown" ? qualityFromTags : (!string.IsNullOrEmpty(rawFormatField) ? SearchResultAttributeParser.DetectQualityFromFormat(rawFormatField) : "Unknown");

                        // Fallback: try to detect quality from description or title (filename-like text)
                        if (finalQuality == "Unknown")
                        {
                            if (!string.IsNullOrEmpty(description))
                            {
                                var q = SearchResultAttributeParser.DetectQualityFromTags(description);
                                if (q != "Unknown") finalQuality = q;
                                else
                                {
                                    var q2 = SearchResultAttributeParser.DetectQualityFromFormat(description);
                                    if (q2 != "Unknown") finalQuality = q2;
                                }
                            }

                            if (finalQuality == "Unknown")
                            {
                                var probeText = title;
                                var q = SearchResultAttributeParser.DetectQualityFromTags(probeText);
                                if (q != "Unknown") finalQuality = q;
                                else
                                {
                                    var q2 = SearchResultAttributeParser.DetectQualityFromFormat(probeText);
                                    if (q2 != "Unknown") finalQuality = q2;
                                }
                            }
                        }

                        // Additional fallback: if format still looks generic MP3, probe description/title
                        if (finalFormat == "MP3")
                        {
                            if (!string.IsNullOrEmpty(description))
                            {
                                var f = SearchResultAttributeParser.DetectFormatFromTags(description);
                                if (!string.IsNullOrEmpty(f) && f != "MP3") finalFormat = f;
                            }

                            if (finalFormat == "MP3")
                            {
                                var probeText = title;
                                var f = SearchResultAttributeParser.DetectFormatFromTags(probeText);
                                if (!string.IsNullOrEmpty(f) && f != "MP3") finalFormat = f;
                            }
                        }

                        var downloadUrl = MyAnonamouseDownloadUrlBuilder.Build(dlHash, id, indexer);

                        // Preserve raw language code for later flagging/flags list
                        string rawLangCode = string.Empty;
                        logger.LogDebug("MyAnonamouse: rawFormat='{Raw}', finalFormat='{Final}', rawLang='{LangCode}'", rawFormatField, finalFormat, rawLangCode);

                        var result = new IndexerSearchResult
                        {
                            Id = id ?? Guid.NewGuid().ToString(),
                            Title = title,
                            Artist = author ?? "Unknown Author",
                            Album = narrator != null ? $"Narrated by {narrator}" : "Unknown",
                            Category = category ?? "Audiobook",
                            Size = size,
                            Seeders = seeders,
                            Leechers = leechers,
                            Source = indexer.Name ?? "MyAnonamouse",
                            PublishedDate = publishDate?.ToString("o") ?? string.Empty,
                            Quality = finalQuality,
                            Format = finalFormat,
                            TorrentUrl = downloadUrl,
                            // Use MyAnonamouse public item page pattern: https://myanonamouse.net/t/{id}
                            ResultUrl = !string.IsNullOrEmpty(id) ? $"https://myanonamouse.net/t/{Uri.EscapeDataString(id)}" : (indexer.Url ?? ""),
                            MagnetLink = "",
                            NzbUrl = ""
                        };
                        // If we have a parsed language code, map to name and preserve raw code
                        if (!string.IsNullOrEmpty(rawLangCode) && string.IsNullOrEmpty(result.Language))
                        {
                            result.Language = SearchResultAttributeParser.ParseLanguageFromCode(rawLangCode) ?? SearchResultAttributeParser.ParseLanguageFromText(rawLangCode);
                        }
                        result.IndexerId = indexer.Id;
                        result.IndexerImplementation = indexer.Implementation ?? string.Empty;
                        PopulateDownloadLinks(
                            item,
                            result,
                            downloadUrlField,
                            infoUrlField,
                            fileNameField,
                            title,
                            id ?? string.Empty,
                            _mamDebugIndex,
                            logger);

                        // Prefer explicit language fields when present (lang_code, language_code, lang, language) - case-insensitive search
                        string explicitLang = string.Empty;
                        foreach (var prop in item.EnumerateObject().Where(prop =>
                            (prop.Name.Equals("lang_code", StringComparison.OrdinalIgnoreCase) ||
                             prop.Name.Equals("language_code", StringComparison.OrdinalIgnoreCase) ||
                             prop.Name.Equals("lang", StringComparison.OrdinalIgnoreCase) ||
                             prop.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) &&
                            prop.Value.ValueKind == JsonValueKind.String))
                        {
                            explicitLang = prop.Value.GetString() ?? string.Empty;
                            logger.LogDebug("MyAnonamouse: found language field '{Field}'='{Lang}' for item {Id}", prop.Name, explicitLang, id);
                            break;
                        }

                        // Numeric language id fallback (case-insensitive check)
                        if (string.IsNullOrEmpty(explicitLang) && item.TryGetProperty("language", out var langNumElem) && langNumElem.ValueKind == JsonValueKind.Number)
                        {
                            var numeric = langNumElem.GetInt32();
                            if (numeric == 1) { explicitLang = "ENG"; }
                            logger.LogDebug("MyAnonamouse: found numeric language id={Num} mapped to '{Lang}' for item {Id}", numeric, explicitLang, id);
                        }

                        if (!string.IsNullOrWhiteSpace(explicitLang))
                        {
                            // Prefer direct code mapping (e.g., ENG -> English) when a short code is provided
                            var parsedLang = SearchResultAttributeParser.ParseLanguageFromCode(explicitLang) ?? SearchResultAttributeParser.ParseLanguageFromText(explicitLang);
                            if (!string.IsNullOrWhiteSpace(parsedLang))
                            {
                                result.Language = parsedLang;
                            }
                        }

                        // Fallback: parse title, tags and description for language codes (e.g. '[ENG / M4B]')
                        if (string.IsNullOrWhiteSpace(result.Language))
                        {
                            var probe = string.Join(" ", new[] { title, tags ?? string.Empty, description ?? string.Empty }).Trim();
                            var detectedLang = SearchResultAttributeParser.ParseLanguageFromText(probe);
                            if (!string.IsNullOrEmpty(detectedLang))
                            {
                                result.Language = detectedLang;
                            }
                        }

                        // Apply grabs/files to the result when available
                        result.Grabs = grabs;
                        result.Files = files;

                        try
                        {
                            if (_mamDebugIndex < 5)
                            {
                                logger.LogDebug("ParseMyAnonamouse: constructed SearchResult #{Index} -> Id='{Id}', Title='{Title}', Size={Size}, Seeders={Seeders}, TorrentUrl='{TorrentUrl}', Artist='{Artist}', Album='{Album}', Category='{Category}', Source='{Source}', Grabs={Grabs}, Files={Files}, PublishedDate={PublishedDate}'",
                                    _mamDebugIndex, result.Id, result.Title, result.Size, result.Seeders, result.TorrentUrl ?? "", result.Artist ?? "", result.Album ?? "", result.Category ?? "", result.Source ?? "", result.Grabs, result.Files, result.PublishedDate);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            logger.LogDebug(ex, "Failed to write debug log for constructed MyAnonamouse SearchResult");
                        }

                        _mamDebugIndex++;
                        // Final best-effort: if title lacks bracketed flags but we have a TorrentFileName with them, append the filename's suffix
                        if (!string.IsNullOrEmpty(result.TorrentFileName) && !System.Text.RegularExpressions.Regex.IsMatch(result.Title ?? string.Empty, "\\[.*\\]$"))
                        {
                            try
                            {
                                var fname = result.TorrentFileName;
                                var dotIdx2 = fname.LastIndexOf('.');
                                var nameOnly2 = dotIdx2 > 0 ? fname.Substring(0, dotIdx2) : fname;
                                var bracketStart2 = nameOnly2.IndexOf(" [");
                                if (bracketStart2 >= 0)
                                {
                                    var suffix2 = nameOnly2.Substring(bracketStart2);
                                    if (!(result.Title ?? string.Empty).Contains(suffix2))
                                    {
                                        result.Title = (result.Title ?? string.Empty) + suffix2;
                                    }
                                }
                            }
                            catch (Exception ex2) when (ex2 is not OperationCanceledException && ex2 is not OutOfMemoryException && ex2 is not StackOverflowException)
                            {
                                logger.LogDebug(ex2, "Failed to append filename flags to title for MyAnonamouse item {Id}", id);
                            }
                        }


                        results.Add(result);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogWarning(ex, "Failed to parse MyAnonamouse result item");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to parse MyAnonamouse response");
            }

            return results;
        }

    }
}
