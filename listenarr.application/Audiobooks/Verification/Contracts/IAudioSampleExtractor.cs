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
namespace Listenarr.Application.Audiobooks.Verification.Contracts
{
    /// <summary>
    /// The audio windows sampled for spoken-credit verification (ADR-0001).
    /// Spoken credits live at the start of the FIRST file and often repeat at
    /// the end of the LAST file, so the two windows come from different source
    /// files on multi-file books.
    /// </summary>
    /// <param name="OpeningSeconds">Seconds clipped from the start of the first file; 0 disables the opening window.</param>
    /// <param name="ClosingSeconds">Seconds clipped from the end of the last file; 0 disables the closing window.</param>
    public sealed record AudioSampleStrategy(int OpeningSeconds = 90, int ClosingSeconds = 30);

    /// <summary>
    /// Temp WAV clips produced by <see cref="IAudioSampleExtractor"/>. Dispose
    /// deletes the temp files; either path is null when that window was disabled
    /// or its extraction failed.
    /// </summary>
    public sealed class AudioSampleSet : IAsyncDisposable
    {
        // Supplied by the (infrastructure) extractor that created the temp
        // clips; the application layer holds no filesystem code of its own.
        private readonly Action<string?>? _cleanup;

        public AudioSampleSet(Action<string?>? cleanup = null)
        {
            _cleanup = cleanup;
        }

        public string? OpeningClipPath { get; init; }
        public string? ClosingClipPath { get; init; }

        public bool IsEmpty => OpeningClipPath == null && ClosingClipPath == null;

        public ValueTask DisposeAsync()
        {
            _cleanup?.Invoke(OpeningClipPath);
            _cleanup?.Invoke(ClosingClipPath);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Clips the verification sample windows out of a book's audio via FFmpeg,
    /// downmixed to the 16 kHz mono WAV whisper.cpp expects (ADR-0001).
    /// </summary>
    public interface IAudioSampleExtractor
    {
        /// <summary>
        /// Extract the strategy's windows. <paramref name="firstFilePath"/> must be
        /// the correctly-ordered first audio file (natural/track-number sort);
        /// <paramref name="lastFilePath"/> the correspondingly-last file (same file
        /// for single-file books). A window whose extraction fails is returned as
        /// null rather than failing the whole set.
        /// </summary>
        Task<AudioSampleSet> ExtractAsync(string firstFilePath, string? lastFilePath, AudioSampleStrategy strategy, CancellationToken cancellationToken = default);
    }
}
