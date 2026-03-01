using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Listenarr.Api.Services
{
    public interface IDiscordBotService
    {
        Task<bool> StartBotAsync();
        Task<bool> StopBotAsync();
        Task<bool> IsBotRunningAsync();
        Task<string?> GetBotStatusAsync();
    }

    public class DiscordBotService : IDiscordBotService
    {
        private readonly ILogger<DiscordBotService> _logger;
        private readonly IStartupConfigService _startupConfigService;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IProcessRunner? _processRunner;
        private string? _botApiKey;
        private Process? _botProcess;
        private readonly Lock _processLock = new();

        public DiscordBotService(
            ILogger<DiscordBotService> logger,
            IStartupConfigService startupConfigService,
            IHostEnvironment hostEnvironment,
            IHttpContextAccessor httpContextAccessor,
            IProcessRunner? processRunner = null)
        {
            _logger = logger;
            _startupConfigService = startupConfigService;
            _hostEnvironment = hostEnvironment;
            _httpContextAccessor = httpContextAccessor;
            _processRunner = processRunner;
        }

        public async Task<bool> StartBotAsync()
        {
            lock (_processLock)
            {
                // Clear the process reference if it has exited
                if (_botProcess != null && _botProcess.HasExited)
                {
                    _botProcess = null;
                }

                if (_botProcess != null && !_botProcess.HasExited)
                {
                    _logger.LogInformation("Bot is already running");
                    return true;
                }
            }

            try
            {
                var botDirectory = Path.Combine(_hostEnvironment.ContentRootPath, "tools", "discord-bot");
                if (!Directory.Exists(botDirectory))
                {
                    _logger.LogError("Discord bot directory not found at {Path}", botDirectory);
                    return false;
                }

                var indexJsPath = Path.Combine(botDirectory, "index.js");
                if (!File.Exists(indexJsPath))
                {
                    _logger.LogError("Discord bot index.js not found at {Path}", indexJsPath);
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash",
                    WorkingDirectory = botDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Determine the Listenarr API URL with proper fallbacks for Docker
                var listenarrUrl = GetListenarrUrl();

                _logger.LogInformation("Starting Discord bot with LISTENARR_URL: {Url}", listenarrUrl);
                startInfo.EnvironmentVariables["LISTENARR_URL"] = listenarrUrl;

                // Pass the server API key into the helper process so it can authenticate
                // programmatic requests (SignalR negotiate, settings fetch, etc.). Only set
                // when an API key is present in the startup config to avoid sending empty
                // values into the child environment.
                try
                {
                    var cfg = _startupConfigService.GetConfig();
                    if (cfg != null && !string.IsNullOrWhiteSpace(cfg.ApiKey))
                    {
                        _botApiKey = cfg.ApiKey;
                        startInfo.EnvironmentVariables["LISTENARR_API_KEY"] = _botApiKey;
                        _logger.LogInformation("Passing LISTENARR_API_KEY to bot process (present=true)");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to read startup config for API key passthrough");
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    startInfo.Arguments = "/c node index.js";
                }
                else
                {
                    startInfo.Arguments = "-c \"node index.js\"";
                }

                _logger.LogInformation("Starting Discord bot with command: {Command} {Args} in {WorkingDir}",
                    startInfo.FileName, startInfo.Arguments, startInfo.WorkingDirectory);

                // If we have a process runner available, use it to perform a quick pre-flight
                // check (e.g. `node --version`) so we can give a clearer diagnostic when Node
                // is not available on the PATH. We still start the long-running bot process
                // via Process.Start so we retain the ability to kill and inspect the child.
                if (_processRunner != null)
                {
                    try
                    {
                        var checkPsi = new ProcessStartInfo
                        {
                            FileName = "node",
                            Arguments = "--version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = botDirectory
                        };

                        using var _reg_check = _processRunner.RegisterTransientSensitive(new[] { _botApiKey ?? string.Empty });
                        var check = await _processRunner.RunAsync(checkPsi, 5000);
                        if (check.TimedOut)
                        {
                            _logger.LogWarning("Pre-flight node --version check timed out");
                        }
                        else if (check.ExitCode != 0)
                        {
                            _logger.LogWarning("Pre-flight node --version returned non-zero (ExitCode={Code}). Stderr: {Err}", check.ExitCode, LogRedaction.RedactText(check.Stderr, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { _botApiKey ?? string.Empty })));
                        }
                        else
                        {
                            _logger.LogDebug("Detected node: {Out}", LogRedaction.RedactText(check.Stdout, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { _botApiKey ?? string.Empty }))?.Trim());
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogDebug(ex, "node --version pre-flight check failed (node may not be available in PATH)");
                    }
                }

                lock (_processLock)
                {
                    // Start the long-running bot process via the process runner wrapper if available.
                    if (_processRunner != null)
                    {
                        using var _reg_start = _processRunner.RegisterTransientSensitive(new[] { _botApiKey ?? string.Empty });
                        _botProcess = _processRunner.StartProcess(startInfo);
                    }
                    else
                    {
                        // Fallback to directly starting the process when no runner is provided.
                        _botProcess = Process.Start(startInfo);
                    }

                    if (_botProcess != null)
                    {
                        try
                        {
                            _botProcess.EnableRaisingEvents = true;
                        }
                        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }

                        _botProcess.Exited += (sender, e) =>
                        {
                            try
                            {
                                if (sender is Process exitedProc)
                                {
                                    try
                                    {
                                        _logger.LogInformation("Discord bot process exited with code: {ExitCode}", exitedProc.ExitCode);
                                    }
                                    catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                            }
                            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { 
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }

                            // Clear the process reference so it can be restarted
                            lock (_processLock)
                            {
                                _botProcess = null;
                            }
                        };

                        // Start async reading of output to prevent deadlocks
                        var localProc = _botProcess;
                        if (localProc?.StandardOutput != null)
                        {
                            _ = Task.Run(() => ReadOutputAsync(localProc));
                        }

                        if (localProc?.StandardError != null)
                        {
                            _ = Task.Run(() => ReadErrorAsync(localProc));
                        }
                    }
                }

                // Give the process a moment to start
                await Task.Delay(1000);

                return await IsBotRunningAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to start Discord bot");
                return false;
            }
        }

        private string GetListenarrUrl()
        {
            // Priority 1: LISTENARR_PUBLIC_URL environment variable (Docker deployments)
            var envUrl = Environment.GetEnvironmentVariable("LISTENARR_PUBLIC_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
            {
                _logger.LogInformation("Using LISTENARR_PUBLIC_URL from environment: {Url}", envUrl);
                return envUrl.TrimEnd('/');
            }

            // Priority 2: Construct from current HTTP request (when available)
            try
            {
                var httpContext = _httpContextAccessor.HttpContext;
                if (httpContext != null)
                {
                    var request = httpContext.Request;
                    var scheme = request.Scheme;
                    var host = request.Host.Value;

                    // Check if we're behind a reverse proxy (X-Forwarded headers)
                    if (request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto))
                    {
                        scheme = forwardedProto.ToString();
                    }
                    if (request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost))
                    {
                        host = forwardedHost.ToString();
                    }

                    var url = $"{scheme}://{host}";
                    _logger.LogInformation("Constructed URL from HTTP context: {Url}", url);
                    return url;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to construct URL from HTTP context");
            }

            // Priority 3: Use startup config
            try
            {
                var config = _startupConfigService.GetConfig();
                if (config != null)
                {
                    var protocol = (config.EnableSsl ?? false) ? "https" : "http";
                    var port = config.Port ?? 5000;
                    var urlBase = config.UrlBase?.TrimEnd('/') ?? "";

                    // In Docker, use host.docker.internal instead of localhost for better compatibility
                    var host = Environment.GetEnvironmentVariable("DOCKER_ENV") != null
                        ? "host.docker.internal"
                        : "localhost";

                    var url = $"{protocol}://{host}:{port}{urlBase}";
                    _logger.LogInformation("Constructed URL from startup config: {Url}", url);
                    return url;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to construct URL from startup config");
            }

            // Priority 4: Final fallback - try host.docker.internal first in Docker, then localhost
            var fallbackHost = Environment.GetEnvironmentVariable("DOCKER_ENV") != null
                ? "host.docker.internal"
                : "localhost";

            var fallbackUrl = $"http://{fallbackHost}:5000";
            _logger.LogWarning("Using fallback URL: {Url}", fallbackUrl);
            return fallbackUrl;
        }

        public async Task<bool> StopBotAsync()
        {
            lock (_processLock)
            {
                if (_botProcess == null || _botProcess.HasExited)
                {
                    _logger.LogInformation("Bot is not running");
                    return true;
                }
            }

            try
            {
                Process? proc;
                lock (_processLock)
                {
                    proc = _botProcess;
                }

                if (proc == null)
                {
                    _logger.LogInformation("Bot is not running");
                    return true;
                }

                _logger.LogInformation("Stopping Discord bot process");
                try
                {
                    proc.Kill(true); // Kill entire process tree
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Error killing bot process (may have exited already)");
                }

                // Wait up to 5 seconds for process exit
                try
                {
                    using var cts = new CancellationTokenSource(5000);
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Timed out waiting for Discord bot to exit after kill");
                }

                lock (_processLock)
                {
                    _botProcess = null;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to stop Discord bot");
                return false;
            }
        }

        public Task<bool> IsBotRunningAsync()
        {
            lock (_processLock)
            {
                return Task.FromResult(_botProcess != null && !_botProcess.HasExited);
            }
        }

        public async Task<string?> GetBotStatusAsync()
        {
            var isRunning = await IsBotRunningAsync();
            if (!isRunning)
            {
                return "stopped";
            }

            lock (_processLock)
            {
                if (_botProcess != null)
                {
                    return $"running (PID: {_botProcess.Id})";
                }
            }

            return "unknown";
        }

        private async Task ReadOutputAsync(Process process)
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line != null)
                    {
                        var safe = LogRedaction.RedactText(line, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { _botApiKey ?? string.Empty }));
                        _logger.LogInformation("Bot stdout: {Line}", safe);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error reading bot stdout");
            }
        }

        private async Task ReadErrorAsync(Process process)
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line != null)
                    {
                        var safe = LogRedaction.RedactText(line, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { _botApiKey ?? string.Empty }));
                        _logger.LogError("Bot stderr: {Line}", safe);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error reading bot stderr");
            }
        }


    }
}
