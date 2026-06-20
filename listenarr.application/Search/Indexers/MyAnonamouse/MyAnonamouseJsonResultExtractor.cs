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
    internal static class MyAnonamouseJsonResultExtractor
    {
        public static bool TryExtractResultArray(
            string jsonResponse,
            Indexer indexer,
            ILogger logger,
            out JsonDocument? document,
            out JsonElement dataArrayElement)
        {
            document = null;
            dataArrayElement = default;

            try
            {
                document = JsonDocument.Parse(jsonResponse);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                var start = jsonResponse.IndexOf('[');
                var end = jsonResponse.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    var sub = jsonResponse.Substring(start, end - start + 1);
                    try
                    {
                        document = JsonDocument.Parse(sub);
                    }
                    catch (Exception parseEx) when (parseEx is not OperationCanceledException && parseEx is not OutOfMemoryException && parseEx is not StackOverflowException)
                    {
                        logger.LogWarning(parseEx, "Failed to parse extracted JSON array from MyAnonamouse response");
                        return false;
                    }
                }
                else
                {
                    logger.LogWarning("Unable to locate JSON array in MyAnonamouse response");
                    return false;
                }
            }

            var root = document!.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                dataArrayElement = root;
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetNamedArray(root, out dataArrayElement))
                {
                    return true;
                }

                foreach (var prop in root.EnumerateObject().Where(prop => prop.Value.ValueKind == JsonValueKind.Array))
                {
                    dataArrayElement = prop.Value;
                    break;
                }

                if (dataArrayElement.ValueKind == JsonValueKind.Undefined)
                {
                    logger.LogWarning("MyAnonamouse response did not contain an expected array property. Response preview: {Preview}", LogRedaction.RedactText(jsonResponse.Length > 500 ? jsonResponse.Substring(0, 500) + "..." : jsonResponse, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty })));
                    return false;
                }

                return true;
            }

            logger.LogWarning("Unexpected MyAnonamouse root JSON kind: {Kind}", root.ValueKind);
            return false;
        }

        private static bool TryGetNamedArray(JsonElement root, out JsonElement dataArrayElement)
        {
            if (root.TryGetProperty("data", out dataArrayElement) && dataArrayElement.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            if (root.TryGetProperty("parsed", out dataArrayElement) && dataArrayElement.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            if (root.TryGetProperty("results", out dataArrayElement) && dataArrayElement.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            if (root.TryGetProperty("items", out dataArrayElement) && dataArrayElement.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            dataArrayElement = default;
            return false;
        }
    }
}
