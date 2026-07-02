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
    /// Verifies that an audiobook's stored metadata matches what the audio
    /// itself says in its spoken credits (ADR-0001).
    /// </summary>
    public interface IIdentityVerifier
    {
        /// <summary>
        /// Compare the audiobook's stored metadata against the spoken credits
        /// extracted from its audio.
        /// </summary>
        /// <param name="audiobook">The book whose metadata is being verified.</param>
        /// <param name="audioFilePath">
        /// The correctly-ordered FIRST audio file of the book (natural/track-number
        /// sort for multi-file books, so "Chapter 2" precedes "Chapter 10") — not
        /// the deprecated single FilePath and not an arbitrary "primary" file.
        /// Spoken credits live at the start of the first file (and end of the last).
        /// </param>
        Task<VerificationVerdict> VerifyAsync(Audiobook audiobook, string audioFilePath, CancellationToken cancellationToken = default);
    }
}
