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
using Microsoft.AspNetCore.Mvc.Filters;

namespace Listenarr.Api.Attributes
{
    /// <summary>
    /// Controls access to API key management endpoints.
    /// When authentication is enabled, requires an authenticated administrator session (not an API key).
    /// When authentication is disabled, restricts access to local or private-network callers only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireApiKeyManagementAccessAttribute : TypeFilterAttribute
    {
        /// <summary>Initialises a new instance of <see cref="RequireApiKeyManagementAccessAttribute"/>.</summary>
        public RequireApiKeyManagementAccessAttribute()
            : base(typeof(RequireApiKeyManagementAccessFilter))
        {
        }
    }

    /// <summary>Action filter that backs <see cref="RequireApiKeyManagementAccessAttribute"/>.</summary>
    public sealed class RequireApiKeyManagementAccessFilter : IAsyncActionFilter
    {
        private readonly IStartupConfigService _startupConfigService;

        public RequireApiKeyManagementAccessFilter(IStartupConfigService startupConfigService)
        {
            _startupConfigService = startupConfigService;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var httpContext = context.HttpContext;
            if (_startupConfigService.IsAuthenticationRequired())
            {
                var user = httpContext.User;
                if (user?.Identity?.IsAuthenticated == true &&
                    user.IsInRole("Administrator") &&
                    !HttpSecurityRequestUtils.IsApiKeyAuthenticated(httpContext))
                {
                    await next();
                    return;
                }

                context.Result = user?.Identity?.IsAuthenticated == true
                    ? new ObjectResult(new { message = "Administrator session required" }) { StatusCode = StatusCodes.Status403Forbidden }
                    : new UnauthorizedObjectResult(new { message = "Authentication required" });
                return;
            }

            if (HttpSecurityRequestUtils.IsLocalOrPrivateRequest(httpContext))
            {
                await next();
                return;
            }

            context.Result = new ObjectResult(new
            {
                message = "API key management is only available from local or private-network clients when authentication is disabled"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }
}
