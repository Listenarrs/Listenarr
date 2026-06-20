/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Common;
using Microsoft.AspNetCore.DataProtection;

namespace Listenarr.Tests.Features.Application.Downloads.Submission;

[Trait("Name", "DownloadReferenceServiceTests")]
[Trait("Category", "DownloadReferenceService")]
public sealed class DownloadReferenceServiceTests : BaseTests
{
    [Fact]
    public void CreateAndRead_RoundTripsTrustedCandidate()
    {
        // Given
        var service = CreateService(out _);
        var candidate = CreateCandidate();

        // When
        var downloadReference = service.Create(candidate);
        var restored = service.Read(downloadReference);

        // Then
        Assert.Equal(candidate.Id, restored.Id);
        Assert.Equal(candidate.Title, restored.Title);
        Assert.Equal(candidate.SourceDescriptor.Protocol, restored.SourceDescriptor.Protocol);
        Assert.Equal(candidate.SourceDescriptor.IndexerId, restored.SourceDescriptor.IndexerId);
        Assert.Equal(candidate.SourceDescriptor.Locators, restored.SourceDescriptor.Locators);
        Assert.DoesNotContain("download.example", downloadReference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsTamperedReference()
    {
        // Given
        var service = CreateService(out _);
        var downloadReference = service.Create(CreateCandidate());
        var replacement = downloadReference[^1] == 'A' ? 'B' : 'A';
        var tampered = downloadReference[..^1] + replacement;

        // When
        var exception = Assert.Throws<DownloadReferenceException>(() => service.Read(tampered));

        // Then
        Assert.False(exception.IsExpired);
        Assert.Equal("The download reference is invalid.", exception.Message);
    }

    [Fact]
    public void Read_RejectsExpiredReference()
    {
        // Given
        var service = CreateService(out var timeProvider);
        var downloadReference = service.Create(CreateCandidate());
        timeProvider.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        // When
        var exception = Assert.Throws<DownloadReferenceException>(() => service.Read(downloadReference));

        // Then
        Assert.True(exception.IsExpired);
        Assert.Equal("The download reference has expired. Run the search again.", exception.Message);
    }

    private static DownloadReferenceService CreateService(out MutableTimeProvider timeProvider)
    {
        timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionDownloadReferenceProtector(dataProtectionProvider);
        return new DownloadReferenceService(protector, timeProvider);
    }

    private static TrustedDownloadCandidate CreateCandidate()
        => new(
            "result-1",
            "Example Audiobook",
            "Example Author",
            "Example Audiobook",
            "Example Indexer",
            "MP3",
            "English",
            1024,
            12,
            new DownloadSourceDescriptor(
                7,
                "Torznab",
                DownloadProtocol.Torrent,
                [new DownloadSourceLocator(
                    DownloadSourceLocatorKind.TorrentUrl,
                    "https://download.example/file.torrent?apikey=secret")],
                "example.torrent"));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
