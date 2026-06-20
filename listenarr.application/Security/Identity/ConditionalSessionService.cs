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

using System.Security.Claims;

namespace Listenarr.Application.Security.Identity
{
    /// <summary>
    /// A wrapper around SessionService that only provides session functionality
    /// when authentication is required in the configuration.
    /// </summary>
    public class ConditionalSessionService : ISessionService
    {
        private readonly IStartupConfigService _startupConfigService;
        private readonly SessionService _sessionService;

        public ConditionalSessionService(IStartupConfigService startupConfigService, SessionService sessionService)
        {
            _startupConfigService = startupConfigService;
            _sessionService = sessionService;
        }

        public Task<string> CreateSessionAsync(string username, bool isAdmin, bool rememberMe = false)
        {
            if (!_startupConfigService.IsAuthenticationRequired())
            {
                throw new InvalidOperationException("Authentication is not enabled. Set AuthenticationRequired to 'true' in configuration.");
            }

            return _sessionService.CreateSessionAsync(username, isAdmin, rememberMe);
        }

        public Task<ClaimsPrincipal?> GetSessionUserAsync(string sessionToken)
        {
            if (!_startupConfigService.IsAuthenticationRequired())
            {
                return Task.FromResult<ClaimsPrincipal?>(null);
            }

            return _sessionService.GetSessionUserAsync(sessionToken);
        }

        public Task<bool> InvalidateSessionAsync(string sessionToken)
        {
            if (!_startupConfigService.IsAuthenticationRequired())
            {
                return Task.FromResult(false);
            }

            return _sessionService.InvalidateSessionAsync(sessionToken);
        }

        public Task InvalidateAllSessionsForUserAsync(string username)
        {
            if (!_startupConfigService.IsAuthenticationRequired())
            {
                return Task.CompletedTask;
            }

            return _sessionService.InvalidateAllSessionsForUserAsync(username);
        }

        public Task<int> GetActiveSessionCountAsync(string username)
        {
            if (!_startupConfigService.IsAuthenticationRequired())
            {
                return Task.FromResult(0);
            }

            return _sessionService.GetActiveSessionCountAsync(username);
        }
    }
}
