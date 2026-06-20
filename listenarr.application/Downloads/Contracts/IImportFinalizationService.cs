/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
namespace Listenarr.Application.Downloads.Contracts
{
    public interface IImportFinalizationService
    {
        Task FinalizeAsync(
            string jobId,
            string downloadId,
            int audiobookId,
            string title,
            string downloadClientId,
            string correlationId,
            Dictionary<string, object>? details = null,
            CancellationToken ct = default);
    }
}
