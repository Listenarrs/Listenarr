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

using Listenarr.Application.Audiobooks.Organizing;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Library-wide "Organize" sweep: bucket every audiobook against the
    /// canonical path its metadata computes to (preview, read-only), then queue
    /// per-book background moves for the rows the operator confirmed (apply).
    /// A separate flatten action (see the Helpers partial) resolves the
    /// "nested one level too deep" rows the move queue refuses.
    /// </summary>
    public sealed partial class LibraryOrganizeSweepWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IHistoryRepository _historyRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigurationService _configurationService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IOrganizeFilesystem _organizeFilesystem;
        private readonly ILogger<LibraryOrganizeSweepWorkflow> _logger;

        public LibraryOrganizeSweepWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IHistoryRepository historyRepository,
            IRootFolderService rootFolderService,
            IConfigurationService configurationService,
            IFileNamingService fileNamingService,
            IOrganizeFilesystem organizeFilesystem,
            ILogger<LibraryOrganizeSweepWorkflow> logger,
            IMoveQueueService? moveQueueService = null)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _historyRepository = historyRepository;
            _rootFolderService = rootFolderService;
            _configurationService = configurationService;
            _fileNamingService = fileNamingService;
            _moveQueueService = moveQueueService;
            _organizeFilesystem = organizeFilesystem;
            _logger = logger;
        }

        /// <summary>
        /// Walk every audiobook and bucket it into one of:
        /// <c>already_canonical</c>, <c>will_move</c>, <c>collision</c>, or
        /// <c>invalid_target</c>. Read-only — no DB or filesystem changes.
        /// </summary>
        public async Task<IActionResult> PreviewAsync(CancellationToken ct)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var rootFolders = await _rootFolderService.GetAllAsync();

            var allAudiobooks = await _repo.GetAllAsync();
            var allFiles = await _audioFileRepository.GetAllAsync(ct);
            var filesByAudiobookId = allFiles
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // First pass: compute target per audiobook + initial bucket
            // (invalid_target vs proposed). Collision detection happens after
            // because it needs the full target map.
            var rows = new List<OrganizePreviewRowDto>(allAudiobooks.Count);
            var targetGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var audiobook in allAudiobooks)
            {
                ct.ThrowIfCancellationRequested();
                var files = filesByAudiobookId.TryGetValue(audiobook.Id, out var fs) ? fs : new List<AudiobookFile>();

                // Rows with no tracked files have nothing to organize — whatever
                // BasePath says, there's no on-disk content to move. Letting them
                // through puts monitored-but-not-downloaded records in will_move,
                // where each queued job then fails the source-path-exists check.
                if (files.Count == 0)
                {
                    continue;
                }

                var currentPath = NormalizeOrganizePath(audiobook.BasePath);
                var row = new OrganizePreviewRowDto
                {
                    Id = audiobook.Id,
                    Title = audiobook.Title,
                    Author = audiobook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)),
                    CurrentPath = currentPath,
                    FileCount = files.Count,
                    TotalSize = files.Sum(f => f.Size ?? 0L),
                };

                // Source-at-root guard: rows whose BasePath equals a configured
                // root folder (e.g. "/audiobooks") can't be moved — the post-
                // copy delete of the source would obliterate every other
                // audiobook on that root.
                if (IsSourceAtRootFolder(currentPath, rootFolders))
                {
                    row.Status = OrganizePreviewStatus.InvalidTarget;
                    row.ReasonCode = OrganizeInvalidReasonCode.SourceAtRoot;
                    row.Reason = "Source path is the library root folder. Re-scan the library so this audiobook's BasePath points at its actual subfolder before organizing.";
                    rows.Add(row);
                    continue;
                }

                var (target, invalidReason, invalidReasonCode) = ComputeOrganizeTarget(audiobook, settings, rootFolders);
                if (!string.IsNullOrEmpty(invalidReason))
                {
                    row.Status = OrganizePreviewStatus.InvalidTarget;
                    row.Reason = invalidReason;
                    row.ReasonCode = invalidReasonCode;
                    rows.Add(row);
                    continue;
                }

                // Apply-feasibility checks. The move queue refuses these at
                // execute time; surfacing them here keeps un-moveable rows
                // out of the will_move bucket so the user isn't told an
                // operation is queued and then watches it fail one by one.
                // Skip these for the canonical-equality case — if target
                // equals current, target-exists-with-content is just the
                // audiobook's own files, not a conflict.
                if (!IsCurrentPathEqualToTarget(currentPath, target))
                {
                    if (IsTargetAncestorOfSource(currentPath, target))
                    {
                        row.Status = OrganizePreviewStatus.InvalidTarget;
                        row.TargetPath = target;
                        // Disk-check the flatten so the preview never offers a
                        // one-click flatten the executor would refuse, and so an
                        // orphaned record (folder gone from disk) is surfaced as
                        // its own "files missing" case rather than a flatten-able
                        // nested row.
                        var (feasibility, _) = _organizeFilesystem.EvaluateFlatten(currentPath, target);
                        if (feasibility == FlattenFeasibility.SourceMissing)
                        {
                            row.ReasonCode = OrganizeInvalidReasonCode.SourceMissing;
                            row.Reason = "The record's folder no longer exists on disk — its files are gone. Re-scan the library to clear it, or remove the record.";
                        }
                        else
                        {
                            row.ReasonCode = OrganizeInvalidReasonCode.TargetAncestor;
                            row.Reason = "Target is an ancestor of source; the move would flatten the source into one of its own parent directories. Adjust the Folder Naming Pattern or relocate the source manually.";
                            row.CanFlatten = feasibility == FlattenFeasibility.Ok;
                        }
                        rows.Add(row);
                        continue;
                    }
                    if (TargetExistsWithContent(target))
                    {
                        // A populated target is only a hard conflict when it
                        // holds something worth protecting. A metadata-only husk
                        // (covers, .opf, playlists — leftovers from a removed
                        // release) that nothing in the DB references can be
                        // replaced by the move; surface it as will_move with a
                        // flag instead of making the operator delete it by hand.
                        if (IsReplaceableStubTarget(target, audiobook.Id, allAudiobooks, allFiles))
                        {
                            row.ReplacesStubTarget = true;
                        }
                        else
                        {
                            row.Status = OrganizePreviewStatus.InvalidTarget;
                            row.ReasonCode = OrganizeInvalidReasonCode.TargetExists;
                            row.Reason = "Target directory already exists on disk and contains files. Resolve the existing content (move or delete) before organizing this row.";
                            row.TargetPath = target;
                            rows.Add(row);
                            continue;
                        }
                    }
                }

                row.TargetPath = target;
                var key = NormalizeOrganizeKey(target);
                if (!targetGroups.TryGetValue(key, out var members))
                {
                    members = new List<int>();
                    targetGroups[key] = members;
                }
                members.Add(audiobook.Id);
                rows.Add(row);
            }

            // Second pass: assign bucket from target groups.
            var collisionKeys = targetGroups
                .Where(kv => kv.Value.Count > 1)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row.Status == OrganizePreviewStatus.InvalidTarget) continue;
                var key = NormalizeOrganizeKey(row.TargetPath ?? string.Empty);
                if (collisionKeys.ContainsKey(key))
                {
                    row.Status = OrganizePreviewStatus.Collision;
                    row.CollisionKey = key;
                    continue;
                }
                row.Status = NormalizeOrganizeKey(row.CurrentPath ?? string.Empty) == key
                    ? OrganizePreviewStatus.AlreadyCanonical
                    : OrganizePreviewStatus.WillMove;
            }

            var preview = new OrganizeLibraryPreviewDto
            {
                Rows = rows.OrderBy(r => r.Status switch
                    {
                        OrganizePreviewStatus.WillMove => 0,
                        OrganizePreviewStatus.Collision => 1,
                        OrganizePreviewStatus.InvalidTarget => 2,
                        _ => 3,
                    }).ThenBy(r => r.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                AlreadyCanonicalCount = rows.Count(r => r.Status == OrganizePreviewStatus.AlreadyCanonical),
                WillMoveCount = rows.Count(r => r.Status == OrganizePreviewStatus.WillMove),
                CollisionCount = rows.Count(r => r.Status == OrganizePreviewStatus.Collision),
                InvalidTargetCount = rows.Count(r => r.Status == OrganizePreviewStatus.InvalidTarget),
            };
            return new OkObjectResult(preview);
        }

        /// <summary>
        /// Queue a per-book move for each id the user confirmed in the
        /// preview. Each id is re-validated against the live DB state before
        /// queuing: ids that no longer compute to <c>will_move</c>, or that
        /// collide with another id in the same request, are skipped with a
        /// warning rather than aborting the rest. Missing ids return
        /// BadRequest without queuing anything.
        /// </summary>
        public async Task<IActionResult> ApplyAsync(OrganizeLibraryApplyRequest? request, CancellationToken ct)
        {
            if (request?.AudiobookIds == null || request.AudiobookIds.Count == 0)
            {
                return new BadRequestObjectResult(new { message = "No audiobook ids provided" });
            }

            if (_moveQueueService == null)
            {
                return new ObjectResult(new { message = "Move queue not available" })
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                };
            }

            var requestedIds = request.AudiobookIds.Distinct().ToList();
            var audiobooks = await _repo.GetByIdsWithFilesAsync(requestedIds, ct);
            var foundIds = audiobooks.Select(a => a.Id).ToHashSet();
            var missing = requestedIds.Where(id => !foundIds.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                return new BadRequestObjectResult(new
                {
                    message = $"Audiobook id(s) not found: {string.Join(", ", missing)}",
                    missingIds = missing,
                });
            }

            var settings = await _configurationService.GetApplicationSettingsAsync();
            var rootFolders = await _rootFolderService.GetAllAsync();

            // Re-compute targets for the selected set so the collision check
            // reflects the live world, not the user's snapshot.
            var targets = new Dictionary<int, string>();
            var result = new OrganizeLibraryApplyResultDto();
            var byKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            // Full library state for the stub-target reference checks below —
            // a target counts as a replaceable stub only if NOTHING in the DB
            // points at it, which requires looking beyond the requested ids.
            var allAudiobooksForStubCheck = await _repo.GetAllAsync();
            var allFilesForStubCheck = await _audioFileRepository.GetAllAsync(ct);
            var replaceStubIds = new HashSet<int>();

            foreach (var audiobook in audiobooks)
            {
                // Defense in depth: even if the preview gate missed this or
                // the operator's snapshot is stale, refuse to queue a move
                // whose source is the library root (would wipe everything on
                // delete-source).
                if (IsSourceAtRootFolder(audiobook.BasePath, rootFolders))
                {
                    result.Skipped++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto
                    {
                        AudiobookId = audiobook.Id,
                        Reason = "Source path is the library root folder; re-scan to correct BasePath before organizing.",
                    });
                    continue;
                }

                var (target, invalidReason, _) = ComputeOrganizeTarget(audiobook, settings, rootFolders);
                if (!string.IsNullOrEmpty(invalidReason))
                {
                    result.Skipped++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto { AudiobookId = audiobook.Id, Reason = invalidReason });
                    continue;
                }

                var currentKey = NormalizeOrganizeKey(NormalizeOrganizePath(audiobook.BasePath));
                var targetKey = NormalizeOrganizeKey(target);
                if (currentKey == targetKey)
                {
                    result.Skipped++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto
                    {
                        AudiobookId = audiobook.Id,
                        Reason = "Already at canonical path",
                    });
                    continue;
                }

                // Mirror the preview's populated-target handling: replaceable
                // metadata stubs get queued with the replace flag; anything
                // else occupying the target is skipped here instead of failing
                // one by one in the move queue.
                if (!IsTargetAncestorOfSource(NormalizeOrganizePath(audiobook.BasePath), target)
                    && TargetExistsWithContent(target))
                {
                    if (IsReplaceableStubTarget(target, audiobook.Id, allAudiobooksForStubCheck, allFilesForStubCheck))
                    {
                        replaceStubIds.Add(audiobook.Id);
                    }
                    else
                    {
                        result.Skipped++;
                        result.SkippedDetails.Add(new OrganizeApplySkippedDto
                        {
                            AudiobookId = audiobook.Id,
                            Reason = "Target directory already exists on disk and contains files",
                        });
                        continue;
                    }
                }

                targets[audiobook.Id] = target;
                if (!byKey.TryGetValue(targetKey, out var ids))
                {
                    ids = new List<int>();
                    byKey[targetKey] = ids;
                }
                ids.Add(audiobook.Id);
            }

            // Drop ids that collide with another id in the same request.
            foreach (var (key, ids) in byKey)
            {
                if (ids.Count <= 1) continue;
                foreach (var id in ids)
                {
                    targets.Remove(id);
                    result.Skipped++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto
                    {
                        AudiobookId = id,
                        Reason = $"Collides with audiobook id(s) {string.Join(", ", ids.Where(i => i != id))} at the same target",
                    });
                }
                result.Warnings.Add($"Skipped {ids.Count} audiobooks that compute to the same target path '{key}'");
            }

            foreach (var audiobook in audiobooks)
            {
                if (!targets.TryGetValue(audiobook.Id, out var target)) continue;
                try
                {
                    var sourcePath = NormalizeOrganizePath(audiobook.BasePath);
                    var jobId = await _moveQueueService.EnqueueMoveAsync(
                        audiobook.Id, target, sourcePath,
                        replaceStubTarget: replaceStubIds.Contains(audiobook.Id));
                    result.Queued++;
                    result.QueuedJobs.Add(new OrganizeQueuedJobDto
                    {
                        JobId = jobId.ToString(),
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title,
                        TargetPath = target,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to enqueue organize move for audiobook {AudiobookId}", audiobook.Id);
                    result.FailedToQueue++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto
                    {
                        AudiobookId = audiobook.Id,
                        Reason = $"Failed to enqueue: {ex.Message}",
                    });
                }
            }

            return new OkObjectResult(result);
        }
    }
}
