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

namespace Listenarr.Application.Search.Indexers.MyAnonamouse
{
    internal static class MyAnonamousePublishDateParser
    {
        public static DateTime? Parse(JsonElement item, string title, ILogger logger)
        {
            // Prefer explicit 'added' timestamp when present (MyAnonamouse uses "yyyy-MM-dd HH:mm:ss")
            DateTime? publishDate = null;
            if (item.TryGetProperty("added", out var addedElem) && addedElem.ValueKind == JsonValueKind.String)
            {
                var addedStr = addedElem.GetString();
                if (!string.IsNullOrWhiteSpace(addedStr))
                {
                    try
                    {
                        publishDate = DateTime.ParseExact(addedStr, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).ToLocalTime();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        // ignore and fallback to other fields below
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                }
            }

            // Parse publish date when present; fallback to 'age' if necessary
            if (!publishDate.HasValue)
            {
                string? publishDateStr = null;
                if (item.TryGetProperty("publishDate", out var pdElem) && pdElem.ValueKind == JsonValueKind.String)
                    publishDateStr = pdElem.GetString();
                else if (item.TryGetProperty("publish_date", out var pd2) && pd2.ValueKind == JsonValueKind.String)
                    publishDateStr = pd2.GetString();
                else if (item.TryGetProperty("publishdate", out var pd3) && pd3.ValueKind == JsonValueKind.String)
                    publishDateStr = pd3.GetString();

                if (!string.IsNullOrWhiteSpace(publishDateStr))
                {
                    if (System.DateTimeOffset.TryParse(publishDateStr, out var dto))
                    {
                        publishDate = dto.UtcDateTime;
                    }
                    else if (DateTime.TryParse(publishDateStr, out var pdv))
                    {
                        publishDate = DateTime.SpecifyKind(pdv, DateTimeKind.Utc);
                    }
                }
                else
                {
                    // Support multiple representations of "age": days, hours, minutes, or alternate keys (ageHours, ageMinutes)
                    int? days = null;
                    double? hours = null;
                    double? minutes = null;

                    // Prefer explicit ageHours/ageMinutes if present
                    if (item.TryGetProperty("ageHours", out var ah) && (ah.ValueKind == JsonValueKind.Number || ah.ValueKind == JsonValueKind.String))
                    {
                        if (ah.ValueKind == JsonValueKind.Number) hours = ah.GetDouble();
                        else if (double.TryParse(ah.GetString(), out var htmp)) hours = htmp;
                    }
                    if (item.TryGetProperty("ageMinutes", out var am) && (am.ValueKind == JsonValueKind.Number || am.ValueKind == JsonValueKind.String))
                    {
                        if (am.ValueKind == JsonValueKind.Number) minutes = am.GetDouble();
                        else if (double.TryParse(am.GetString(), out var mtmp)) minutes = mtmp;
                    }

                    // Fallback to 'age' if present. Heuristic: small values (<=48) likely hours; otherwise treat as days.
                    if ((hours == null && minutes == null) && item.TryGetProperty("age", out var ageElem))
                    {
                        if (ageElem.ValueKind == JsonValueKind.Number)
                        {
                            var a = ageElem.GetDouble();
                            if (a <= 48) hours = a;
                            else days = (int)Math.Floor(a);
                        }
                        else if (ageElem.ValueKind == JsonValueKind.String && double.TryParse(ageElem.GetString(), out var adtmp))
                        {
                            var a = adtmp;
                            if (a <= 48) hours = a;
                            else days = (int)Math.Floor(a);
                        }
                    }

                    if (minutes.HasValue && minutes.Value > 0)
                        publishDate = DateTime.UtcNow.AddMinutes(-minutes.Value);
                    else if (hours.HasValue && hours.Value > 0)
                        publishDate = DateTime.UtcNow.AddHours(-hours.Value);
                    else if (days.HasValue && days.Value > 0)
                        publishDate = DateTime.UtcNow.AddDays(-days.Value);
                }
            }


            return publishDate;
        }
    }
}
