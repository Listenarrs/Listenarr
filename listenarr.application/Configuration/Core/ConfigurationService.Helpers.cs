/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;

namespace Listenarr.Application.Configuration.Core
{
    public partial class ConfigurationService
    {
        private static void ApplyRuntimeDefaults(ApplicationSettings settings)
        {
            settings.ImportBlacklistExtensions ??= [];
            settings.EnabledNotificationTriggers ??= [];
            settings.Webhooks ??= [];
        }

        private static List<string>? NormalizeTriggerList(List<string>? list)
        {
            if (list == null) return null;
            if (list.Count == 1)
            {
                var first = list[0];
                if (!string.IsNullOrWhiteSpace(first) && first.TrimStart().StartsWith("["))
                {
                    try
                    {
                        var decoded = JsonSerializer.Deserialize<List<string>>(first);
                        if (decoded != null && decoded.Count > 0) return decoded;
                    }
                    catch (JsonException)
                    {
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                    catch (NotSupportedException)
                    {
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                }
            }

            return list;
        }
    }
}
