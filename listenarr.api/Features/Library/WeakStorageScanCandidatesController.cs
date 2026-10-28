using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Features.Library;

public sealed record ConfirmWeakStorageMissingFilesRequest(
    Guid ScanToken,
    IReadOnlyCollection<Guid> CandidateIds);

[ApiController]
[Route("api/v{version:apiVersion}/library/{audiobookId:int}/weak-storage-missing-files")]
[Tags("Library")]
public sealed class WeakStorageScanCandidatesController(
    IWeakStorageScanCandidateStore candidateStore) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        int audiobookId,
        CancellationToken cancellationToken)
    {
        var candidates = await candidateStore.GetPendingAsync(
            audiobookId,
            cancellationToken);
        return Ok(new
        {
            scanToken = candidates.FirstOrDefault()?.ScanToken,
            expiresAt = candidates.FirstOrDefault()?.ExpiresAt,
            items = candidates.Select(candidate => new
            {
                candidate.Id,
                candidate.AudiobookFileId,
                path = candidate.ExpectedResolvedPath
            })
        });
    }

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(
        int audiobookId,
        [FromBody] ConfirmWeakStorageMissingFilesRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ScanToken == Guid.Empty
            || request.CandidateIds == null
            || request.CandidateIds.Count == 0)
        {
            return BadRequest(new { message = "Select at least one current scan candidate." });
        }

        try
        {
            return Ok(await candidateStore.ConfirmAsync(
                audiobookId,
                request.ScanToken,
                request.CandidateIds,
                cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "The missing-file scan is stale. Scan the audiobook again before removing records.",
                code = "weak_storage_scan_candidates_stale"
            });
        }
    }
}
