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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Listenarr.Api.Attributes
{
    /// <summary>
    /// Requires the caller to be an authenticated administrator or API-key principal when
    /// authentication is enabled. Requests to endpoints decorated with <see cref="Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute"/>
    /// are always passed through. When authentication is disabled, all requests are allowed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireAdminOrApiKeyAttribute : TypeFilterAttribute
    {
        /// <summary>Initialises a new instance of <see cref="RequireAdminOrApiKeyAttribute"/>.</summary>
        public RequireAdminOrApiKeyAttribute()
            : base(typeof(RequireAdminOrApiKeyFilter))
        {
        }
    }

    /// <summary>Action filter that backs <see cref="RequireAdminOrApiKeyAttribute"/>.</summary>
    public sealed class RequireAdminOrApiKeyFilter : IAsyncActionFilter
    {
        private readonly IStartupConfigService _startupConfigService;

        public RequireAdminOrApiKeyFilter(IStartupConfigService startupConfigService)
        {
            _startupConfigService = startupConfigService;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var endpoint = context.HttpContext.GetEndpoint();
            if (endpoint?.Metadata?.GetMetadata<AllowAnonymousAttribute>() != null)
            {
                await next();
                return;
            }

            if (!_startupConfigService.IsAuthenticationRequired() ||
                HttpSecurityRequestUtils.IsAuthenticatedAdminOrApiKey(context.HttpContext))
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
