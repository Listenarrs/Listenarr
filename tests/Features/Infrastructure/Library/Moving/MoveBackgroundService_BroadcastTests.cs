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
using Microsoft.AspNetCore.SignalR;
using Listenarr.Tests.Common;
using System.Text.Json;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    public class MoveBackgroundService_BroadcastTests : BaseTests
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

        private class CapturingHubContext : IHubContext<DownloadHub>
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
            // Add capturing hub context so we can assert sends
            var capturingHub = new CapturingHubContext();
            _services.AddSingleton<IHubContext<DownloadHub>>(capturingHub);
            Init();

            var src = FileService.GetTempDirectory("listenarr_test_src");
            var dst = FileService.GetTempDirectory("listenarr_test_dst");
            await FileService.GetFileAsync(src, "file1.txt", "one");

            var ab = new Audiobook { Title = "MoveBroadcastTest", BasePath = dst };
            await _audiobookRepository.AddAsync(ab);

            var moveQueue = _provider.GetRequiredService<IMoveQueueService>();
            var bg = _provider.GetRequiredService<MoveBackgroundService>();

            // Start the background service
            await bg.StartAsync(CancellationToken.None);

            // Enqueue move
            var jobId = await moveQueue.EnqueueMoveAsync(ab.Id, dst, src);

            // Poll for job completion (timeout ~15s)
            var succeeded = false;
            for (int i = 0; i < 60; i++)
            {
                var job = await moveQueue.GetJobAsync(jobId);
                if (job != null && string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    succeeded = true; break;
                }
                await Task.Delay(250, CancellationToken.None);
            }

            // Stop background service
            await bg.StopAsync(CancellationToken.None);

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
            var dto = dtoObj as JsonElement?;
            if (dto.HasValue && dto.Value.ValueKind == JsonValueKind.Object)
            {
                var root = dto.Value;
                // basePath should match destination
                Assert.True(root.TryGetProperty("basePath", out var bp));
                Assert.Equal(Path.GetFullPath(dst), bp.GetString());

                // files array should exist (may be empty)
                Assert.True(root.TryGetProperty("files", out var filesProp));
                Assert.True(filesProp.ValueKind == JsonValueKind.Array);
            }
            else
            {
                // If SendCoreAsync serialized using typed object, attempt reflection-based checks
                var basePathProp = dtoObj.GetType().GetProperty("BasePath") ?? dtoObj.GetType().GetProperty("basePath");
                Assert.NotNull(basePathProp);
                var val = basePathProp.GetValue(dtoObj)?.ToString();
                Assert.Equal(Path.GetFullPath(dst), val);
            }
        }
    }
}
