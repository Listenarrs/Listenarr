/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Api.Services
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

        private readonly IDbContextFactory<ListenArrDbContext> _dbContextFactory;
        private readonly ILogger<SessionService> _logger;

        public SessionService(IDbContextFactory<ListenArrDbContext> dbContextFactory, ILogger<SessionService> logger)
        {
            _dbContextFactory = dbContextFactory;
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

                await using var db = await _dbContextFactory.CreateDbContextAsync();
                db.UserSessions.Add(new UserSession
                {
                    Username = username,
                    TokenHash = tokenHash,
                    IsAdmin = isAdmin,
                    RememberMe = rememberMe,
                    CreatedAt = now,
                    ExpiresAt = now.Add(expiration),
                    LastAccessed = now,
                });

                try
                {
                    await db.SaveChangesAsync();
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
            var now = DateTime.UtcNow;

            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var session = await db.UserSessions.SingleOrDefaultAsync(s => s.TokenHash == tokenHash);
            if (session == null)
            {
                return null;
            }

            if (session.ExpiresAt <= now)
            {
                db.UserSessions.Remove(session);
                await db.SaveChangesAsync();
                _logger.LogInformation("Session expired for user {Username}", session.Username);
                return null;
            }

            // Track activity for diagnostics and possible future inactivity policies.
            session.LastAccessed = now;
            await db.SaveChangesAsync();

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
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var session = await db.UserSessions.SingleOrDefaultAsync(s => s.TokenHash == tokenHash);
            if (session == null)
            {
                return false;
            }

            db.UserSessions.Remove(session);
            await db.SaveChangesAsync();
            _logger.LogInformation("Invalidated session for user {Username}", session.Username);
            return true;
        }

        public async Task InvalidateAllSessionsForUserAsync(string username)
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var sessions = await db.UserSessions.Where(s => s.Username == username).ToListAsync();
            if (sessions.Count == 0)
            {
                return;
            }

            db.UserSessions.RemoveRange(sessions);
            await db.SaveChangesAsync();
            _logger.LogInformation("Invalidated all sessions for user {Username} (count: {Count})", username, sessions.Count);
        }

        public async Task<int> GetActiveSessionCountAsync(string username)
        {
            var now = DateTime.UtcNow;
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var expired = await db.UserSessions
                .Where(s => s.Username == username && s.ExpiresAt <= now)
                .ToListAsync();
            if (expired.Count > 0)
            {
                db.UserSessions.RemoveRange(expired);
                await db.SaveChangesAsync();
            }

            return await db.UserSessions.CountAsync(s => s.Username == username && s.ExpiresAt > now);
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
