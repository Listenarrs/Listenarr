// csharp
using System.Threading.Tasks;
using System.Collections.Generic;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    // IAudiobookRepository moved into the Application project (physical location changed).
    // Kept the original namespace to minimise churn across the codebase.
    public interface IAudiobookRepository
    {
        Task<List<Audiobook>> GetAllAsync();
        Task<Audiobook?> GetByAsinAsync(string asin);
        Task<Audiobook?> GetByIsbnAsync(string isbn);
        Task<Audiobook?> GetByIdAsync(int id);
        Task<string?> GetAuthorAsinByNameAsync(string name);
        Task AddAsync(Audiobook audiobook);
        Task<bool> UpdateAsync(Audiobook audiobook);
        Task<bool> DeleteAsync(Audiobook audiobook);
        Task<bool> DeleteByIdAsync(int id);
        Task<int> DeleteBulkAsync(List<int> ids);
    }
}
