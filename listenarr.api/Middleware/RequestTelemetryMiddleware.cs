/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Listenarr.Api.Middleware;

public sealed class RequestTelemetryMiddleware(
    RequestDelegate next,
    ILogger<RequestTelemetryMiddleware> logger)
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private static readonly Meter Meter = new("Listenarr.Api");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("http.server.request.duration", "ms");
    private static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("http.server.request.errors");

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationHeader] = correlationId;
        Activity.Current?.SetTag("listenarr.correlation_id", correlationId);

        var started = Stopwatch.GetTimestamp();
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["RequestMethod"] = context.Request.Method,
            ["RequestPath"] = context.Request.Path.Value
        });
        try
        {
            await next(context);
        }
        finally
        {
            var tags = new TagList
            {
                { "http.request.method", context.Request.Method },
                { "http.response.status_code", context.Response.StatusCode }
            };
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
            if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                Errors.Add(1, tags);
            }
        }
    }

    private static string GetCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers[CorrelationHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(supplied) &&
            supplied.Length <= 128 &&
            supplied.All(IsCorrelationCharacter))
        {
            return supplied;
        }

        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    }

    private static bool IsCorrelationCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.';
}
