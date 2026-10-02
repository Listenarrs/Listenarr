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
using Microsoft.Extensions.Logging;

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

        private List<string>? NormalizeTriggerList(List<string>? list)
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
                    catch (JsonException exception)
                    {
                        logger.LogWarning(exception, "Stored notification trigger list is not valid JSON; keeping its raw single-element form");
                    }
                    catch (NotSupportedException exception)
                    {
                        logger.LogWarning(exception, "Stored notification trigger list could not be decoded; keeping its raw single-element form");
                    }
                }
            }

            return list;
        }
    }
}
