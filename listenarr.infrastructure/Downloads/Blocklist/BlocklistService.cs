/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Downloads.Contracts;
using Listenarr.Domain.Downloads;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Blocklist
{
    public class BlocklistService : IBlocklistService
    {
        private readonly ListenArrDbContext _context;
        private readonly ILogger<BlocklistService> _logger;

        public BlocklistService(ListenArrDbContext context, ILogger<BlocklistService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task BlockAsync(
            int audiobookId,
            string releaseIdentifier,
            string title,
            long? size,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(releaseIdentifier))
            {
                return;
            }

            var already = await _context.BlockedReleases.AnyAsync(
                entry => entry.AudiobookId == audiobookId
                    && entry.ReleaseIdentifier == releaseIdentifier);
            if (already)
            {
                return;
            }

            _context.BlockedReleases.Add(new BlockedRelease
            {
                AudiobookId = audiobookId,
                ReleaseIdentifier = releaseIdentifier,
                Title = title,
                Size = size,
                Reason = reason
            });

            await _context.SaveChangesAsync();
            _logger.LogInformation(
                "Blocked release for audiobook {AudiobookId} so it is not grabbed again: {Reason}",
                audiobookId,
                reason);
        }

        public async Task<IReadOnlyCollection<string>> GetBlockedIdentifiersAsync(int audiobookId)
        {
            return await _context.BlockedReleases
                .Where(entry => entry.AudiobookId == audiobookId)
                .Select(entry => entry.ReleaseIdentifier)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<BlockedRelease>> GetForAudiobookAsync(int audiobookId)
        {
            return await _context.BlockedReleases
                .Where(entry => entry.AudiobookId == audiobookId)
                .OrderByDescending(entry => entry.BlockedAt)
                .ToListAsync();
        }
    }
}
