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
    /// The download client refused this particular release, and refusing it says nothing
    /// about the health of the client or of the release itself.
    ///
    /// The case that motivates it is a qBittorrent 5.2 conflict: the client already holds
    /// the info-hash being submitted, which happens whenever one release satisfies more
    /// than one wanted book. Treating that as a submission failure reports a broken client
    /// for what is really "I have this already", and treating it as a release failure
    /// would blocklist a release the user is currently downloading.
    ///
    /// Deriving from <see cref="DownloadClientSubmissionException"/> is deliberate. Callers
    /// that do not care keep their existing behaviour, and only the ones that want to skip
    /// rather than fail need to know this type exists.
    /// </summary>
    public sealed class DownloadClientRejectedReleaseException : DownloadClientSubmissionException
    {
        public DownloadClientRejectedReleaseException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
