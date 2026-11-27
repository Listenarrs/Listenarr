using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Move audio files from one audiobook to another existing library record —
    /// the remediation for "these files are actually a different book I track"
    /// (two books' tracks imported onto one record, or a collection being split
    /// into its real books). The transport layer only validates and maps; the
    /// ownership re-home and the organize-backed physical move live in
    /// <see cref="IAudiobookFileTransferService"/>.
    /// </summary>
    public sealed class LibraryTransferFilesWorkflow(
        ILibraryFilesystemMutationGate filesystemMutationGate,
        IAudiobookFileTransferService transferService,
        ILogger<LibraryTransferFilesWorkflow> logger)
    {
        public async Task<IActionResult> TransferAsync(
            int id,
            LibraryController.TransferFilesRequest? request,
            CancellationToken ct)
        {
            if (request == null)
            {
                return new BadRequestObjectResult(new { message = "Request body required" });
            }

            if (request.TargetAudiobookId == id)
            {
                return new BadRequestObjectResult(new { message = "Target audiobook must be different from the source" });
            }

            filesystemMutationGate.EnsureReady();

            AudiobookFileTransferResult result;
            try
            {
                result = await transferService.TransferAsync(
                    new AudiobookFileTransferRequest(id, request.TargetAudiobookId, request.FileIds),
                    ct);
            }
            catch (ApplicationConflictException exception)
            {
                return new ConflictObjectResult(new
                {
                    message = exception.SafeDetail,
                    code = exception.Code
                });
            }

            switch (result.Status)
            {
                case AudiobookFileTransferStatus.SameAudiobook:
                    return new BadRequestObjectResult(new { message = "Target audiobook must be different from the source" });
                case AudiobookFileTransferStatus.SourceNotFound:
                    return new NotFoundObjectResult(new { message = "Source audiobook not found" });
                case AudiobookFileTransferStatus.TargetNotFound:
                    return new NotFoundObjectResult(new { message = "Target audiobook not found" });
                case AudiobookFileTransferStatus.UnknownFileIds:
                    return new BadRequestObjectResult(new { message = "One or more file ids do not belong to this audiobook" });
                case AudiobookFileTransferStatus.NoMatchingFiles:
                    return new BadRequestObjectResult(new { message = "No matching files to transfer" });
            }

            var transferred = result.TransferredCount;
            var physicallyMoved = result.RelocatedCount;
            var warnings = BuildWarnings(result);
            logger.LogInformation(
                "Transferred {Count} file(s) ({Physical} moved on disk) from audiobook {SourceId} to {TargetId}",
                transferred,
                physicallyMoved,
                id,
                request.TargetAudiobookId);

            return new OkObjectResult(new
            {
                message = $"Transferred {transferred} file(s) to '{result.TargetTitle}'",
                sourceId = id,
                targetId = request.TargetAudiobookId,
                transferred,
                physicallyMoved,
                warnings
            });
        }

        private static List<string> BuildWarnings(AudiobookFileTransferResult result)
        {
            var warnings = new List<string>();
            foreach (var item in result.Items)
            {
                var name = string.IsNullOrWhiteSpace(item.Path)
                    ? $"file {item.FileId}"
                    : Path.GetFileName(item.Path);
                if (!item.Transferred)
                {
                    warnings.Add($"{name} was not transferred: {item.Reason}");
                }
                else if (!item.Relocated && !string.IsNullOrWhiteSpace(item.Reason))
                {
                    warnings.Add($"{name} now belongs to '{result.TargetTitle}' but stays in its current folder: {item.Reason}");
                }
            }

            if (!string.IsNullOrWhiteSpace(result.RelocationError))
            {
                warnings.Add($"Transferred files were not moved into '{result.TargetTitle}' folder: {result.RelocationError} They already belong to it and can be relocated with Organize.");
            }

            return warnings;
        }
    }
}
