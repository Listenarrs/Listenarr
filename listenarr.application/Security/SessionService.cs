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

using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Application.Security
{
    public interface ISessionService
    {
        Task<string> CreateSessionAsync(string username, bool isAdmin, bool rememberMe = false);
        Task<ClaimsPrincipal?> GetSessionUserAsync(string sessionToken);
        Task<bool> InvalidateSessionAsync(string sessionToken);
        Task InvalidateAllSessionsForUserAsync(string username);
        Task<int> GetActiveSessionCountAsync(string username);
    }

    public class SessionService : ISessionService
    {
        /// <summary>Default session duration (no "remember me").</summary>
        public static readonly TimeSpan DefaultExpiration = TimeSpan.FromHours(8);
        /// <summary>Extended session duration when "remember me" is checked.</summary>
        public static readonly TimeSpan RememberMeExpiration = TimeSpan.FromDays(30);

        private readonly IUserSessionRepository _sessions;
        private readonly ILogger<SessionService> _logger;

        public SessionService(IUserSessionRepository sessions, ILogger<SessionService> logger)
        {
            _sessions = sessions;
            _logger = logger;
        }

        public async Task<string> CreateSessionAsync(string username, bool isAdmin, bool rememberMe = false)
        {
            var expiration = rememberMe ? RememberMeExpiration : DefaultExpiration;
            var now = DateTime.UtcNow;

            // Retry token generation on the unlikely chance of a hash collision.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var sessionToken = GenerateSecureToken();
                var tokenHash = HashToken(sessionToken);

                var session = new UserSession
                {
                    Username = username,
                    TokenHash = tokenHash,
                    IsAdmin = isAdmin,
                    RememberMe = rememberMe,
                    CreatedAt = now,
                    ExpiresAt = now.Add(expiration),
                    LastAccessed = now,
                };

                try
                {
                    await _sessions.CreateAsync(session);
                    _logger.LogInformation("Created session for user {Username} (RememberMe: {RememberMe})", username, rememberMe);
                    return sessionToken;
                }
                catch (DbUpdateException) when (attempt < 2)
                {
                    // Try another token when uniqueness is violated.
                }
            }

            throw new InvalidOperationException("Failed to create a unique session token.");
        }

        public async Task<ClaimsPrincipal?> GetSessionUserAsync(string sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                return null;
            }

            var tokenHash = HashToken(sessionToken);
            var session = await _sessions.GetByTokenHashAsync(tokenHash);
            if (session == null)
            {
                return null;
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, session.Username),
            };

            if (session.IsAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
            }

            var identity = new ClaimsIdentity(claims, "Session");
            return new ClaimsPrincipal(identity);
        }

        public async Task<bool> InvalidateSessionAsync(string sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                return false;
            }

            var tokenHash = HashToken(sessionToken);
            await _sessions.InvalidateAsync(tokenHash);
            return true;
        }

        public async Task InvalidateAllSessionsForUserAsync(string username)
        {
            await _sessions.InvalidateAllForUserAsync(username);
            _logger.LogInformation("Invalidated all sessions for user {Username}", username);
        }

        public async Task<int> GetActiveSessionCountAsync(string username)
        {
            return await _sessions.GetActiveCountAsync(username);
        }

        private static string GenerateSecureToken()
        {
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string HashToken(string sessionToken)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken));
            return Convert.ToHexString(bytes);
        }
    }
}
