/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Services;

public static class SensitiveEndpointAccessGuard
{
    public static IActionResult? RequireLocalOrAdmin(HttpContext? context, ILogger? logger, string endpointName)
    {
        if (SecurityRequestUtils.IsLoopbackRequest(context) || SecurityRequestUtils.IsAuthenticatedAdminOrApiKey(context))
        {
            return null;
        }

        var httpContext = context!;
        logger?.LogWarning(
            "Blocked sensitive endpoint {Endpoint} for remote unauthenticated caller from {RemoteIp}",
            endpointName,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return new ObjectResult(new
        {
            message = "This endpoint is restricted to localhost callers or an authenticated admin/API key."
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
