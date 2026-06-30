// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

namespace Listenarr.Application.Downloads.Submission;

/// <summary>
/// Persisted metadata keys used by the internal direct-download pipeline.
/// Keep these keys centralized because application creates the DDL row while
/// infrastructure later reads the same row to perform the trusted transfer.
/// </summary>
public static class DirectDownloadMetadataKeys
{
    public const string DownloadType = "DownloadType";
    public const string SourcePolicyKey = "DirectDownloadSourcePolicy";
    public const string OriginalHost = "DirectDownloadOriginalHost";
    public const string StartedAt = "DirectDownloadStartedAt";
    public const string CompletedAt = "DirectDownloadCompletedAt";
    public const string FailedAt = "DirectDownloadFailedAt";
}
