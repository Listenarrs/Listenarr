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
using Listenarr.Application.Common;
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Common;

public sealed class ListenarrExceptionHandler(
    IHostEnvironment environment,
    ILogger<ListenarrExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var mapping = Map(exception);
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (mapping.Status >= StatusCodes.Status500InternalServerError
            && exception is not ApplicationUnavailableException)
        {
            logger.LogError(
                exception,
                "Unhandled API exception for {Method} {Path}; error code {ErrorCode}; trace {TraceId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                mapping.Code,
                traceId);
        }
        else
        {
            logger.LogWarning(
                exception,
                "API request failed for {Method} {Path}; error code {ErrorCode}; trace {TraceId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                mapping.Code,
                traceId);
        }

        httpContext.Response.StatusCode = mapping.Status;
        var problem = new ProblemDetails
        {
            Status = mapping.Status,
            Title = mapping.Title,
            Detail = mapping.Detail ?? (environment.IsDevelopment() ? exception.Message : null),
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = mapping.Code;
        problem.Extensions["traceId"] = traceId;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static ExceptionMapping Map(Exception exception) => exception switch
    {
        ApplicationValidationException ex => new(
            StatusCodes.Status400BadRequest,
            "Validation failed",
            ex.Code,
            ex.SafeDetail),
        ApplicationNotFoundException ex => new(
            StatusCodes.Status404NotFound,
            "Resource not found",
            ex.Code,
            ex.SafeDetail),
        ApplicationConflictException ex => new(
            StatusCodes.Status409Conflict,
            "Conflict",
            ex.Code,
            ex.SafeDetail),
        ApplicationForbiddenException ex => new(
            StatusCodes.Status403Forbidden,
            "Forbidden",
            ex.Code,
            ex.SafeDetail),
        ApplicationUnavailableException ex => new(
            StatusCodes.Status503ServiceUnavailable,
            "Service unavailable",
            ex.Code,
            ex.SafeDetail),
        UniqueConstraintViolationException => new(
            StatusCodes.Status409Conflict,
            "Conflict",
            "unique_constraint_violation",
            "The requested change conflicts with an existing resource."),
        PersistenceException ex => new(
            StatusCodes.Status500InternalServerError,
            "Persistence failure",
            ex.Code,
            ex.SafeDetail),
        ExternalServiceException ex => new(
            StatusCodes.Status502BadGateway,
            "External service failure",
            ex.Code,
            ex.SafeDetail),
        _ => new(
            StatusCodes.Status500InternalServerError,
            "Internal server error",
            "internal_error",
            null)
    };

    private sealed record ExceptionMapping(int Status, string Title, string Code, string? Detail);
}
