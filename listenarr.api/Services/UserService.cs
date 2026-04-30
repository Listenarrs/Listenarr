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
using System.Security.Cryptography;

namespace Listenarr.Api.Services
{
    public interface IUserService
    {
        Task<User?> GetByUsernameAsync(string username);
        Task<User> CreateUserAsync(string username, string password, string? email = null, bool isAdmin = false);
        Task UpdatePasswordAsync(string username, string newPassword);
        Task<bool> ValidateCredentialsAsync(string username, string password);
        Task<List<User>> GetAdminUsersAsync();
        Task<int> GetUsersCountAsync();
    }

    public class UserService : IUserService
    {
        private readonly IUserRepository _users;
        private readonly ILogger<UserService> _logger;

        public UserService(IUserRepository users, ILogger<UserService> logger)
        {
            _users = users;
            _logger = logger;
        }

        public async Task<User?> GetByUsernameAsync(string username)
        {
            return await _users.GetByUsernameAsync(username);
        }

        public async Task<User> CreateUserAsync(string username, string password, string? email = null, bool isAdmin = false)
        {
            try
            {
                _logger.LogDebug("Attempting to create user: {Username} (IsAdmin: {IsAdmin})", username, isAdmin);

                var existing = await GetByUsernameAsync(username);
                if (existing != null)
                {
                    _logger.LogWarning("User creation failed - user already exists: {Username}", username);
                    throw new InvalidOperationException($"User '{username}' already exists");
                }

                var hash = HashPassword(password);
                var user = new User
                {
                    Username = username,
                    PasswordHash = hash,
                    Email = email,
                    IsAdmin = isAdmin,
                    CreatedAt = DateTime.UtcNow
                };

                await _users.AddAsync(user);

                _logger.LogInformation("User created successfully: {Username} (IsAdmin: {IsAdmin})", username, isAdmin);
                return user;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error creating user: {Username}", username);
                throw;
            }
        }

        public async Task<bool> ValidateCredentialsAsync(string username, string password)
        {
            var user = await GetByUsernameAsync(username);
            if (user == null) return false;
            return VerifyPassword(password, user.PasswordHash);
        }

        public async Task UpdatePasswordAsync(string username, string newPassword)
        {
            try
            {
                _logger.LogDebug("Attempting to update password for user: {Username}", username);

                var user = await _users.GetByUsernameAsync(username);
                if (user == null)
                {
                    _logger.LogWarning("Password update failed - user not found: {Username}", username);
                    throw new InvalidOperationException($"User '{username}' not found");
                }

                user.PasswordHash = HashPassword(newPassword);
                await _users.UpdateAsync(user);

                _logger.LogInformation("Password updated successfully for user: {Username}", username);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error updating password for user: {Username}", username);
                throw;
            }
        }

        public async Task<List<User>> GetAdminUsersAsync()
        {
            return await _users.GetAdminUsersAsync();
        }

        public async Task<int> GetUsersCountAsync()
        {
            return await _users.CountAsync();
        }

        // PBKDF2 with HMACSHA256
        private static string HashPassword(string password)
        {
            using var rng = RandomNumberGenerator.Create();
            var salt = new byte[16];
            rng.GetBytes(salt);

            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

            return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
        }

        private static bool VerifyPassword(string password, string stored)
        {
            try
            {
                var parts = stored.Split(':', 2);
                if (parts.Length != 2) return false;
                var salt = Convert.FromBase64String(parts[0]);
                var hash = Convert.FromBase64String(parts[1]);

                var computed = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, hash.Length);
                return CryptographicOperations.FixedTimeEquals(computed, hash);
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                return false;
            }
        }
    }
}
