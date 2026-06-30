/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Downloads.Submission;

/// <summary>
/// Persisted metadata keys used by the internal direct-download pipeline.
/// Keep these keys centralized because application creates the DDL row while
/// infrastructure later reads the same row to perform the trusted transfer.
/// </summary>
public static class DirectDownloadMetadataKeys
{
    public const string ClientId = "DDL";
    public const string DownloadType = "DownloadType";
    public const string SourcePolicyKey = "DirectDownloadSourcePolicy";
    public const string OriginalHost = "DirectDownloadOriginalHost";
    public const string ArtifactPlan = "DirectDownloadArtifactPlan";
    public const string RequiresArchiveExtraction = "DirectDownloadRequiresArchiveExtraction";
    public const string StartedAt = "DirectDownloadStartedAt";
    public const string CompletedAt = "DirectDownloadCompletedAt";
    public const string FailedAt = "DirectDownloadFailedAt";
}
