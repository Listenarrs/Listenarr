
namespace Listenarr.Application.Security.Contracts
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
}
