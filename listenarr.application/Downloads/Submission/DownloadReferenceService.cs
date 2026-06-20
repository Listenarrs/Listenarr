/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;

namespace Listenarr.Application.Downloads.Submission;

public sealed class DownloadReferenceService(
    IDownloadReferenceProtector protector,
    TimeProvider timeProvider) : IDownloadReferenceService
{
    private const int CurrentVersion = 1;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public string Create(TrustedDownloadCandidate candidate)
    {
        var payload = new DownloadReferencePayload(
            CurrentVersion,
            timeProvider.GetUtcNow(),
            candidate);
        return protector.Protect(JsonSerializer.Serialize(payload));
    }

    public TrustedDownloadCandidate Read(string downloadReference)
    {
        if (string.IsNullOrWhiteSpace(downloadReference))
        {
            throw new DownloadReferenceException("A download reference is required.");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<DownloadReferencePayload>(
                protector.Unprotect(downloadReference));
            if (payload == null || payload.Version != CurrentVersion || payload.Candidate == null)
            {
                throw new DownloadReferenceException("The download reference is invalid or unsupported.");
            }

            if (timeProvider.GetUtcNow() - payload.IssuedAt > Lifetime)
            {
                throw new DownloadReferenceException(
                    "The download reference has expired. Run the search again.",
                    expired: true);
            }

            return payload.Candidate;
        }
        catch (DownloadReferenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            throw new DownloadReferenceException("The download reference is invalid.", innerException: exception);
        }
    }

    private sealed record DownloadReferencePayload(
        int Version,
        DateTimeOffset IssuedAt,
        TrustedDownloadCandidate Candidate);
}
