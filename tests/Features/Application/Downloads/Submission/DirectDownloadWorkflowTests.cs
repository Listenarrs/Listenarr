/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Downloads.Submission;

public sealed class DirectDownloadWorkflowTests
{
    [Fact]
    public async Task PersistenceFailure_DoesNotReturnPhantomDownloadId()
    {
        var repository = new Mock<IDownloadRepository>();
        repository.Setup(repo => repo.AddAsync(It.IsAny<Download>()))
            .ThrowsAsync(new IOException("disk unavailable"));
        var workflow = new DirectDownloadWorkflow(
            repository.Object,
            NullLogger<DirectDownloadWorkflow>.Instance);

        await Assert.ThrowsAsync<PersistenceException>(
            () => workflow.CreateTrackedDownloadAsync(CreateSubmission(), audiobookId: 42));
    }

    [Fact]
    public async Task DuplicateReservation_ReturnsEmptyId()
    {
        var repository = new Mock<IDownloadRepository>();
        repository.Setup(repo => repo.AddAsync(It.IsAny<Download>()))
            .ThrowsAsync(new UniqueConstraintViolationException(
                "duplicate active download",
                new InvalidOperationException()));
        var workflow = new DirectDownloadWorkflow(
            repository.Object,
            NullLogger<DirectDownloadWorkflow>.Instance);

        var id = await workflow.CreateTrackedDownloadAsync(CreateSubmission(), audiobookId: 42);

        Assert.Empty(id);
    }

    private static PreparedDirectDownloadSubmission CreateSubmission() => new(
        "Book",
        "Author",
        "Album",
        "Source",
        "M4B",
        "en",
        100,
        "https://example.com/book.m4b",
        new Uri("https://example.com/book.m4b"));
}
