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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Listenarr.Api.Filters;

public sealed class ServerErrorProblemDetailsFilter(IHostEnvironment environment) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(
        ResultExecutingContext context,
        ResultExecutionDelegate next)
    {
        if (context.Result is IStatusCodeActionResult statusResult &&
            statusResult.StatusCode is >= StatusCodes.Status500InternalServerError &&
            context.Result is not ObjectResult { Value: ProblemDetails })
        {
            var value = (context.Result as ObjectResult)?.Value;
            var problem = new ProblemDetails
            {
                Status = statusResult.StatusCode,
                Title = "Internal server error",
                Detail = environment.IsDevelopment() ? ReadSafeDevelopmentDetail(value) : null,
                Instance = context.HttpContext.Request.Path
            };
            problem.Extensions["code"] = "internal_error";
            problem.Extensions["traceId"] =
                Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            context.Result = new ObjectResult(problem)
            {
                StatusCode = statusResult.StatusCode,
                ContentTypes = { "application/problem+json" }
            };
        }

        await next();
    }

    private static string? ReadSafeDevelopmentDetail(object? value)
    {
        if (value is string text)
        {
            return text;
        }

        var property = value?.GetType().GetProperty("message")
            ?? value?.GetType().GetProperty("Message");
        return property?.GetValue(value)?.ToString();
    }
}
