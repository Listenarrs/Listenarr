using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

[ApiController]
[Route("api/v{version:apiVersion}/file-registration-recovery")]
[Tags("Library")]
public sealed class FileRegistrationRecoveryController(
    IFileRegistrationRecoveryService recoveryService,
    IFilesystemMutationCoordinator mutationCoordinator) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<FileRegistrationReview>> List(CancellationToken cancellationToken) =>
        recoveryService.ListReviewsAsync(cancellationToken);

    [HttpGet("{operationId:guid}")]
    public async Task<IActionResult> Inspect(Guid operationId, CancellationToken cancellationToken)
    {
        try { return Ok(await recoveryService.InspectAsync(operationId, cancellationToken)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException) { return Conflict(new { code = "registration_review_ineligible" }); }
    }

    public sealed record ResolveRequest(string ReviewToken, string Note, bool AcknowledgeIncompleteContent);

    [HttpPost("{operationId:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid operationId, ResolveRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.Identity?.Name ?? (User.Identity?.IsAuthenticated == true ? "Authenticated operator" : "Local operator (authentication disabled)");
        try
        {
            return Ok(await mutationCoordinator.ExecuteExclusiveAsync(token =>
                recoveryService.ResolveAsync(operationId, request.ReviewToken, request.Note,
                    request.AcknowledgeIncompleteContent, actor, token), cancellationToken));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { code = "registration_review_invalid", message = "A review token, note and incomplete-content acknowledgement are required." }); }
        catch (InvalidOperationException) { return Conflict(new { code = "registration_review_ineligible" }); }
    }

    [HttpPost("{operationId:guid}/retry")]
    public async Task<IActionResult> Retry(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await mutationCoordinator.ExecuteExclusiveAsync(
                token => recoveryService.RetryAsync(operationId, token),
                cancellationToken);
            return status.CanRetry
                || status.Disposition
                    == FileRegistrationRecoveryDisposition.RequiresOperatorAttention
                ? Conflict(status)
                : Ok(status);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new
            {
                code = "registration_recovery_not_found",
                message = "File-registration recovery operation not found."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new
            {
                code = "registration_recovery_invalid",
                message = "The file-registration recovery operation is invalid."
            });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                code = "registration_recovery_not_retryable",
                message = "The requested operation is not eligible for file-registration recovery."
            });
        }
    }
}
