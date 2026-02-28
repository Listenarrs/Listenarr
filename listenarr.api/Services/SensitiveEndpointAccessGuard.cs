using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
