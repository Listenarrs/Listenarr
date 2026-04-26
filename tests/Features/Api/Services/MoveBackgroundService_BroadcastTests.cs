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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Infrastructure.Models;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Repositories;
using Listenarr.Infrastructure.Repositories;

namespace Listenarr.Tests.Features.Api.Services
{
    public class MoveBackgroundService_BroadcastTests
    {
        private class CapturingClientProxy : IClientProxy
        {
            public List<(string Method, object?[] Args)> Calls { get; } = new List<(string, object?[])>();
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                Calls.Add((method, args ?? Array.Empty<object?>()));
                return Task.CompletedTask;
            }
        }

        private class CapturingHubClients : IHubClients
        {
            private readonly CapturingClientProxy _proxy = new CapturingClientProxy();
            public IClientProxy All => _proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
            public IClientProxy Client(string connectionId) => _proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
            public IClientProxy Group(string groupName) => _proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
            public IClientProxy User(string userId) => _proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
            public CapturingClientProxy Proxy => _proxy;
        }

        private class CapturingHubContext : IHubContext<Listenarr.Api.Hubs.DownloadHub>
        {
            public IHubClients Clients { get; } = new CapturingHubClients();
            public IGroupManager Groups { get; } = new TestGroupManager();
        }

        private class TestGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        [Fact(Timeout = 20000)]
        public async Task MoveBackgroundService_BroadcastsFullAudiobookDto_AfterSuccessfulMove()
        {
            var services = new ServiceCollection();
            // Enable debug logging for test diagnostics
            services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
            var dbRoot = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
            var dbName = Guid.NewGuid().ToString();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase(dbName, dbRoot));
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            services.AddSingleton<MoveBackgroundService>();
            services.AddScoped<IMoveJobRepository, EfMoveJobRepository>();
            services.AddScoped<IAudiobookRepository, AudiobookRepository>();

            // Add capturing hub context so we can assert sends
            var capturingHub = new CapturingHubContext();
            services.AddSingleton<IHubContext<Listenarr.Api.Hubs.DownloadHub>>(capturingHub);
            // Register history repository so the move handler will add history and broadcast an AudiobookUpdate
            services.AddScoped<IHistoryRepository, EfHistoryRepository>();

            var provider = services.BuildServiceProvider();
            using var rootScope = provider.CreateScope();
            var db = rootScope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

            // Create source with files
            var src = Path.Join(Path.GetTempPath(), "listenarr_test_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Join(src, "file1.txt"), "one");

            var ab = new Audiobook { Title = "MoveBroadcastTest", BasePath = src };
            db.Audiobooks.Add(ab);
            await db.SaveChangesAsync();

            // Ensure data is visible from a newly created scope (mirrors background service behavior)
            using (provider.CreateScope()) { }

            var moveQueue = provider.GetRequiredService<IMoveQueueService>();
            var bg = provider.GetRequiredService<MoveBackgroundService>();

            // Destination
            var dst = Path.Join(Path.GetTempPath(), "listenarr_test_dst_" + Guid.NewGuid().ToString("N"));

            // Start the background service
            await bg.StartAsync(CancellationToken.None);

            // Enqueue move
            var jobId = await moveQueue.EnqueueMoveAsync(ab.Id, dst, src);

            // Poll for job completion (timeout ~15s)
            var succeeded = false;
            for (int i = 0; i < 60; i++)
            {
                if (moveQueue.TryGetJob(jobId, out var job) && string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    succeeded = true; break;
                }
                await Task.Delay(250, CancellationToken.None);
            }

            // Stop background service
            await bg.StopAsync(CancellationToken.None);

            if (!succeeded)
            {
                // Diagnostic dump for why job didn't complete
                if (moveQueue.TryGetJob(jobId, out var queuedJob))
                {
                    Console.WriteLine($"DIAG: Job {jobId} status={queuedJob.Status} error={queuedJob.Error} attempts={queuedJob.AttemptCount}");
                }
                try
                {
                    var jobs = db.MoveJobs.ToList();
                    foreach (var j in jobs)
                    {
                        Console.WriteLine($"DIAG: DB MoveJob {j.Id} status={j.Status} error={j.Error} attempts={j.AttemptCount}");
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    Console.WriteLine($"DIAG: Failed to read DB move jobs: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"DIAG: Failed to read DB move jobs: {ex.Message}");
                }
            }

            Assert.True(succeeded, "Move job did not complete in time");

            // Assert that the hub received an AudiobookUpdate send with a full DTO (check basePath and files)
            var proxy = ((CapturingHubClients)capturingHub.Clients).Proxy;
            var calls = proxy.Calls.Where(c => string.Equals(c.Method, "AudiobookUpdate", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(calls.Count >= 1, "No AudiobookUpdate calls were captured on the hub");

            // Examine the most recent call payload
            var last = calls.Last();
            Assert.NotNull(last.Args);
            Assert.True(last.Args.Length >= 1, "AudiobookUpdate should have at least one arg (the DTO)");

            var dtoObj = last.Args[0];
            Assert.NotNull(dtoObj);

            // Basic assertions using dynamic/object properties
            var dto = dtoObj as System.Text.Json.JsonElement?;
            if (dto.HasValue && dto.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var root = dto.Value;
                // basePath should match destination
                Assert.True(root.TryGetProperty("basePath", out var bp));
                Assert.Equal(Path.GetFullPath(dst), bp.GetString());

                // files array should exist (may be empty)
                Assert.True(root.TryGetProperty("files", out var filesProp));
                Assert.True(filesProp.ValueKind == System.Text.Json.JsonValueKind.Array);
            }
            else
            {
                // If SendCoreAsync serialized using typed object, attempt reflection-based checks
                var basePathProp = dtoObj.GetType().GetProperty("BasePath") ?? dtoObj.GetType().GetProperty("basePath");
                Assert.NotNull(basePathProp);
                var val = basePathProp.GetValue(dtoObj)?.ToString();
                Assert.Equal(Path.GetFullPath(dst), val);
            }

            // Cleanup
            try { Directory.Delete(dst, true); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }
        }
    }
}
