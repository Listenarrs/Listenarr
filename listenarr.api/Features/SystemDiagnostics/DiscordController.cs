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
using Listenarr.Api.Attributes;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Listenarr.Api.Features.SystemDiagnostics
{
    [ApiController]
    [Route("api/v{version:apiVersion}/discord")]
    [LocalOrAdmin]
    [Tags("Discord")]
    public class DiscordController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DiscordController> _logger;
        private readonly IDiscordBotService _botService;
        private readonly IProcessRunner _processRunner;
        private readonly IApplicationPathService _applicationPathService;
        private readonly IFileSystem _fileSystem;

        public DiscordController(
            IConfigurationService configurationService,
            IHttpClientFactory httpClientFactory,
            ILogger<DiscordController> logger,
            IDiscordBotService botService,
            IProcessRunner processRunner,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem)
        {
            _configurationService = configurationService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _botService = botService;
            _processRunner = processRunner;
            _applicationPathService = applicationPathService;
            _fileSystem = fileSystem;
        }

        /// <summary>
        /// Check whether the configured bot token is valid and whether the bot is present in the configured guild (if one is set).
        /// </summary>
        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();

                if (settings == null)
                    return StatusCode(500, new { success = false, message = "Unable to load application settings" });

                if (string.IsNullOrWhiteSpace(settings.DiscordBotToken))
                    return BadRequest(new { success = false, message = "No Discord bot token configured" });

                if (string.IsNullOrWhiteSpace(settings.DiscordApplicationId))
                    return BadRequest(new { success = false, message = "No Discord application id configured" });

                var token = settings.DiscordBotToken.Trim();
                var client = _httpClientFactory.CreateClient("discord");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

                // Try to fetch /users/@me once so we can surface bot identity in the status
                string? meBody = null;
                try
                {
                    var meRespInitial = await client.GetAsync("https://discord.com/api/v10/users/@me");
                    meBody = await meRespInitial.Content.ReadAsStringAsync();
                    if (meRespInitial.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        return BadRequest(new { success = false, message = "Invalid bot token (unauthorized)" });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to call /users/@me for diagnostics");
                }

                // If a guild id is configured, try to GET the guild - if 200 -> bot is in guild
                if (!string.IsNullOrWhiteSpace(settings.DiscordGuildId))
                {
                    var guildId = settings.DiscordGuildId.Trim();
                    var guildUrl = $"https://discord.com/api/v10/guilds/{guildId}";
                    var guildResp = await client.GetAsync(guildUrl);
                    if (guildResp.IsSuccessStatusCode)
                    {
                        return Ok(new { success = true, installed = true, guildId = guildId, botInfo = meBody });
                    }

                    if (guildResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return Ok(new { success = true, installed = false, guildId = guildId, botInfo = meBody });
                    }

                    if (guildResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        return BadRequest(new { success = false, message = "Invalid bot token (unauthorized)" });
                    }

                    // Other errors - return not installed with details
                    var guildBody = await guildResp.Content.ReadAsStringAsync();
                    _logger.LogWarning("Discord guild check returned {Status}: {Body}", guildResp.StatusCode, LogRedaction.RedactText(guildBody, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { token })));
                    return StatusCode(500, new { success = false, message = "Failed to verify guild membership", status = (int)guildResp.StatusCode, body = guildBody });
                }

                // No guild configured - verify token by fetching the bot user
                var meRespCheck = await client.GetAsync("https://discord.com/api/v10/users/@me");
                if (meRespCheck.IsSuccessStatusCode)
                {
                    var json = await meRespCheck.Content.ReadAsStringAsync();
                    return Ok(new { success = true, installed = (bool?)null, botInfo = json });
                }

                if (meRespCheck.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return BadRequest(new { success = false, message = "Invalid bot token (unauthorized)" });
                }

                var meCheckBody = await meRespCheck.Content.ReadAsStringAsync();
                _logger.LogWarning("Discord token validation returned {Status}: {Body}", meRespCheck.StatusCode, LogRedaction.RedactText(meCheckBody, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { token })));
                return StatusCode(500, new { success = false, message = "Failed to validate bot token", status = (int)meRespCheck.StatusCode, body = meCheckBody });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error checking Discord status");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Register slash commands (guild-scoped if guild id configured, otherwise global) using the stored application id and bot token.
        /// </summary>
        [HttpPost("register-commands")]
        public async Task<IActionResult> RegisterCommands()
        {
            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();
                if (settings == null)
                    return StatusCode(500, new { success = false, message = "Unable to load application settings" });

                if (string.IsNullOrWhiteSpace(settings.DiscordBotToken) || string.IsNullOrWhiteSpace(settings.DiscordApplicationId))
                    return BadRequest(new { success = false, message = "Discord application id and bot token must be configured" });

                var token = settings.DiscordBotToken.Trim();
                var appId = settings.DiscordApplicationId.Trim();
                var client = _httpClientFactory.CreateClient("discord");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

                // Fetch /users/@me for diagnostics and to verify token <-> application pairing
                string? meBody = null;
                string? botId = null;
                try
                {
                    var meResp = await client.GetAsync("https://discord.com/api/v10/users/@me");
                    meBody = await meResp.Content.ReadAsStringAsync();
                    if (meResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        return BadRequest(new { success = false, message = "Invalid bot token (unauthorized)", body = meBody });
                    }

                    if (meResp.IsSuccessStatusCode)
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(meBody);
                            if (doc.RootElement.TryGetProperty("id", out var idProp)) botId = idProp.GetString();
                        }
                        catch (System.Text.Json.JsonException exception)
                        {
                            _logger.LogWarning(exception, "Discord connection test could not read the bot id from the /users/@me response; continuing without it");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Discord register-commands: failed to call /users/@me");
                }

                if (!string.IsNullOrWhiteSpace(botId) && botId != appId)
                {
                    _logger.LogWarning("Discord register-commands: application id mismatch - configured appId={AppId} but token belongs to botId={BotId}", appId, botId);
                    return BadRequest(new { success = false, message = "Bot token does not belong to the configured Application ID. Ensure the Application ID and Bot Token are from the same Discord application.", details = new { configuredAppId = appId, tokenBotId = botId, me = meBody } });
                }

                // Build commands payload
                var groupName = string.IsNullOrWhiteSpace(settings.DiscordCommandGroupName) ? "request" : settings.DiscordCommandGroupName.Trim();
                var subName = string.IsNullOrWhiteSpace(settings.DiscordCommandSubcommandName) ? "audiobook" : settings.DiscordCommandSubcommandName.Trim();

                object[] commandsPayload = new object[] {
                    new {
                        name = groupName,
                        type = 1,
                        description = "Request commands",
                        options = new object[] {
                            new {
                                type = 1,
                                name = subName,
                                description = "Request an audiobook by title",
                                options = new object[] {
                                    new {
                                        type = 3,
                                        name = "title",
                                        description = "Title to search for",
                                        required = true
                                    }
                                }
                            }
                        }
                    },
                    /*
                    // Temporarily disabled: comment out the request-config set-channel admin subcommand so it is not registered.
                    new {
                        name = "request-config",
                        description = "Admin configuration for requests",
                        options = new object[] {
                            new {
                                type = 1,
                                name = "set-channel",
                                description = "Set the channel to accept request commands",
                                options = new object[] { }
                            }
                        }
                    }
                    */
                };

                string url;
                if (!string.IsNullOrWhiteSpace(settings.DiscordGuildId))
                {
                    var guildId = settings.DiscordGuildId.Trim();
                    url = $"https://discord.com/api/v10/applications/{appId}/guilds/{guildId}/commands";
                }
                else
                {
                    url = $"https://discord.com/api/v10/applications/{appId}/commands";
                }

                var json = System.Text.Json.JsonSerializer.Serialize(commandsPayload);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PutAsync(url, content);
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    return Ok(new { success = true, message = "Commands registered", body });
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return BadRequest(new { success = false, message = "Invalid bot token (unauthorized)", body });
                }

                _logger.LogWarning("Register commands returned {Status}: {Body}", resp.StatusCode, LogRedaction.RedactText(body, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { token })));
                return StatusCode((int)resp.StatusCode, new { success = false, message = "Failed to register commands", body });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error registering Discord commands");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Start the Discord bot process.
        /// </summary>
        [HttpPost("start-bot")]
        public async Task<IActionResult> StartBot()
        {
            try
            {
                var isRunning = await _botService.IsBotRunningAsync();
                if (isRunning)
                {
                    return Ok(new { success = true, message = "Bot is already running", status = "running" });
                }

                // Run lightweight diagnostics first so we can surface missing files/node
                try
                {
                    var diagAction = await Diagnostics();
                    if (diagAction is OkObjectResult okObj && okObj.Value != null)
                    {
                        // Use the diagnostics value directly instead of parsing to avoid JsonDocument disposal issues
                        var diagnosticsValue = okObj.Value;

                        // Serialize and parse to inspect key fields
                        var serialized = JsonSerializer.Serialize(diagnosticsValue);
                        using var doc = JsonDocument.Parse(serialized);
                        var root = doc.RootElement;

                        var botDirExists = root.TryGetProperty("botDirExists", out var bd) && bd.GetBoolean();
                        var indexExists = root.TryGetProperty("indexExists", out var ix) && ix.GetBoolean();
                        var nodeError = root.TryGetProperty("nodeError", out var ne) && ne.ValueKind != JsonValueKind.Null ? ne.GetString() : null;

                        if (!botDirExists || !indexExists)
                        {
                            // Return the original object, not the JsonElement from disposed document
                            return BadRequest(new { success = false, message = "Discord bot files are missing", diagnostics = diagnosticsValue });
                        }

                        if (!string.IsNullOrWhiteSpace(nodeError))
                        {
                            return BadRequest(new { success = false, message = "Node.js not available or returned an error", nodeError });
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to run diagnostics before starting bot");
                }

                var started = await _botService.StartBotAsync();
                if (started)
                {
                    var status = await _botService.GetBotStatusAsync();
                    return Ok(new { success = true, message = "Bot started successfully", status });
                }
                else
                {
                    return StatusCode(500, new { success = false, message = "Failed to start bot" });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error starting Discord bot");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Stop the Discord bot process.
        /// </summary>
        [HttpPost("stop-bot")]
        public async Task<IActionResult> StopBot()
        {
            try
            {
                var isRunning = await _botService.IsBotRunningAsync();
                if (!isRunning)
                {
                    return Ok(new { success = true, message = "Bot is not running", status = "stopped" });
                }

                var stopped = await _botService.StopBotAsync();
                return stopped
                    ? Ok(new { success = true, message = "Bot stopped successfully", status = "stopped" })
                    : StatusCode(500, new { success = false, message = "Failed to stop bot" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error stopping Discord bot");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get the current status of the Discord bot process.
        /// </summary>
        [HttpGet("bot-status")]
        public async Task<IActionResult> GetBotStatus()
        {
            try
            {
                var status = await _botService.GetBotStatusAsync();
                var isRunning = await _botService.IsBotRunningAsync();
                return Ok(new { success = true, status, isRunning });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting Discord bot status");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Lightweight diagnostics to help debug bot startup issues in production.
        /// Returns whether the bot directory and index.js exist and whether `node` is available.
        /// </summary>
        [HttpGet("diagnostics")]
        public async Task<IActionResult> Diagnostics()
        {
            try
            {
                var contentRoot = _applicationPathService.ContentRootPath;
                var botDirectory = _applicationPathService.DiscordBotRootPath;
                var indexJsPath = System.IO.Path.Join(botDirectory, "index.js");

                var botDirExists = _fileSystem.DirectoryExists(botDirectory);
                var indexExists = _fileSystem.FileExists(indexJsPath);

                // Check for node availability by running `node --version` (best-effort, non-blocking)
                string? nodeVersion = null;
                string? nodeError = null;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "cmd.exe" : "bash",
                        Arguments = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "/c node --version" : "-c \"node --version\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = botDirectory
                    };

                    var res = await _processRunner.RunAsync(psi, 2000);
                    nodeVersion = res.Stdout?.Trim();
                    nodeError = res.Stderr?.Trim();
                    if (res.TimedOut)
                    {
                        nodeError = (nodeError ?? string.Empty) + " (timed out)";
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    nodeError = ex.Message;
                }

                return Ok(new
                {
                    success = true,
                    contentRoot,
                    botDirectory = botDirectory,
                    botDirExists,
                    indexJsPath,
                    indexExists,
                    nodeVersion,
                    nodeError
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error running Discord diagnostics");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
    }
}
