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

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Listenarr.Api.Features.Configuration
{
    /// <summary>
    /// Overlays a posted startup-config body onto the configuration the server currently holds (loaded from config.json).
    /// </summary>
    /// <remarks>
    /// The endpoint used to bind <see cref="StartupConfig"/> directly and hand the bound
    /// object to the writer, so any field the caller left out was written back as null and
    /// lost: the API key, the bind address, the port, the SSL settings. Binding the raw
    /// JSON instead is what makes "absent" distinguishable from "sent as null" or "sent as
    /// an empty string", which a bound object cannot express.
    ///
    /// Properties are walked by reflection rather than listed here so that a field added to
    /// <see cref="StartupConfig"/> is merged without anyone remembering to update this file.
    /// </remarks>
    public static class StartupConfigPatchReader
    {
        // The same defaults MVC binds request bodies with, so a value the bound object accepted
        // (a quoted number, for one) is accepted here too.
        private static readonly JsonSerializerOptions ValueOptions = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// Produce a new configuration that is <paramref name="existing"/> with every
        /// property present in <paramref name="patch"/> overlaid onto it.
        /// </summary>
        /// <param name="existing">The configuration the server currently holds. Null is treated as empty.</param>
        /// <param name="patch">A JSON object holding some subset of the startup-config properties.</param>
        /// <returns>A new instance; <paramref name="existing"/> is not mutated.</returns>
        /// <exception cref="ArgumentException">The patch is not a JSON object.</exception>
        public static StartupConfig ApplyTo(StartupConfig? existing, JsonElement patch)
        {
            if (patch.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Startup configuration body must be a JSON object.", nameof(patch));
            }

            var merged = Clone(existing) ?? new StartupConfig();
            ApplyToInstance(merged, patch);
            return merged;
        }

        private static void ApplyToInstance(object target, JsonElement patch)
        {
            foreach (var property in GetMergeableProperties(target.GetType()))
            {
                if (!TryGetProperty(patch, JsonNameOf(property), out var value))
                {
                    continue;
                }

                // A nested object is merged in turn, so posting { "ffmpeg": { "arch": "arm64" } }
                // keeps the provider that is already configured instead of blanking it.
                if (value.ValueKind == JsonValueKind.Object && IsMergeableComplexType(property.PropertyType))
                {
                    var nested = property.GetValue(target);
                    if (nested != null)
                    {
                        ApplyToInstance(nested, value);
                        continue;
                    }
                }

                property.SetValue(target, value.Deserialize(property.PropertyType, ValueOptions));
            }
        }

        private static IEnumerable<PropertyInfo> GetMergeableProperties(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

        private static bool IsMergeableComplexType(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsClass
                   && underlying != typeof(string)
                   && !underlying.IsArray
                   && !typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying);
        }

        private static string JsonNameOf(PropertyInfo property)
            => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }

            // The SPA posts camelCase while the serialized file is PascalCase, and the
            // reader that loads config.json is already case-insensitive. Match that.
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static StartupConfig? Clone(StartupConfig? source)
            => source == null
                ? null
                : JsonSerializer.Deserialize<StartupConfig>(JsonSerializer.Serialize(source), ValueOptions);
    }
}
