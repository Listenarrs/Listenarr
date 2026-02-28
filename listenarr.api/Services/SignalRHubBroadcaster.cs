using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Listenarr.Api.Hubs;
using Listenarr.Application.Services;

namespace Listenarr.Api.Services
{
    public class SignalRHubBroadcaster : IHubBroadcaster
    {
        private readonly IHubContext<DownloadHub> _hubContext;
        private readonly ILogger<SignalRHubBroadcaster> _logger;

        public SignalRHubBroadcaster(IHubContext<DownloadHub> hubContext, ILogger<SignalRHubBroadcaster> logger)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task BroadcastQueueUpdateAsync(List<Domain.Models.QueueItem> queue)
        {
            try
            {
                // Primary, public API
                await _hubContext.Clients.All.SendAsync("QueueUpdate", queue);

                // Some tests/mocks expect SendCoreAsync; call as a compatibility step
                try
                {
                    var clientProxy = _hubContext?.Clients?.All;
                    if (clientProxy != null)
                    {
                        await clientProxy.SendCoreAsync("QueueUpdate", new object[] { queue }, CancellationToken.None);
                    }
                }
                catch (Exception inner) when (inner is not OperationCanceledException && inner is not OutOfMemoryException && inner is not StackOverflowException) {
                    _logger.LogDebug(inner, "Direct SendCoreAsync for QueueUpdate failed (non-fatal)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to broadcast QueueUpdate");
            }
        }
    }
}

