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
using System.Text.RegularExpressions;

namespace Listenarr.Application.Audiobooks.Playback;

public class PlaybackService(
    IAudiobookRepository audiobookRepository,
    IApplicationSettingsRepository settingsRepository) : IPlaybackService
{
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

        return new PlaybackStateDto(
            book.Id,
            book.Title,
            book.Asin,
            fileDtos,
            book.PlaybackFileIndex,
            book.PlaybackPositionSeconds,
            book.Finished);
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
