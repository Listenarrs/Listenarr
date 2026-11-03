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
    /// Represents a safe, user-facing failure while handing a download to an external client.
    /// Infrastructure adapters should avoid including credentials, query strings, or response
    /// bodies in the exception message.
    /// </summary>
    /// <remarks>
    /// Not sealed: <see cref="DownloadClientRejectedReleaseException"/> narrows this to the
    /// case where the client refused the release rather than failing to accept it, so that
    /// every existing handler keeps treating it as a submission failure by default.
    /// </remarks>
    public class DownloadClientSubmissionException : Exception
    {
        public DownloadClientSubmissionException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
