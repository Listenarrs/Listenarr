using AsyncKeyedLock;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services
{
    // Background hosted service to rescan files missing metadata and populate DB fields
    public class MetadataRescanService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MetadataRescanService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
        private readonly AsyncNonKeyedLocker _sem = new(2); // bound concurrent extractions

        public MetadataRescanService(IServiceScopeFactory scopeFactory, ILogger<MetadataRescanService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("MetadataRescanService starting");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                    var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

                    var candidates = await db.AudiobookFiles
                        .Where(f => f.DurationSeconds == null || f.Format == null || f.SampleRate == null)
                        // Ensure deterministic ordering when using Take() to avoid EF Core warnings
                        .OrderBy(f => f.Id)
                        .Take(20)
                        .ToListAsync(stoppingToken);

                    if (candidates.Any())
                    {
                        _logger.LogInformation("Found {Count} files missing metadata to rescan", candidates.Count);
                    }

                    var tasks = new List<Task>();
                    foreach (var f in candidates)
                    {
                        using var releaser = await _sem.LockAsync(stoppingToken);

                        // Capture loop variable
                        var file = f;

                        // Start work without passing the stopping token into Task.Run to avoid
                        // TaskCanceledException bubbling up from the runtime; handle cancellation inside.
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                _logger.LogInformation("Re-extracting metadata for file id={Id} path={Path}", file.Id, file.Path);

                                // Bail early if cancellation requested
                                if (stoppingToken.IsCancellationRequested)
                                {
                                    _logger.LogDebug("Cancellation requested before extracting metadata for file id={Id}", file.Id);
                                    return;
                                }

                                var meta = await metadataService.ExtractFileMetadataAsync(file.Path ?? string.Empty);
                                if (meta != null)
                                {
                                    var fi = new System.IO.FileInfo(file.Path ?? string.Empty);
                                    file.Size = fi.Exists ? fi.Length : file.Size;
                                    file.DurationSeconds = meta.Duration.TotalSeconds != 0 ? meta.Duration.TotalSeconds : file.DurationSeconds;
                                    file.Format = !string.IsNullOrEmpty(meta.Format) ? meta.Format : file.Format;
                                    file.Bitrate = meta.Bitrate != 0 ? meta.Bitrate : file.Bitrate;
                                    file.SampleRate = meta.SampleRate != 0 ? meta.SampleRate : file.SampleRate;
                                    file.Channels = meta.Channels != 0 ? meta.Channels : file.Channels;

                                    // Save changes; respect cancellation
                                    await db.SaveChangesAsync(stoppingToken);
                                    _logger.LogInformation("Updated metadata for file id={Id}", file.Id);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // Cancellation requested - ignore and let the service shutdown gracefully
                                _logger.LogDebug("Metadata rescan cancelled for file id={Id}", file.Id);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                _logger.LogWarning(ex, "Failed to rescan metadata for file id={Id} path={Path}", file.Id, file.Path);
                            }
                            finally
                            {
                                releaser.Dispose();
                            }
                        }));
                    }

                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Metadata rescan cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error while running metadata rescan");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            _logger.LogInformation("MetadataRescanService stopping");
        }
    }
}


