/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Application.Integrations.Audiobookshelf.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

public partial class ScanJobProcessor
{
    private void RequestAudiobookshelfScan(Audiobook audiobook, int createdFiles)
    {
        if (createdFiles <= 0)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var scheduler = scope.ServiceProvider.GetService<IAudiobookshelfScanScheduler>();
            scheduler?.RequestScan($"imported {createdFiles} file(s) for audiobook {audiobook.Id}");
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Failed to request Audiobookshelf scan for audiobook {AudiobookId} in background scan",
                audiobook.Id);
        }
    }
}
