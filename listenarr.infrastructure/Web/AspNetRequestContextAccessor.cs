/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.Web
{
    public sealed class AspNetRequestContextAccessor : IRequestContextAccessor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AspNetRequestContextAccessor(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public RequestContextSnapshot? Current
        {
            get
            {
                var context = _httpContextAccessor.HttpContext;
                if (context == null)
                {
                    return null;
                }

                var user = context.User;
                var isAuthenticatedAdminOrApiKey = user?.Identity?.IsAuthenticated == true
                    && (user.IsInRole("Administrator")
                        || string.Equals(user.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.Ordinal));

                return new RequestContextSnapshot(
                    context.Request.Path.Value,
                    context.Request.Scheme,
                    context.Request.Host.Value,
                    context.Connection.RemoteIpAddress,
                    isAuthenticatedAdminOrApiKey);
            }
        }
    }
}
