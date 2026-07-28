/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Common
{
    /// <summary>
    /// Represents an expected rejection because the audiobook already has an active or imported download.
    /// Callers must not retry through another protocol because the submission guard runs before side effects.
    /// </summary>
    public sealed class DuplicateDownloadSubmissionException : Exception
    {
        public DuplicateDownloadSubmissionException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
