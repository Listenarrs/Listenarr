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
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Playback;

public class PlaybackService(
    IAudiobookRepository audiobookRepository,
    IApplicationSettingsRepository settingsRepository,
    IFfmpegService ffmpegService,
    IAudiobookFileRepository fileRepository,
    ILogger<PlaybackService> logger) : IPlaybackService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<PlaybackStateDto?> GetStateAsync(int audiobookId, CancellationToken ct = default)
    {
        var book = await LoadBookWithFilesAsync(audiobookId, ct);
        if (book is null) return null;

        var ordered = OrderFiles(book.Files);
        var fileDtos = ordered
            .Select((f, i) => new PlaybackFileDto(
                i,
                f.DurationSeconds,
                AudioContentType.ForContainer(f.Container ?? f.Format)))
            .ToList();

        var chapters = await BuildChaptersAsync(ordered, ct);

        return new PlaybackStateDto(
            book.Id,
            book.Title,
            book.Asin,
            fileDtos,
            book.PlaybackFileIndex,
            book.PlaybackPositionSeconds,
            book.Finished,
            chapters);
    }

    public async Task<(string Path, string ContentType)?> ResolveFileAsync(int audiobookId, int fileIndex, CancellationToken ct = default)
    {
        var book = await LoadBookWithFilesAsync(audiobookId, ct);
        if (book is null) return null;

        var ordered = OrderFiles(book.Files);
        if (fileIndex < 0 || fileIndex >= ordered.Count) return null;

        var file = ordered[fileIndex];
        var contentType = AudioContentType.ForContainer(file.Container ?? file.Format);
        return (file.Path!, contentType);
    }

    public async Task<bool> SaveAsync(int audiobookId, SavePlaybackRequest req, CancellationToken ct = default)
    {
        var book = await LoadBookWithFilesAsync(audiobookId, ct);
        if (book is null) return false;

        // Clamp client-supplied values: never persist negative/NaN/out-of-range resume state.
        var fileCount = book.Files?.Count ?? 0;
        book.PlaybackFileIndex = fileCount == 0 ? 0 : Math.Clamp(req.FileIndex, 0, fileCount - 1);
        book.PlaybackPositionSeconds = double.IsFinite(req.PositionSeconds) ? Math.Max(0, req.PositionSeconds) : 0;
        book.PlaybackUpdatedUtc = DateTime.UtcNow;
        book.Finished = req.Finished;

        if (req.Finished)
        {
            var settings = await settingsRepository.GetAsync(ct);
            if (settings?.PlayerAutoUnmonitorOnFinish == true)
            {
                book.Monitored = false;
            }
        }

        await audiobookRepository.UpdateAsync(book);
        return true;
    }

    // ── Chapter extraction & aggregation ─────────────────────────────────────

    private async Task<IReadOnlyList<ChapterDto>> BuildChaptersAsync(List<AudiobookFile> ordered, CancellationToken ct)
    {
        var result = new List<ChapterDto>();
        var chapterIndex = 0;

        for (var fileIdx = 0; fileIdx < ordered.Count; fileIdx++)
        {
            var file = ordered[fileIdx];

            // Lazy-extract and cache chapter data. null = never probed.
            if (file.ChaptersJson is null)
            {
                file.ChaptersJson = await ProbeAndSerializeAsync(file, ct);
                try { await fileRepository.UpdateAsync(file, ct); }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogWarning(ex, "Failed to persist chapter cache for AudiobookFile {Id}", file.Id);
                }
            }

            var embedded = DeserializeChapters(file.ChaptersJson);

            if (embedded.Count > 0)
            {
                // File has embedded chapters — emit one ChapterDto per chapter.
                foreach (var ch in embedded)
                {
                    var title = string.IsNullOrWhiteSpace(ch.Title)
                        ? $"Chapter {chapterIndex + 1}"
                        : ch.Title;
                    result.Add(new ChapterDto(chapterIndex++, fileIdx, ch.StartSeconds, ch.EndSeconds, title));
                }
            }
            else
            {
                // No embedded chapters — emit a single whole-file chapter.
                var title = !string.IsNullOrWhiteSpace(file.Path)
                    ? Path.GetFileNameWithoutExtension(file.Path)
                    : $"Part {fileIdx + 1}";
                result.Add(new ChapterDto(chapterIndex++, fileIdx, 0, file.DurationSeconds ?? 0, title));
            }
        }

        return result;
    }

    /// <summary>Run ffprobe and return the JSON to cache. Always returns non-null ("[]" on any failure).</summary>
    private async Task<string> ProbeAndSerializeAsync(AudiobookFile file, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(file.Path)) return "[]";

        try
        {
            var chapters = await ffmpegService.RunFfprobeChaptersAsync(file.Path);
            if (chapters.Count == 0) return "[]";

            var entries = chapters.Select(c => new ChapterCacheEntry(c.StartSeconds, c.EndSeconds, c.Title)).ToList();
            return JsonSerializer.Serialize(entries, _jsonOptions);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogWarning(ex, "Failed to probe chapters for file {Path}", file.Path);
            return "[]";
        }
    }

    private static IReadOnlyList<ChapterCacheEntry> DeserializeChapters(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return Array.Empty<ChapterCacheEntry>();

        try
        {
            var list = JsonSerializer.Deserialize<List<ChapterCacheEntry>>(json, _jsonOptions);
            return list ?? (IReadOnlyList<ChapterCacheEntry>)Array.Empty<ChapterCacheEntry>();
        }
        catch
        {
            return Array.Empty<ChapterCacheEntry>();
        }
    }

    // ponytail: simple POD for chapter JSON cache; no version field needed until the schema changes.
    private record ChapterCacheEntry(double StartSeconds, double EndSeconds, string Title);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Audiobook?> LoadBookWithFilesAsync(int id, CancellationToken ct)
    {
        var books = await audiobookRepository.GetByIdsWithFilesAsync([id], ct);
        return books.FirstOrDefault();
    }

    private static List<AudiobookFile> OrderFiles(List<AudiobookFile>? files)
    {
        if (files is null or { Count: 0 }) return [];

        // AudiobookFile carries no track-number field; fall back to natural sort on Path.
        return [.. files.OrderBy(f => f.Path ?? string.Empty, NaturalSortComparer.Instance)];
    }

    // ── Natural-sort comparer ─────────────────────────────────────────────────
    // ponytail: simple token-split comparer; use a dedicated lib if multi-locale
    // or Unicode edge-cases matter.

    /// <summary>Sorts strings so that "Part 2" &lt; "Part 10" (numeric segments compared by value).</summary>
    public sealed class NaturalSortComparer : IComparer<string>
    {
        public static readonly NaturalSortComparer Instance = new();

        private static readonly Regex _tokenizer = new(@"(\d+)", RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var xParts = _tokenizer.Split(x);
            var yParts = _tokenizer.Split(y);

            for (var i = 0; i < Math.Min(xParts.Length, yParts.Length); i++)
            {
                int cmp;
                if (int.TryParse(xParts[i], out var xn) && int.TryParse(yParts[i], out var yn))
                    cmp = xn.CompareTo(yn);
                else
                    cmp = string.Compare(xParts[i], yParts[i], StringComparison.OrdinalIgnoreCase);

                if (cmp != 0) return cmp;
            }

            return xParts.Length.CompareTo(yParts.Length);
        }
    }
}
