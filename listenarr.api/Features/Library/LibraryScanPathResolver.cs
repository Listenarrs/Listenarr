/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed class LibraryScanPathResolver(
    IScanPathAuthorizationService authorizationService,
    ILogger<LibraryScanPathResolver> logger)
{
    public async Task<LibraryScanPathResolution> ResolveAsync(
        Audiobook audiobook,
        string? requestedPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        var explicitRequest = !string.IsNullOrWhiteSpace(requestedPath);
        var preferredPath = explicitRequest
            ? requestedPath
            : audiobook.BasePath;
        ScanPathAuthorizationResult authorization;
        try
        {
            authorization = explicitRequest
                ? await authorizationService.AuthorizeAsync(
                    requestedPath!,
                    cancellationToken)
                : await authorizationService.ResolveDefaultAsync(
                    preferredPath,
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            logger.LogWarning(
                exception,
                "Failed to authorize a scan path for audiobook {AudiobookId}",
                audiobook.Id);
            return LibraryScanPathResolution.Failure(new ObjectResult(new
            {
                message = "Failed to determine a safe scan path"
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            });
        }

        if (!authorization.IsAuthorized)
        {
            logger.LogWarning(
                "Rejected scan path for audiobook {AudiobookId}: {Reason}",
                audiobook.Id,
                LogRedaction.SanitizeText(authorization.Error));
            return LibraryScanPathResolution.Failure(
                MapFailure(authorization, explicitRequest));
        }

        return LibraryScanPathResolution.Success(
            authorization.Path!,
            authorization.Identity!.Value,
            authorization.PhysicalIdentity!.Value);
    }

    private static IActionResult MapFailure(
        ScanPathAuthorizationResult authorization,
        bool explicitRequest)
    {
        var statusCode = authorization.Failure switch
        {
            ScanPathAuthorizationFailure.ConfigurationUnavailable =>
                StatusCodes.Status500InternalServerError,
            ScanPathAuthorizationFailure.IdentityUnavailable =>
                StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        var message = authorization.Failure switch
        {
            ScanPathAuthorizationFailure.OutsideConfiguredRoots when explicitRequest =>
                "Requested scan path is not within configured root folders",
            ScanPathAuthorizationFailure.OutsideConfiguredRoots =>
                "Audiobook BasePath is not within configured root folders",
            ScanPathAuthorizationFailure.NoConfiguredRoots when explicitRequest =>
                "No root folders configured; cannot accept explicit scan path",
            ScanPathAuthorizationFailure.ConfigurationUnavailable =>
                "Failed to determine a safe scan path",
            ScanPathAuthorizationFailure.IdentityUnavailable =>
                "Scan path identity could not be established safely",
            ScanPathAuthorizationFailure.InvalidPath =>
                "Scan path is invalid",
            ScanPathAuthorizationFailure.NoConfiguredRoots =>
                "No configured scan path is available",
            _ => "Scan path authorization failed"
        };
        var payload = new
        {
            message,
            reason = authorization.Failure.ToString()
        };
        return statusCode == StatusCodes.Status400BadRequest
            ? new BadRequestObjectResult(payload)
            : new ObjectResult(payload)
            {
                StatusCode = statusCode
            };
    }
}

public sealed record LibraryScanPathResolution(
    string? ScanRoot,
    PathIdentitySnapshot? PathIdentity,
    ScanPathPhysicalIdentity? PhysicalIdentity,
    IActionResult? ErrorResult)
{
    public static LibraryScanPathResolution Success(
        string scanRoot,
        PathIdentitySnapshot pathIdentity,
        ScanPathPhysicalIdentity physicalIdentity) =>
        new(scanRoot, pathIdentity, physicalIdentity, null);

    public static LibraryScanPathResolution Failure(IActionResult errorResult) =>
        new(null, null, null, errorResult);
}
