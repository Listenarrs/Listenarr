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

public interface IGoodreadsImportService
{
    Task<GoodreadsImportResult> ImportAsync(
        GoodreadsImportRequest request,
        CancellationToken cancellationToken = default);
}

public interface IGoodreadsListReader
{
    Task<IReadOnlyList<GoodreadsImportBook>> ReadAsync(
        GoodreadsImportRequest request,
        CancellationToken cancellationToken = default);
}
