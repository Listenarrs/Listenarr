/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Move audio files from one audiobook to another existing library record —
    /// the remediation for "these files are actually a different book I track"
    /// (e.g. two books' tracks imported onto one record, or a collection being
    /// split into its real books). DB ownership is reassigned always; the
    /// physical file is moved into the target's folder best-effort (failures
    /// leave it in place with a warning — the Organize tool can relocate it
    /// later).
    /// </summary>
    public sealed class LibraryTransferFilesWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IHistoryRepository _historyRepository;
        private readonly IFileMover _fileMover;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryTransferFilesWorkflow> _logger;

        public LibraryTransferFilesWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IHistoryRepository historyRepository,
            IFileMover fileMover,
            IFileSystem fileSystem,
            ILogger<LibraryTransferFilesWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _historyRepository = historyRepository;
            _fileMover = fileMover;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<IActionResult> TransferAsync(int id, LibraryController.TransferFilesRequest? request, CancellationToken ct)
        {
            if (request == null)
            {
                return new BadRequestObjectResult(new { message = "Request body required" });
            }

            if (request.TargetAudiobookId == id)
            {
                return new BadRequestObjectResult(new { message = "Target audiobook must be different from the source" });
            }

            var source = await _repo.GetByIdAsync(id);
            if (source == null)
            {
                return new NotFoundObjectResult(new { message = "Source audiobook not found" });
            }

            var target = await _repo.GetByIdAsync(request.TargetAudiobookId);
            if (target == null)
            {
                return new NotFoundObjectResult(new { message = "Target audiobook not found" });
            }

            var sourceFiles = await _audioFileRepository.GetByAudiobookIdAsync(id, ct);
            var toMove = request.FileIds is { Count: > 0 }
                ? sourceFiles.Where(f => request.FileIds.Contains(f.Id)).ToList()
                : sourceFiles.ToList();
            if (toMove.Count == 0)
            {
                return new BadRequestObjectResult(new { message = "No matching files to transfer" });
            }

            if (request.FileIds is { Count: > 0 } && toMove.Count != request.FileIds.Distinct().Count())
            {
                return new BadRequestObjectResult(new { message = "One or more file ids do not belong to this audiobook" });
            }

            var targetFiles = await _audioFileRepository.GetByAudiobookIdAsync(target.Id, ct);

            var warnings = new List<string>();
            var physicallyMoved = 0;
            var reassigned = 0;

            foreach (var file in toMove)
            {
                // Determine the path this file would occupy under the target, and bail BEFORE
                // touching disk if the target already owns a row there — a duplicate from a prior
                // (partial) transfer, or the same physical file dual-referenced by both records.
                // Reassigning would violate the (AudiobookId, Path) unique key; moving first and
                // only then discovering the collision relocates the file on disk but orphans the
                // source's DB row (the file leaves but never reassigns). Skip it entirely so the
                // rest of the transfer still goes through; the user can delete the redundant copy
                // from the source if it's a true duplicate.
                var candidatePath = (!string.IsNullOrWhiteSpace(target.BasePath) && !string.IsNullOrWhiteSpace(file.Path))
                    ? Path.Join(target.BasePath, Path.GetFileName(file.Path))
                    : file.Path;
                if (!string.IsNullOrWhiteSpace(candidatePath)
                    && targetFiles.Any(tf => tf.Id != file.Id && string.Equals(tf.Path, candidatePath, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add($"{Path.GetFileName(candidatePath)} already exists under the target — left in place (delete the redundant copy if it's a duplicate)");
                    continue;
                }

                // Physical relocation is best-effort: ownership (the DB row) is the
                // core semantic, and a file left in the old folder is fixable via
                // the Organize tool. A failed disk move must not abort the transfer.
                var newPath = file.Path;
                if (!string.IsNullOrWhiteSpace(target.BasePath) && !string.IsNullOrWhiteSpace(file.Path))
                {
                    try
                    {
                        var destination = Path.Join(target.BasePath, Path.GetFileName(file.Path));
                        if (!_fileSystem.FileExists(file.Path))
                        {
                            warnings.Add($"File missing on disk, reassigned in place: {Path.GetFileName(file.Path)}");
                        }
                        else if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
                        {
                            // Already where it belongs.
                        }
                        else if (_fileSystem.FileExists(destination))
                        {
                            warnings.Add($"Target folder already has {Path.GetFileName(file.Path)} — file left in place");
                        }
                        else
                        {
                            if (!_fileSystem.DirectoryExists(target.BasePath))
                            {
                                _fileSystem.CreateDirectory(target.BasePath);
                            }

                            if (await _fileMover.PerformActionOn(FileAction.Move, file.Path, destination))
                            {
                                newPath = destination;
                                physicallyMoved++;
                            }
                            else
                            {
                                warnings.Add($"Could not move {Path.GetFileName(file.Path)} on disk — file left in place");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        warnings.Add($"Could not move {Path.GetFileName(file.Path)} on disk — file left in place");
                        _logger.LogWarning(ex, "transfer-files: disk move failed for file {FileId}", file.Id);
                    }
                }

                try
                {
                    await _audioFileRepository.ReassignAsync(file.Id, target.Id, newPath, ct);
                    // Mutate the in-memory row only after the DB update succeeds, so a caught
                    // failure can't leave a stale path in the collision checks below.
                    file.AudiobookId = target.Id;
                    file.Path = newPath;
                    reassigned++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // Last-resort guard: any per-file DB failure (e.g. a same-name collision within
                    // this batch) becomes a warning, never a 500 that aborts the whole transfer.
                    warnings.Add($"Could not reassign {Path.GetFileName(newPath)} — left in place");
                    _logger.LogWarning(ex, "transfer-files: DB reassign failed for file {FileId}", file.Id);
                }
            }

            // Source bookkeeping: when its audio is gone, the legacy single-file
            // columns describe content it no longer owns. Gate on what actually
            // reassigned — a skipped/collided file means the source still holds audio.
            if (reassigned > 0 && reassigned == sourceFiles.Count)
            {
                source.FilePath = null;
                source.FileSize = null;
                await _repo.UpdateAsync(source);
            }

            try
            {
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = source.Id,
                    AudiobookTitle = source.Title ?? "Unknown Title",
                    EventType = "Files Transferred",
                    Message = $"Moved {reassigned} file(s) to '{target.Title}' (id {target.Id}).",
                    Source = "transfer-files",
                    Timestamp = DateTime.UtcNow
                }, ct);
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = target.Id,
                    AudiobookTitle = target.Title ?? "Unknown Title",
                    EventType = "Files Received",
                    Message = $"Received {reassigned} file(s) from '{source.Title}' (id {source.Id}).",
                    Source = "transfer-files",
                    Timestamp = DateTime.UtcNow
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "transfer-files: failed to record history (non-critical)");
            }

            // Post-transfer hook point: when content verification lands, this is where
            // the target gets re-verified against its new audio (and stale verdicts on
            // both records get reset).

            _logger.LogInformation(
                "Transferred {Count} file(s) ({Physical} moved on disk) from audiobook {SourceId} to {TargetId}",
                reassigned, physicallyMoved, source.Id, target.Id);

            return new OkObjectResult(new
            {
                message = $"Transferred {reassigned} file(s) to '{target.Title}'",
                sourceId = source.Id,
                targetId = target.Id,
                transferred = reassigned,
                physicallyMoved,
                warnings
            });
        }
    }
}
