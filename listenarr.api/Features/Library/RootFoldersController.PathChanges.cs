using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    private static bool HasRootPathChanged(
        RootFolder existing,
        string normalizedRequestedPath)
    {
        var persistedSourceSemantics =
            RootFolderPathSemantics.ResolvePersisted(existing)?.Semantics;
        if (!persistedSourceSemantics.HasValue)
        {
            return true;
        }

        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                normalizedRequestedPath,
                out var requestedSyntax)
            || requestedSyntax != persistedSourceSemantics.Value.Syntax)
        {
            return true;
        }

        return !FileSystemPathIdentity.AreEquivalent(
            existing.Path,
            normalizedRequestedPath,
            persistedSourceSemantics.Value);
    }

    [HttpPost("{id}/path-changes")]
    public async Task<IActionResult> ChangePath(
        int id,
        [FromBody] RootFolderPathChangeRequest request,
        CancellationToken cancellationToken)
    {
        if (!RootFolderRequestValidation.TryParseRelocationMode(request.Mode, out var mode)
            || !Enum.IsDefined(request.TargetCaseSensitivityMode)
            || string.IsNullOrWhiteSpace(request.ExpectedCurrentPath))
        {
            return BadRequest(new
            {
                message = "Mode must be 'relocate' or 'metadataOnly', and case sensitivity must be valid."
            });
        }

        if (mode == RootFolderRelocationMode.Relocate)
        {
            _filesystemMutationGate.EnsureReady();
        }
        else
        {
            _filesystemReadiness.EnsureMetadataRepairReady();
        }

        try
        {
            var result = await _relocationService.StartAsync(
                id,
                new RootFolderPathChangeCommand(
                    request.TargetPath,
                    mode,
                    request.DeleteEmptySource,
                    request.DesiredName,
                    request.DesiredIsDefault,
                    request.TargetCaseSensitivityMode,
                    request.ExpectedCurrentPath),
                cancellationToken);
            var publicResult = RootFolderRelocationPublicProjection.Sanitize(result);
            return result.Status switch
            {
                RootFolderRelocationStatus.Completed => Ok(publicResult),
                RootFolderRelocationStatus.NeedsAttention
                    when mode == RootFolderRelocationMode.MetadataOnly => Ok(publicResult),
                RootFolderRelocationStatus.NeedsAttention or RootFolderRelocationStatus.Failed =>
                    Conflict(publicResult),
                _ when mode == RootFolderRelocationMode.Relocate => AcceptedAtRoute(
                    "GetRootFolderRelocation",
                    new { id = result.RelocationId },
                    publicResult),
                _ => Ok(publicResult)
            };
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Root folder not found" });
        }
        catch (RootFolderPathChangeRejectedException exception)
        {
            return RootFolderPathChangeConflict(exception);
        }
        catch (InvalidOperationException)
        {
            return RootFolderPathChangeBlocked();
        }
        catch (ArgumentException)
        {
            return BadRequest(new
            {
                message = "The root folder path change request is invalid."
            });
        }
    }
}
