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

using Listenarr.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Listenarr.Api.Attributes
{
    /// <summary>
    /// Requires the caller to present a valid API key when authentication is enabled.
    /// When authentication is disabled, all requests are allowed through.
    /// Use this attribute on machine-to-machine endpoints that do not require an interactive session.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireApiKeyAttribute : TypeFilterAttribute
    {
        /// <summary>Initialises a new instance of <see cref="RequireApiKeyAttribute"/>.</summary>
        public RequireApiKeyAttribute()
            : base(typeof(RequireApiKeyFilter))
        {
        }
    }

    /// <summary>Action filter that backs <see cref="RequireApiKeyAttribute"/>.</summary>
    public sealed class RequireApiKeyFilter : IAsyncActionFilter
    {
        private readonly IStartupConfigService _startupConfigService;

        public RequireApiKeyFilter(IStartupConfigService startupConfigService)
        {
            _startupConfigService = startupConfigService;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!_startupConfigService.IsAuthenticationRequired())
            {
                await next();
                return;
            }

            if (HttpSecurityRequestUtils.IsApiKeyAuthenticated(context.HttpContext))
            {
                await next();
                return;
            }

            context.Result = context.HttpContext.User?.Identity?.IsAuthenticated == true
                ? new StatusCodeResult(StatusCodes.Status403Forbidden)
                : new UnauthorizedResult();
        }
    }
}
