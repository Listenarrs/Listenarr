/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record AudiobookDestinationRewriteResult(
    int AudiobookId,
    string DestinationPath,
    string? SourcePath);

public interface IAudiobookDestinationRewriteService
{
    Task<AudiobookDestinationRewriteResult> RewriteDestinationAsync(
        int audiobookId,
        string destinationPath,
        string? expectedSourcePath,
        CancellationToken cancellationToken = default);
}
