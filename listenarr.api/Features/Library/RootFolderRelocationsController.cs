using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

[ApiController]
[Route("api/v{version:apiVersion}/rootfolder-relocations")]
[Tags("Root Folder Relocations")]
public sealed class RootFolderRelocationsController(
    IRootFolderRelocationService relocationService,
    ILibraryFilesystemReadiness filesystemReadiness,
    ILibraryFilesystemMutationGate filesystemMutationGate)
    : ControllerBase
{
    [HttpGet("{id:guid}", Name = "GetRootFolderRelocation")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await relocationService.GetAsync(id, cancellationToken);
        return result == null
            ? NotFound(new { message = "Root folder relocation not found" })
            : Ok(RootFolderRelocationPublicProjection.Sanitize(result));
    }

    [HttpGet("{id:guid}/skipped/{audiobookId:int}")]
    public async Task<IActionResult> GetSkippedMetadataRepair(
        Guid id,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        try
        {
            var details = await relocationService.GetSkippedMetadataRepairDetailsAsync(
                id,
                audiobookId,
                cancellationToken);
            return details == null
                ? NotFound(new { message = "Skipped audiobook repair state not found" })
                : Ok(details);
        }
        catch (ApplicationConflictException exception)
        {
            return Conflict(new
            {
                message = exception.SafeDetail,
                code = exception.Code
            });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The audiobook path repair details are no longer available in their current state."
            });
        }
    }

    [HttpDelete("{id:guid}/skipped/{audiobookId:int}/files/{audiobookFileId:int}")]
    public async Task<IActionResult> RemoveSkippedMetadataRepairFile(
        Guid id,
        int audiobookId,
        int audiobookFileId,
        CancellationToken cancellationToken)
    {
        filesystemReadiness.EnsureMetadataRepairReady();
        try
        {
            return Ok(await relocationService.RemoveSkippedMetadataRepairFileAsync(
                id,
                audiobookId,
                audiobookFileId,
                cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Tracked file repair state not found" });
        }
        catch (ApplicationConflictException exception)
        {
            return Conflict(new
            {
                message = exception.SafeDetail,
                code = exception.Code
            });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The tracked file record cannot be repaired in its current state."
            });
        }
    }

    [HttpPost("{id:guid}/abandon-unpublished")]
    public async Task<IActionResult> AbandonUnpublished(
        Guid id,
        CancellationToken cancellationToken)
    {
        filesystemMutationGate.EnsureReady();
        try
        {
            return Ok(RootFolderRelocationPublicProjection.Sanitize(
                await relocationService.AbandonUnpublishedAsync(
                    id,
                    cancellationToken)));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Root folder relocation not found" });
        }
        catch (ApplicationConflictException exception)
        {
            return Conflict(new
            {
                message = exception.SafeDetail,
                code = exception.Code
            });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The unfinished relocation cannot be abandoned safely in its current state."
            });
        }
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var current = await relocationService.GetAsync(id, cancellationToken);
            if (current == null)
            {
                return NotFound(new { message = "Root folder relocation not found" });
            }
            if (current.Mode == RootFolderRelocationMode.Relocate)
            {
                filesystemMutationGate.EnsureReady();
            }
            else
            {
                filesystemReadiness.EnsureMetadataRepairReady();
            }

            return Ok(RootFolderRelocationPublicProjection.Sanitize(
                await relocationService.RetryAsync(id, cancellationToken)));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Root folder relocation not found" });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The relocation cannot be retried in its current state."
            });
        }
    }

}
