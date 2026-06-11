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

using System.Diagnostics;
using Listenarr.Domain.Models;
using Serilog.Core;
using Serilog.Events;

namespace Listenarr.Infrastructure.SignalR
{
    /// <summary>
    /// Custom Serilog sink to broadcast log messages via SignalR in real-time
    /// </summary>
    public class SignalRLogSink : ILogEventSink
    {
        private IHubContext<LogHub>? _hubContext;

        public SignalRLogSink()
        {
        }

        public void Initialize(IHubContext<LogHub> hubContext)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        }

        public void Emit(LogEvent logEvent)
        {
            // Don't try to broadcast if hub context not initialized yet
            if (_hubContext == null)
            {
                return;
            }

            // Fire and forget - broadcast in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var hubContext = _hubContext;

                    // Map Serilog log level to our log level string
                    var level = logEvent.Level switch
                    {
                        LogEventLevel.Verbose => "Debug",
                        LogEventLevel.Debug => "Debug",
                        LogEventLevel.Information => "Info",
                        LogEventLevel.Warning => "Warning",
                        LogEventLevel.Error => "Error",
                        LogEventLevel.Fatal => "Error",
                        _ => "Info"
                    };

                    // Extract source from log event properties
                    string? source = null;
                    if (logEvent.Properties.TryGetValue("SourceContext", out var sourceContextValue))
                    {
                        source = sourceContextValue.ToString().Trim('"');
                        // Simplify namespace (e.g., "Listenarr.Api.Controllers.SystemController" -> "SystemController")
                        if (source.Contains('.'))
                        {
                            var parts = source.Split('.');
                            source = parts[^1]; // Last part
                        }
                    }

                    // Create log entry
                    var logEntry = new LogEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        Timestamp = logEvent.Timestamp.UtcDateTime,
                        Level = level,
                        Message = logEvent.RenderMessage(),
                        Exception = logEvent.Exception?.ToString(),
                        Source = source ?? "Application"
                    };

                    // Broadcast to all connected clients
                    await hubContext.Clients.All.SendAsync("ReceiveLog", logEntry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // If the host is shutting down the service provider may be disposed.
                    // Swallow disposal-related exceptions (including when wrapped in AggregateException
                    // or InnerExceptions) to avoid noisy CI logs during shutdown.
                    static bool IsOrContainsObjectDisposed(Exception? e)
                    {
                        while (e != null)
                        {
                            if (e is ObjectDisposedException)
                                return true;

                            if (e is AggregateException agg)
                            {
                                return agg.InnerExceptions.Any(ie => IsOrContainsObjectDisposed(ie));
                            }

                            e = e.InnerException;
                        }

                        return false;
                    }

                    if (IsOrContainsObjectDisposed(ex))
                    {
                        return;
                    }

                    // Don't log errors from the log sink to avoid infinite loops.
                    // Use Trace so output is still available for diagnostics without feeding back into Serilog.
                    Trace.TraceError($"[SignalRLogSink] Error broadcasting log: {ex.Message}");
                }
            });
        }
    }
}


