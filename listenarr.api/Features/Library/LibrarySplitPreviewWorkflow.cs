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

using Listenarr.Application.Audiobooks;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Preview for the "Split collection" workflow: cluster a record's files
    /// into per-book groups (subdirectory first, then embedded tag, then
    /// filename stem) and suggest an existing library record for each group.
    /// Read-only — applying is a sequence of file transfers/deletes driven by
    /// the client.
    /// </summary>
    public sealed class LibrarySplitPreviewWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IFfmpegService _ffmpegService;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibrarySplitPreviewWorkflow> _logger;

        public LibrarySplitPreviewWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IFfmpegService ffmpegService,
            IFileSystem fileSystem,
            ILogger<LibrarySplitPreviewWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _ffmpegService = ffmpegService;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<IActionResult> PreviewAsync(int id, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var files = await _audioFileRepository.GetByAudiobookIdAsync(id, ct);

            // Read each file's embedded book title (Album/Title tag) so a collection bulk-renamed
            // to the parent record's name still splits by the real book each file belongs to —
            // filenames are useless there, but the tags name the actual book.
            //
            // CRITICAL: parallelize ONLY the raw ffprobe (RunFfprobeAsync spawns a process and
            // touches no DbContext). Higher-level metadata reads hit the request-scoped,
            // non-thread-safe EF DbContext per call; probing those concurrently corrupted
            // results, so the same file read its tag on one run and blank on the next — making
            // the clusters (and therefore the moves) non-deterministic.
            var embeddedTitles = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
            try
            {
                // Resolve/install ffprobe once up front so the parallel probes don't race on it.
                await _ffmpegService.GetFfprobePathAsync();

                var targets = files
                    .Select(f => (f.Id, path: FileUtils.CombineWithOptionalBase(audiobook.BasePath, f.Path ?? string.Empty)))
                    .Where(x => !string.IsNullOrWhiteSpace(x.path) && _fileSystem.FileExists(x.path))
                    .ToList();

                await Parallel.ForEachAsync(
                    targets,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (target, token) =>
                    {
                        try
                        {
                            var meta = await _ffmpegService.RunFfprobeAsync(target.path);
                            var bookTitle = !string.IsNullOrWhiteSpace(meta?.Album) ? meta!.Album : meta?.Title;
                            if (!string.IsNullOrWhiteSpace(bookTitle))
                            {
                                embeddedTitles[target.Id] = bookTitle!;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "ffprobe failed for split clustering (file {FileId})", target.Id);
                        }
                    });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Tag reads are an enhancement to clustering, not a requirement —
                // fall back to path/stem clustering when ffprobe is unavailable.
                _logger.LogDebug(ex, "Embedded-tag read skipped for split preview of audiobook {AudiobookId}", id);
            }

            var clusters = FileClustering.Cluster(files, audiobook.BasePath, embeddedTitles);

            // Suggestion candidates: same-author records first (a collection
            // dump is almost always one author's shelf), then the whole library
            // as fallback. The suggester prefers the longest matching title, so
            // ordering only matters for the fallback breadth.
            var all = await _repo.GetAllAsync();
            var sourceAuthors = new HashSet<string>(
                audiobook.Authors ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var sameAuthor = new List<(int, string)>();
            var others = new List<(int, string)>();
            foreach (var candidate in all)
            {
                if (candidate.Id == id || string.IsNullOrWhiteSpace(candidate.Title)) continue;
                var isSameAuthor = (candidate.Authors ?? new List<string>()).Any(sourceAuthors.Contains);
                (isSameAuthor ? sameAuthor : others).Add((candidate.Id, candidate.Title!));
            }
            var titleById = all.Where(a => a.Id != id).ToDictionary(a => a.Id, a => a.Title ?? string.Empty);

            var response = clusters.Select(cluster =>
            {
                var suggested = SplitDestinationSuggester.Suggest(cluster.DisplayName, sameAuthor)
                                ?? SplitDestinationSuggester.Suggest(cluster.DisplayName, others);
                return new
                {
                    key = cluster.Key,
                    displayName = cluster.DisplayName,
                    fileIds = cluster.Files.Select(f => f.Id).ToList(),
                    fileNames = cluster.Files
                        .Select(f => Path.GetFileName(f.Path ?? string.Empty))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    suggestedTargetId = suggested,
                    suggestedTargetTitle = suggested.HasValue && titleById.TryGetValue(suggested.Value, out var t) ? t : null
                };
            }).ToList();

            return new OkObjectResult(new { audiobookId = id, clusters = response });
        }
    }
}
