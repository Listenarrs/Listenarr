/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    [HttpPatch("{id}/weak-storage-policy")]
    public async Task<IActionResult> PatchWeakStoragePolicy(
        int id,
        [FromBody] RootFolderWeakStoragePolicyRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Policy) || request.ExpectedRevision < 0)
        {
            return BadRequest(new { message = "The weak-storage policy request is invalid." });
        }

        try
        {
            var updated = await _weakStoragePolicyService.UpdateAsync(
                id,
                new RootFolderWeakStoragePolicyUpdate(
                    request.Policy,
                    request.ExpectedRevision),
                cancellationToken);
            return Ok(await MapAsync(updated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Root folder not found" });
        }
        catch (RootFolderWeakStoragePolicyConflictException exception)
        {
            return Conflict(new
            {
                message = exception.Message,
                code = "root_folder_policy_revision_conflict"
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "The weak-storage policy request is invalid." });
        }
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(
        int id,
        [FromBody] RootFolderMetadataUpdateRequest request)
    {
        if (!Enum.IsDefined(request.CaseSensitivityMode))
        {
            return BadRequest(new { message = "The root folder metadata is invalid." });
        }

        var existing = await _service.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound(new { message = "Root folder not found" });
        }

        if (existing.CaseSensitivityMode != request.CaseSensitivityMode)
        {
            return Conflict(new
            {
                message = "Root filesystem semantics must be changed through the path-changes endpoint."
            });
        }

        try
        {
            existing.Name = request.Name;
            existing.IsDefault = request.IsDefault;
            var updated = await _service.UpdateAsync(existing);
            return Ok(await MapAsync(updated));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "The root folder metadata is invalid." });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The root folder metadata conflicts with an existing root folder."
            });
        }
    }
}
