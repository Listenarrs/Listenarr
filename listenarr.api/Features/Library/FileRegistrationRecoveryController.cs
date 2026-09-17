using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

[ApiController]
[Route("api/v{version:apiVersion}/file-registration-recovery")]
[Tags("Library")]
public sealed class FileRegistrationRecoveryController(
    IFileRegistrationRecoveryService recoveryService,
    IFilesystemMutationCoordinator mutationCoordinator) : ControllerBase
{
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
