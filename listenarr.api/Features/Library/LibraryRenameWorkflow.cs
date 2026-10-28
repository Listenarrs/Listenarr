/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed class LibraryRenameWorkflow(
    ILibraryFilesystemMutationGate filesystemMutationGate,
    IRenameService? renameService = null)
{
    public async Task<IActionResult> PreviewAsync(
        BulkRenameRequest request,
        CancellationToken cancellationToken)
    {
        if (renameService == null)
        {
            return Unavailable();
        }

        if (request?.AudiobookIds == null || request.AudiobookIds.Length == 0)
        {
            return new BadRequestObjectResult(new { message = "At least one audiobook ID is required" });
        }

        if (request.AudiobookIds.Length > 500)
        {
            return new BadRequestObjectResult(new { message = "Cannot preview more than 500 audiobooks at once" });
        }

        return new OkObjectResult(
            await renameService.PreviewRenameAsync(request.AudiobookIds, cancellationToken));
    }

    public async Task<IActionResult> ExecuteAsync(
        ExecuteRenameRequest request,
        CancellationToken cancellationToken)
    {
        if (renameService == null)
        {
            return Unavailable();
        }

        if (request?.Operations == null || request.Operations.Count == 0)
        {
            return new BadRequestObjectResult(new { message = "At least one rename operation is required" });
        }

        if (request.Operations.Count > 500)
        {
            return new BadRequestObjectResult(new { message = "Cannot execute more than 500 rename operations at once" });
        }

        filesystemMutationGate.EnsureReady();

        try
        {
            return new OkObjectResult(
                await renameService.ExecuteRenameAsync(request.Operations, cancellationToken));
        }
        catch (ApplicationConflictException exception)
        {
            return MoveConflict(exception);
        }
    }

    public async Task<IActionResult> PreviewSingleAsync(
        int id,
        CancellationToken cancellationToken)
    {
        if (renameService == null)
        {
            return Unavailable();
        }

        var preview = (await renameService.PreviewRenameAsync([id], cancellationToken))
            .FirstOrDefault();
        return preview == null
            ? new NotFoundObjectResult(new { message = "Audiobook not found" })
            : new OkObjectResult(preview);
    }

    public async Task<IActionResult> ExecuteSingleAsync(
        int id,
        RenameOperation operation,
        CancellationToken cancellationToken)
    {
        if (renameService == null)
        {
            return Unavailable();
        }

        if (operation == null)
        {
            return new BadRequestObjectResult(new { message = "Rename operation is required" });
        }

        operation.AudiobookId = id;
        filesystemMutationGate.EnsureReady();
        try
        {
            var result = (await renameService.ExecuteRenameAsync([operation], cancellationToken))
                .FirstOrDefault();
            return result == null
                ? new NotFoundObjectResult(new { message = "Audiobook not found" })
                : new OkObjectResult(result);
        }
        catch (ApplicationConflictException exception)
        {
            return MoveConflict(exception);
        }
    }

    private static ConflictObjectResult MoveConflict(ApplicationConflictException exception) =>
        new(new
        {
            message = exception.SafeDetail,
            code = exception.Code
        });

    private static ObjectResult Unavailable() =>
        new(new { message = "Rename service not available" })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
}
