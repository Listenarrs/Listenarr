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
    /// Local speech-to-text over a bundled whisper.cpp binary (ADR-0001).
    /// No network is involved at transcription time; audio never leaves the host.
    /// </summary>
    public interface IWhisperService
    {
        /// <summary>
        /// True when both the whisper binary and the ggml model are present, so
        /// callers can fail a verification job up-front with a clear message
        /// instead of erroring per book.
        /// </summary>
        Task<bool> IsAvailableAsync();

        /// <summary>
        /// Short model identifier for audit stamping (e.g. "base.en"), derived
        /// from the configured model file name.
        /// </summary>
        string ModelName { get; }

        /// <summary>
        /// Transcribe a 16 kHz mono WAV clip and return the plain-text transcript,
        /// or null when transcription failed. Failures are logged, not thrown —
        /// a single unreadable clip must not abort a batch verification pass.
        /// </summary>
        Task<string?> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default);
    }
}
