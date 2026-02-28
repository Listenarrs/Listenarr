using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Services
{
    public interface IProcessExecutionStore
    {
        Task SaveAsync(ProcessResult result, string? source = null, ProcessStartInfo? startInfo = null, CancellationToken cancellationToken = default);
    }

    public class ProcessExecutionStore : IProcessExecutionStore
    {
        private readonly ListenArrDbContext _db;

        public ProcessExecutionStore(ListenArrDbContext db)
        {
            _db = db;
        }

        public async Task SaveAsync(ProcessResult result, string? source = null, ProcessStartInfo? startInfo = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var entity = new ProcessExecutionLog
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = source,
                    FileName = startInfo?.FileName,
                    Arguments = startInfo?.Arguments,
                    ExitCode = result.ExitCode,
                    TimedOut = result.TimedOut,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    DurationMs = null
                };

                _db.ProcessExecutionLogs.Add(entity);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                // Swallow errors here - persistence is best-effort to avoid disrupting process flows.
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }
    }
}

