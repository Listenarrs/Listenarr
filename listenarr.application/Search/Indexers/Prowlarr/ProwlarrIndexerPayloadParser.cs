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

namespace Listenarr.Application.Search.Indexers.Prowlarr;

public static class ProwlarrIndexerPayloadParser
{
    public static HashSet<string> GetTagValues(JsonElement element, IReadOnlyDictionary<string, string>? tagMap)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (element.TryGetProperty("tags", out var rawTags))
        {
            AddTagValues(rawTags, tags, tagMap);
        }

        if (element.TryGetProperty("tagNames", out var tagNames))
        {
            AddTagValues(tagNames, tags, tagMap);
        }

        if (element.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fields.EnumerateArray())
            {
                if (!field.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var fieldName = nameProp.GetString();
                if (!string.Equals(fieldName, "tags", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fieldName, "tagNames", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (field.TryGetProperty("value", out var valueProp))
                {
                    AddTagValues(valueProp, tags, tagMap);
                }
            }
        }

        return tags;
    }

    public static bool PayloadRequiresTagMap(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Array &&
               payload.EnumerateArray().Any(ElementRequiresTagMap);
    }

    public static HashSet<int> GetCategoryIds(JsonElement element)
    {
        var categories = new HashSet<int>();

        if (element.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object &&
            caps.TryGetProperty("categories", out var catArray) && catArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var cat in catArray.EnumerateArray())
            {
                TryAddCategoryId(cat, categories);

                if (cat.ValueKind == JsonValueKind.Object &&
                    cat.TryGetProperty("subCategories", out var subCats) &&
                    subCats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sub in subCats.EnumerateArray())
                    {
                        TryAddCategoryId(sub, categories);
                    }
                }
            }
        }

        if (element.TryGetProperty("categories", out var directCategories))
        {
            AddCategoryValues(directCategories, categories);
        }

        if (element.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fields.EnumerateArray())
            {
                if (!field.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!string.Equals(nameProp.GetString(), "categories", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (field.TryGetProperty("value", out var valueProp))
                {
                    AddCategoryValues(valueProp, categories);
                }
            }
        }

        return categories;
    }

    private static void AddTagValues(JsonElement value, HashSet<string> tags, IReadOnlyDictionary<string, string>? tagMap)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                foreach (var part in value.GetString()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>())
                {
                    AddTagValue(part, tags, tagMap);
                }
                break;
            case JsonValueKind.Number:
                AddTagValue(value.ToString(), tags, tagMap);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    AddTagValues(item, tags, tagMap);
                }
                break;
            case JsonValueKind.Object:
                if (value.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                {
                    AddTagValue(labelProp.GetString(), tags, tagMap);
                }
                else if (value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    AddTagValue(nameProp.GetString(), tags, tagMap);
                }
                else if (value.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                {
                    AddTagValue(idProp.GetInt32().ToString(), tags, tagMap);
                }
                break;
        }
    }

    private static void AddTagValue(string? rawValue, HashSet<string> tags, IReadOnlyDictionary<string, string>? tagMap)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var trimmed = rawValue.Trim();
        tags.Add(trimmed);

        if (tagMap != null && tagMap.TryGetValue(trimmed, out var label) && !string.IsNullOrWhiteSpace(label))
        {
            tags.Add(label.Trim());
        }
    }

    private static bool ElementRequiresTagMap(JsonElement element)
    {
        var hasTagData = false;
        var hasTextualTagData = false;

        if (element.TryGetProperty("tags", out var rawTags))
        {
            InspectTagValue(rawTags, ref hasTagData, ref hasTextualTagData);
        }

        if (element.TryGetProperty("tagNames", out var tagNames))
        {
            InspectTagValue(tagNames, ref hasTagData, ref hasTextualTagData);
        }

        if (element.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fields.EnumerateArray())
            {
                if (!field.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var fieldName = nameProp.GetString();
                if (!string.Equals(fieldName, "tags", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fieldName, "tagNames", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (field.TryGetProperty("value", out var valueProp))
                {
                    InspectTagValue(valueProp, ref hasTagData, ref hasTextualTagData);
                }
            }
        }

        return hasTagData && !hasTextualTagData;
    }

    private static void InspectTagValue(JsonElement value, ref bool hasTagData, ref bool hasTextualTagData)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                foreach (var part in value.GetString()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>())
                {
                    InspectTagToken(part, ref hasTagData, ref hasTextualTagData);
                }
                break;
            case JsonValueKind.Number:
                hasTagData = true;
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    InspectTagValue(item, ref hasTagData, ref hasTextualTagData);
                }
                break;
            case JsonValueKind.Object:
                if (value.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                {
                    InspectTagToken(labelProp.GetString(), ref hasTagData, ref hasTextualTagData);
                }
                else if (value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    InspectTagToken(nameProp.GetString(), ref hasTagData, ref hasTextualTagData);
                }
                else if (value.TryGetProperty("id", out var idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.Number)
                    {
                        hasTagData = true;
                    }
                    else if (idProp.ValueKind == JsonValueKind.String)
                    {
                        InspectTagToken(idProp.GetString(), ref hasTagData, ref hasTextualTagData);
                    }
                }
                break;
        }
    }

    private static void InspectTagToken(string? rawValue, ref bool hasTagData, ref bool hasTextualTagData)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        hasTagData = true;
        if (!long.TryParse(rawValue.Trim(), out _))
        {
            hasTextualTagData = true;
        }
    }

    private static void AddCategoryValues(JsonElement value, HashSet<int> categories)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in value.EnumerateArray())
            {
                TryAddCategoryId(v, categories);
            }
        }
        else
        {
            TryAddCategoryId(value, categories);
        }
    }

    private static void TryAddCategoryId(JsonElement element, HashSet<int> categories)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var idProp))
        {
            TryAddCategoryId(idProp, categories);
            return;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var id))
        {
            categories.Add(id);
            return;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
        {
            categories.Add(parsed);
        }
    }
}
