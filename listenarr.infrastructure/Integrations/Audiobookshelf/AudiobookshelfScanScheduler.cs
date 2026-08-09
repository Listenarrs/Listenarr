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

using Listenarr.Application.Integrations.Audiobookshelf.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Integrations.Audiobookshelf;

/// <summary>
/// Coalesces post-import scan requests into a single Audiobookshelf scan.
/// Imports often arrive in bursts (multi-book grabs, bulk manual imports); the
/// trailing-edge debounce waits for a quiet period so one scan covers the whole
/// burst, and the max-delay cap keeps a long steady trickle from deferring the
/// scan forever.
/// </summary>
public sealed class AudiobookshelfScanScheduler : BackgroundService, IAudiobookshelfScanScheduler
{
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AudiobookshelfScanScheduler> _logger;
    private readonly SemaphoreSlim _signal = new(0);

    public AudiobookshelfScanScheduler(IServiceScopeFactory scopeFactory, ILogger<AudiobookshelfScanScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void RequestScan(string reason)
    {
        _logger.LogDebug("Audiobookshelf scan requested: {Reason}", reason);
        _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(stoppingToken);

                // Trailing-edge debounce: each further request restarts the quiet
                // window, up to MaxDelay from the first request.
                var deadline = DateTime.UtcNow + MaxDelay;
                while (DateTime.UtcNow < deadline)
                {
                    var moreRequests = await _signal.WaitAsync(QuietWindow, stoppingToken);
                    if (!moreRequests)
                    {
                        break;
                    }
                }

                await TriggerScanIfEnabledAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task TriggerScanIfEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetAudiobookshelfSettingsAsync();
            if (!settings.NotifyOnImport || string.IsNullOrWhiteSpace(settings.Url) || !settings.HasSavedApiKey)
            {
                _logger.LogDebug("Skipping Audiobookshelf post-import scan; integration disabled or not configured");
                return;
            }

            var audiobookshelfService = scope.ServiceProvider.GetRequiredService<IAudiobookshelfService>();
            var result = await audiobookshelfService.TriggerScanAsync(cancellationToken);
            if (!result.Success)
            {
                _logger.LogWarning("Audiobookshelf post-import scan failed: {Message}", result.Message);
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(exception, "Audiobookshelf post-import scan failed unexpectedly");
        }
    }

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }
}
