/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal static class MovedAudiobookPathRewriter
{
    public static async Task RewriteAsync(
        int audiobookId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        IAudiobookRepository audiobookRepository,
        ILogger logger,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> targetPhysicalObjectIdentities,
        FileSystemCaseSensitivityMode targetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(audiobookRepository);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(targetPhysicalObjectIdentities);

        bool rewritten;
        try
        {
            rewritten = await audiobookRepository.RewriteMovedPathReferencesAsync(
                audiobookId,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                targetPhysicalObjectIdentities,
                DateTime.UtcNow,
                cancellationToken,
                targetCaseSensitivityMode);
        }
        catch (AudiobookPathRewriteException exception)
        {
            throw new MoveNeedsAttentionException(exception.Message);
        }
        catch (UniqueConstraintViolationException exception)
        {
            logger.LogWarning(
                exception,
                "Moved audiobook {AudiobookId} could not publish file ownership identities",
                audiobookId);
            throw new MoveNeedsAttentionException(
                "The moved audiobook file identity conflicts with an existing ownership record.");
        }

        if (!rewritten)
        {
            throw new MoveNeedsAttentionException(
                "The audiobook disappeared before its moved path references could be persisted.");
        }

        logger.LogInformation(
            "Rewrote stored path references for audiobook {AudiobookId} after physical move",
            audiobookId);
    }
}
