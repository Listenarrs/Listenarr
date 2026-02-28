using System.Threading.Tasks;

namespace Listenarr.Api.Services
{
    public interface IFileMover
    {
        Task<bool> MoveFileAsync(string sourceFile, string destFile);
        Task<bool> CopyFileAsync(string sourceFile, string destFile);
        Task<bool> HardlinkFileAsync(string sourceFile, string destFile);
        Task<bool> MoveDirectoryAsync(string sourceDir, string destDir);
        Task<bool> CopyDirectoryAsync(string sourceDir, string destDir);
    }
}
