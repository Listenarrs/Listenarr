using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
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
        private readonly ListenArrDbContext _db;
        private readonly ILogger<UserService> _logger;

        public UserService(ListenArrDbContext db, ILogger<UserService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<User?> GetByUsernameAsync(string username)
        {
            return await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
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

                _db.Users.Add(user);
                await _db.SaveChangesAsync();

                _logger.LogInformation("User created successfully: {Username} (IsAdmin: {IsAdmin})", username, isAdmin);
                return user;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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

                var user = await GetByUsernameAsync(username);
                if (user == null)
                {
                    _logger.LogWarning("Password update failed - user not found: {Username}", username);
                    throw new InvalidOperationException($"User '{username}' not found");
                }

                user.PasswordHash = HashPassword(newPassword);
                _db.Users.Update(user);
                await _db.SaveChangesAsync();

                _logger.LogInformation("Password updated successfully for user: {Username}", username);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error updating password for user: {Username}", username);
                throw;
            }
        }

        public async Task<List<User>> GetAdminUsersAsync()
        {
            return await _db.Users.Where(u => u.IsAdmin).ToListAsync();
        }

        public async Task<int> GetUsersCountAsync()
        {
            return await _db.Users.CountAsync();
        }

        // PBKDF2 with HMACSHA256
        private static string HashPassword(string password)
        {
            using var rng = RandomNumberGenerator.Create();
            var salt = new byte[16];
            rng.GetBytes(salt);

            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
            var hash = pbkdf2.GetBytes(32);

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

                using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
                var computed = pbkdf2.GetBytes(hash.Length);
                return CryptographicOperations.FixedTimeEquals(computed, hash);
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                return false;
            }
        }
    }
}


