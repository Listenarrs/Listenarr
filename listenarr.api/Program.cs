/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using Listenarr.Api.Services;
using Listenarr.Api.Services.Search;
using Listenarr.Api.Services.Search.Filters;
using Listenarr.Api.Services.Search.Strategies;
using Listenarr.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Linq;
using Listenarr.Api.Middleware;
using System.Net;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Serilog;
using Serilog.Events;
using Polly;
using Polly.Extensions.Http;
using Listenarr.Api.Extensions;
using Listenarr.Infrastructure.Extensions;

// Check for special CLI helpers before building the web host
// Set ContentRootPath to a reliable value for local dev, but leave Docker/production unaffected.
var isDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
var isDev = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";

// Allow an explicit override via environment variable (robust for CI and custom installs)
var contentRootOverride = Environment.GetEnvironmentVariable("LISTENARR_CONTENT_ROOT");

WebApplicationBuilder builder;
string? projectDir = null;
if (!string.IsNullOrWhiteSpace(contentRootOverride))
{
    // Validate the provided override path before using it as ContentRootPath.
    if (Directory.Exists(contentRootOverride))
    {
        builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRootOverride
        });
        projectDir = contentRootOverride;
    }
    else
    {
        Console.WriteLine($"[Listenarr] Warning: LISTENARR_CONTENT_ROOT '{contentRootOverride}' does not exist; ignoring override.");
        builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
    }
}
else if (isDev && !isDocker)
{
    // Resolve to the listenarr.api project directory from the build output.
    // AppContext.BaseDirectory is typically: <project>/bin/<config>/<tfm>/
    // Three GetParent calls navigate back to the project root.
    var baseDir = AppContext.BaseDirectory;
    projectDir = Directory.GetParent(
        Directory.GetParent(
            Directory.GetParent(baseDir)?.FullName ?? baseDir
        )?.FullName ?? baseDir
    )?.FullName ?? baseDir;

    // Safety check: if the resolved directory doesn't look like the project root
    // (i.e. no 'config' sibling or the project file), fall back to default.
    var looksLikeProjectRoot = Directory.Exists(Path.Join(projectDir, "config"))
        || File.Exists(Path.Join(projectDir, "listenarr.api.csproj"));

    if (looksLikeProjectRoot && Directory.Exists(projectDir))
    {
        try
        {
            builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = projectDir });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
        {
            // If for some reason the ContentRootPath cannot be used by CreateBuilder
            // (for example a transient IO issue or an unexpected path layout), fall
            // back to the default builder which will use the running assembly's
            // base directory. Log to console so developers can see the fallback.
            Console.WriteLine($"[Listenarr] Warning: failed to use content root '{projectDir}' - falling back to default. Error: {ex.Message}");
            builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        }
    }
    else
    {
        builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
    }
}
else
{
    builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
}

// repoRoot fallback used in other path computations later
var repoRoot = projectDir ?? AppContext.BaseDirectory;
// dotnet test hosts are typically `testhost` and may not always set
// ASPNETCORE_ENVIRONMENT=Test; detect this explicitly to keep tests isolated.
var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
var isLikelyTestHost =
    string.Equals(processName, "testhost", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(Environment.GetEnvironmentVariable("LISTENARR_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSTEST_SESSION_ID")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_TEST_RUNNER"));

// Configure Serilog for structured logging, file rotation and SignalR broadcasting
var logFilePath = Path.Combine(builder.Environment.ContentRootPath, "config", "logs", "listenarr-.log");
var signalRSink = new SignalRLogSink();
// Prefer explicit environment variable (useful for Docker/runtime overrides)
var logLevelEnv = Environment.GetEnvironmentVariable("LISTENARR_LOG_LEVEL");

// Ensure an external config file in 'config/appsettings/appsettings.json' is available and registered.
// If the file does not exist on first startup, create a default one so non-Docker users have a place to customize.
var externalConfigRelative = Path.Combine("config", "appsettings", "appsettings.json");
var externalConfigAbsolute = Path.Combine(builder.Environment.ContentRootPath, externalConfigRelative);
try
{
    var dir = Path.GetDirectoryName(externalConfigAbsolute) ?? string.Empty;
    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (!File.Exists(externalConfigAbsolute))
        {
            // Minimal, safe default configuration (non-sensitive)
            var defaultJson = "{\n  \"Serilog\": {\n    \"MinimumLevel\": {\n      \"Default\": \"Information\",\n      \"Override\": {\n        \"Microsoft\": \"Warning\",\n        \"System\": \"Warning\"\n      }\n    }\n  }\n}";
            File.WriteAllText(externalConfigAbsolute, defaultJson);
            // Log the absolute path so it's clear where the file was created
            Console.WriteLine($"[Listenarr] Created default configuration at '{externalConfigAbsolute}'. Edit this file to customize app settings.");
        }
}
catch (Exception ex) when (
    ex is IOException
    || ex is UnauthorizedAccessException
    || ex is System.Security.SecurityException
    || ex is ArgumentException
    || ex is NotSupportedException)
{
    // Do not fail startup on inability to write sample config; just log to console and continue
    Console.WriteLine($"[Listenarr] Warning: failed to create default config '{externalConfigRelative}': {ex.Message}");
}

// Register the external config file (relative path is resolved against ContentRootPath)
builder.Configuration.AddJsonFile(externalConfigRelative, optional: true, reloadOnChange: true);

// Allow configuration files to also specify the minimum level (e.g., appsettings.json or appsettings.Development.json)
var configLevel = builder.Configuration["Serilog:MinimumLevel:Default"] ?? builder.Configuration["Logging:LogLevel:Default"];

LogEventLevel minimumLevel;
if (!string.IsNullOrWhiteSpace(logLevelEnv) && Enum.TryParse<LogEventLevel>(logLevelEnv, ignoreCase: true, out var parsedFromEnv))
{
    minimumLevel = parsedFromEnv;
}
else if (!string.IsNullOrWhiteSpace(configLevel) && Enum.TryParse<LogEventLevel>(configLevel, ignoreCase: true, out var parsedFromConfig))
{
    minimumLevel = parsedFromConfig;
}
else
{
    minimumLevel = LogEventLevel.Information;
}

// Industry-standard defaults:
// - Application logs at Information (unless overridden)
// - Third-party and framework logs (Microsoft/System) at Warning
// - EF Core DB command logging elevated to Warning by default (can be lowered to Debug for troubleshooting)
Log.Logger = new Serilog.LoggerConfiguration()
    .Enrich.FromLogContext()
    // Use explicit properties to avoid optional enrichers that may not be present in all builds
    .Enrich.WithProperty("Machine", Environment.MachineName)
    .Enrich.WithProperty("ProcessId", Environment.ProcessId)
    .Enrich.WithProperty("Application", "Listenarr.Api")
    .MinimumLevel.Is(minimumLevel)
    // Framework and system noise should be at Warning by default
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    // EF Core: keep DB command messages higher than app logs; changeable via configuration when needed
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    // Enable debug logging for Transmission adapter to troubleshoot RPC issues
    .MinimumLevel.Override("Listenarr.Api.Services.Adapters.TransmissionAdapter", LogEventLevel.Debug)
    // Console sink for developer-friendly output (includes SourceContext for quick tracing)
    .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    // Primary file sink with daily rolling and structured JSON compatible output template
    .WriteTo.File(
        logFilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 5,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Sink(signalRSink)
    .CreateLogger();

// Use Serilog for logging
builder.Host.UseSerilog();

// Configure URLs to listen on port 4545 (main Listenarr port) - can be overridden by --urls
if (!args?.Any(arg => arg.StartsWith("--urls")) ?? true)
{
    builder.WebHost.UseUrls("http://*:4545");
}

// Configure logging is now handled by Serilog above

// Add services to the container.
// If running as an integration test host, allow the test-side partial to apply any
// additional registrations (for example AddListenarrPersistence so IDbContextFactory<>
// is available to hosted/background services during tests).
if (builder.Environment.IsEnvironment("Test") || isLikelyTestHost)
{
    ApplyTestHostPatches(builder);
}
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Serialize enums as strings instead of integers for better frontend compatibility
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        // Only ignore null values (not empty strings or zeros) to reduce payload size while preserving meaningful empty values
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

// Required for [Authorize] / role policies used by controllers.
builder.Services.AddAuthorization();

// Add SignalR for real-time updates
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        // Serialize enums as strings for SignalR messages too
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// RootFolder repository + service
builder.Services.AddScoped<Listenarr.Api.Repositories.IRootFolderRepository, Listenarr.Api.Repositories.EfRootFolderRepository>();
builder.Services.AddScoped<Listenarr.Api.Services.IRootFolderService, Listenarr.Api.Services.RootFolderService>();
// Migrator for legacy single-outputPath -> RootFolder migration
builder.Services.AddScoped<Listenarr.Api.Services.ILegacyOutputPathMigrator, Listenarr.Api.Services.LegacyOutputPathMigrator>();

// History repository for tracking events
builder.Services.AddScoped<Listenarr.Infrastructure.Repositories.IHistoryRepository, Listenarr.Infrastructure.Repositories.HistoryRepository>();

// Download history service for idempotency and audit trail
builder.Services.AddScoped<Listenarr.Application.Services.IDownloadHistoryService, Listenarr.Infrastructure.Services.DownloadHistoryService>();

// Add in-memory cache for metadata prefetch / reuse
builder.Services.AddMemoryCache();

// Add HTTP client for Audimeta service
builder.Services.AddHttpClient<AudimetaService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

// Add HTTP client for Audnexus service
builder.Services.AddHttpClient<IAudnexusService, AudnexusService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

// Metadata routing across providers
builder.Services.AddScoped<IAudiobookMetadataService, AudiobookMetadataService>();

// Add metadata converters helper
builder.Services.AddScoped<MetadataConverters>();
builder.Services.AddScoped<MetadataMerger>();
builder.Services.AddScoped<SearchProgressReporter>();

// Add search result filters
builder.Services.AddScoped<ISearchResultFilter, KindleEditionFilter>();
builder.Services.AddScoped<ISearchResultFilter, AudiobookOnlyFilter>();
builder.Services.AddScoped<ISearchResultFilter, PromotionalTitleFilter>();
builder.Services.AddScoped<ISearchResultFilter, ProductLikeTitleFilter>();
builder.Services.AddScoped<ISearchResultFilter, MissingInformationFilter>();
builder.Services.AddScoped<SearchResultFilterPipeline>();

// Add metadata fetching strategies
builder.Services.AddScoped<IMetadataStrategy, AudimetaStrategy>();
builder.Services.AddScoped<IMetadataStrategy, AudnexusStrategy>();
builder.Services.AddScoped<MetadataStrategyCoordinator>();

// Add ASIN candidate collector
builder.Services.AddScoped<AsinCandidateCollector>();

// Add ASIN enricher
builder.Services.AddScoped<AsinEnricher>();

// Add fallback scraper
// Add search result scorer
builder.Services.AddScoped<SearchResultScorer>();

// Add ASIN search handler
builder.Services.AddScoped<AsinSearchHandler>();

// Add default HTTP client for other services
builder.Services.AddHttpClient();

// Add named HttpClient for download operations (qBittorrent, Transmission, etc.)
// Prevents socket exhaustion by reusing connections
builder.Services.AddHttpClient("DownloadClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false // Cookies handled per-request for multi-tenant scenarios
    })
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 3,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, duration) =>
            {
                Log.Logger.Warning("[CIRCUIT BREAKER] Download client circuit opened due to {Reason}. Breaking for {Seconds}s", outcome.Exception?.Message ?? "policy trigger", duration.TotalSeconds);
            },
            onReset: () =>
            {
                Log.Logger.Information("[CIRCUIT BREAKER] Download client circuit reset - service recovered");
            }
        ))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                Log.Logger.Information("[RETRY] Download client retry attempt {Attempt} after {Delay}s delay", retryAttempt, timespan.TotalSeconds);
            }
        ));

// Bind download client definitions from configuration and expose via IOptions
builder.Services.Configure<Listenarr.Api.Services.Adapters.DownloadClientsOptions>(builder.Configuration.GetSection("DownloadClients"));

// Validate download client configuration at startup, surface errors early
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Listenarr.Api.Services.Adapters.DownloadClientsOptions>, Listenarr.Api.Services.Adapters.DownloadClientsOptionsValidator>();

// Register named HttpClients for each adapter type so adapter implementations can request the appropriately-configured client.
// qbittorrent
builder.Services.AddHttpClient("qbittorrent")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 3,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, duration) =>
            {
                Log.Logger.Warning("[CIRCUIT BREAKER] qbittorrent client circuit opened due to {Reason}. Breaking for {Seconds}s", outcome.Exception?.Message ?? "policy trigger", duration.TotalSeconds);
            },
            onReset: () =>
            {
                Log.Logger.Information("[CIRCUIT BREAKER] qbittorrent client circuit reset - service recovered");
            }
        ))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                Log.Logger.Information("[RETRY] qbittorrent client retry attempt {Attempt} after {Delay}s delay", retryAttempt, timespan.TotalSeconds);
            }
        ));

// transmission
builder.Services.AddHttpClient("transmission")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30)))
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

// sabnzbd
builder.Services.AddHttpClient("sabnzbd")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30)))
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

// nzbget
builder.Services.AddHttpClient("nzbget")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30)))
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

// Adapter factory resolution is provided by `IDownloadClientAdapterFactory`.

// Register import item resolution service for V2 path resolution
builder.Services.AddScoped<Listenarr.Api.Services.IImportItemResolutionService, Listenarr.Api.Services.ImportItemResolutionService>();

// Add named HttpClient for direct downloads (DDL)
builder.Services.AddHttpClient("DirectDownload")
    .ConfigureHttpClient(client =>
    {


        // Allow up to 2 hours for large file downloads
        client.Timeout = TimeSpan.FromHours(2);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .Or<TaskCanceledException>()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 3,
            durationOfBreak: TimeSpan.FromMinutes(1),
            onBreak: (outcome, duration) =>
            {
                Log.Logger.Warning("[CIRCUIT BREAKER] Direct download circuit opened. Breaking for {Minutes}m", duration.TotalMinutes);
            },
            onReset: () =>
            {
                Log.Logger.Information("[CIRCUIT BREAKER] Direct download circuit reset");
            }
        ))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .Or<TaskCanceledException>()
        .WaitAndRetryAsync(
            retryCount: 2,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))
        ));

// Register our custom services
// Compute an absolute path for the SQLite file based on the content root so
// the published exe will create/use the intended config/database path even
// when the working directory differs.
// Compute default SQLite DB path (config/database/listenarr.db) relative to content root.
// Allow tests to override the path via configuration to avoid shared DB state in CI.
var sqliteDbPathOverride = builder.Configuration["Listenarr:SqliteDbPath"];
var hasExplicitSqliteDbPathOverride = !string.IsNullOrWhiteSpace(sqliteDbPathOverride);
var sqliteDbPath = string.IsNullOrWhiteSpace(sqliteDbPathOverride)
    ? Path.Combine(builder.Environment.ContentRootPath, "config", "database", "listenarr.db")
    : (Path.IsPathRooted(sqliteDbPathOverride)
        ? sqliteDbPathOverride
        : Path.Combine(builder.Environment.ContentRootPath, sqliteDbPathOverride));

// Safety guard: test hosts must never write to the repository DB path.
if (builder.Environment.IsEnvironment("Test") || isLikelyTestHost)
{
    var repoDbPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "config", "database", "listenarr.db"));
    var resolvedSqlitePath = Path.GetFullPath(sqliteDbPath);
    if (string.Equals(resolvedSqlitePath, repoDbPath, StringComparison.OrdinalIgnoreCase))
    {
        sqliteDbPath = Path.Combine(Path.GetTempPath(), "listenarr-tests", "program-main", $"listenarr-{Guid.NewGuid():N}.db");
        Log.Logger.Warning("[Startup] Test environment attempted to use repo sqlite path; forcing isolated test DB path: {SqliteDbPath}", sqliteDbPath);
    }
}

// In development, prefer the repository database path so `npm run dev` uses
// `listenarr.api/config/database/listenarr.db` regardless of the resolved
// ContentRootPath. This ensures developers see and edit the canonical DB.
if (builder.Environment.IsDevelopment() && !isDocker && !hasExplicitSqliteDbPathOverride && !isLikelyTestHost)
{
    // Search ancestors from the content root for a directory that contains
    // the `listenarr.api/config` folder. This avoids duplicating `listenarr.api`
    // when ContentRootPath is already inside a nested build folder.
    string? repoCandidate = null;
    try
    {
        var dir = new DirectoryInfo(builder.Environment.ContentRootPath);
        const int maxDepth = 8;
        int depth = 0;
        while (dir != null && depth++ < maxDepth)
        {
            if (Directory.Exists(Path.Join(dir.FullName, "listenarr.api", "config")))
            {
                repoCandidate = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }
    }
    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
    {
        Log.Logger.Debug(ex, "[Startup] Failed to resolve repo candidate from ContentRootPath; continuing with fallback resolution.");
    }

    // Prefer the current working directory when running dev (npm run dev uses repo root)
    try
    {
        // First, search upward from the current working directory for the repository root
        // (identified by the presence of listenarr.sln). This is the most reliable
        // indicator of the repo root regardless of whether the process was started
        // from a build output folder or the repo directory itself.
        var cwd = Directory.GetCurrentDirectory();
        string? repoRootFromCwd = null;
        try
        {
            var dir = new DirectoryInfo(cwd);
            const int maxDepth2 = 8;
            int depth2 = 0;
            while (dir != null && depth2++ < maxDepth2)
            {
                if (File.Exists(Path.Join(dir.FullName, "listenarr.sln")))
                {
                    repoRootFromCwd = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Log.Logger.Debug(ex, "[Startup] Failed to probe for repo root from current directory '{Cwd}'", cwd);
        }
        string devRepoRoot = repoRootFromCwd ?? repoCandidate ?? repoRoot;

        // If the chosen root already points at the listenarr.api folder, avoid adding
        // an extra 'listenarr.api' segment which previously produced duplicate paths.
        bool rootIsListenarrApi = string.Equals(
            Path.GetFileName(devRepoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            "listenarr.api",
            StringComparison.OrdinalIgnoreCase);

        string devRepoDb = rootIsListenarrApi
            ? Path.Join(devRepoRoot, "config", "database", "listenarr.db")
            : Path.Join(devRepoRoot, "listenarr.api", "config", "database", "listenarr.db");

        if (File.Exists(devRepoDb) || Directory.Exists(Path.GetDirectoryName(devRepoDb)!))
        {
            sqliteDbPath = devRepoDb;
            Log.Logger.Information("[Startup] Development mode detected - forcing SQLite DB to repo path: {DevRepoDb}", devRepoDb);
        }
    }
    catch (Exception ex) when (
        ex is IOException ||
        ex is UnauthorizedAccessException ||
        ex is DirectoryNotFoundException ||
        ex is PathTooLongException ||
        ex is System.Security.SecurityException)
    {
        Log.Logger.Warning(ex, "Failed to resolve dev repo DB using working directory; falling back to computed repoRoot");
        var devRepoDbFallback = Path.Join(repoRoot, "listenarr.api", "config", "database", "listenarr.db");
        sqliteDbPath = devRepoDbFallback;
        Log.Logger.Information("[Startup] Development mode detected - forcing SQLite DB to repo path (fallback): {DevRepoDb}", devRepoDbFallback);
    }
}
else if (builder.Environment.IsDevelopment() && hasExplicitSqliteDbPathOverride)
{
    Log.Logger.Information("[Startup] Development mode detected but honoring explicit SQLite DB path override: {SqliteDbPathOverride}", sqliteDbPath);
}
// Ensure directory exists at startup so EF migrations can create the DB file there
var sqliteDbDir = Path.GetDirectoryName(sqliteDbPath);
if (!string.IsNullOrEmpty(sqliteDbDir) && !Directory.Exists(sqliteDbDir))
{
    Directory.CreateDirectory(sqliteDbDir);
}

// Log the resolved SQLite DB path so developers can verify which file is used at runtime
Log.Logger.Information("[Startup] Resolved SQLite DB path: {SqliteDbPath}", sqliteDbPath);

// Register persistence (DbContextFactory + compatibility DbContext + repositories) via extension
builder.Services.AddListenarrPersistence(builder.Configuration, sqliteDbPath);

// Register consolidated HttpClient policies and common typed clients
builder.Services.AddListenarrHttpClients(builder.Configuration);

// Register adapters and related options/validators
builder.Services.AddListenarrAdapters(builder.Configuration);

// Register infrastructure implementations (repositories live in the Infrastructure project)
builder.Services.AddListenarrInfrastructure();
// Register application-level services (moved from Program.cs to keep startup focused)
builder.Services.AddListenarrAppServices(builder.Configuration);
// Register hosted/background services (moved from Program.cs). Allow tests to disable these.
// In local development, disable hosted/background services to avoid activating
// long-running background workers (and to avoid EF resolution at host start).
    if (isDev)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "Listenarr:DisableHostedServices", "true" }
        });
    }
var disableHostedServices = builder.Configuration.GetValue<bool>("Listenarr:DisableHostedServices");
if (!disableHostedServices)
{
    builder.Services.AddListenarrHostedServices(builder.Configuration);
}

// Startup DB normalizer: run once at startup to idempotently normalize legacy JSON columns
builder.Services.AddHostedService<Listenarr.Api.Services.StartupDbNormalizer>();
// External request options (Prefer US domain / optional US proxy)
builder.Services.Configure<Listenarr.Api.Services.ExternalRequestOptions>(builder.Configuration.GetSection("ExternalRequests"));

// Named HttpClient for US-origin requests (can be configured to use a proxy)
builder.Services.AddHttpClient("us").ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };

    // Proxy configuration removed; keep handler default (no explicit proxy configuration)

    return handler;
});

// CORS is handled by reverse proxy (nginx, Traefik, Caddy, etc.)
// Only add CORS support for local development
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("DevOnly",
            policy =>
            {
                policy.WithOrigins(
                        "http://localhost:5173",
                        "https://localhost:5173",
                        "http://127.0.0.1:5173",
                        "https://127.0.0.1:5173"
                    )
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials(); // Required for SignalR
            });
    });
}

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Try to include XML comments if available
    try
    {
        var xmlFile = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }
    }
    catch (Exception ex) when (
        ex is IOException
        || ex is UnauthorizedAccessException
        || ex is System.Xml.XmlException
        || ex is InvalidOperationException
        || ex is ArgumentException)
    {
        Log.Logger.Warning("[WARNING] Failed to include XML comments in Swagger: {Message}", ex.Message);
    }
    // Use full type names for schema Ids (replace '+' from nested types with '.') to
    // avoid collisions between nested controller DTOs and top-level DTOs that share
    // the same simple type name (e.g. TranslatePathRequest).
    options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace('+', '.'));

        // Resolve conflicting actions (ambiguous HTTP method actions) by selecting the first
        // description. This prevents Swagger generation failures when multiple action descriptors
        // map to similar routes. If more complex disambiguation is needed in future, refine here.
        options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
    });

// whether the cookie is marked secure by forwarding the original scheme (X-Forwarded-Proto).
// Override via configuration: Antiforgery:Cookie:SecurePolicy = None|SameAsRequest|Always
var antiforgeryCookiePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
var cfgPolicy = builder.Configuration["Antiforgery:Cookie:SecurePolicy"];
if (!string.IsNullOrWhiteSpace(cfgPolicy) && Enum.TryParse<Microsoft.AspNetCore.Http.CookieSecurePolicy>(cfgPolicy, true, out var parsedPolicy))
{
    antiforgeryCookiePolicy = parsedPolicy;
}
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.SecurePolicy = antiforgeryCookiePolicy;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
});

// Log guidance on startup when running in production so self-hosters know requirements
if (builder.Environment.IsProduction())
{
    Log.Logger.Information("Antiforgery cookie SecurePolicy set to {Policy}. Ensure the app runs behind HTTPS or forwards X-Forwarded-Proto from a TLS-terminating proxy.", antiforgeryCookiePolicy);
}

// During local development we often run the frontend on a different port via Vite
// and use plain HTTP. Ensure antiforgery cookie can be set in that scenario by
// relaxing the SecurePolicy and SameSite settings when running in Development.
// NOTE: CodeQL flags SecurePolicy.None as a security issue, but this is acceptable
// in Development environment where HTTPS is not available. Production uses SameAsRequest
// (configured above) which properly enforces Secure flag when behind HTTPS reverse proxy.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAntiforgery(options =>
    {
        options.HeaderName = "X-XSRF-TOKEN";
        // Allow the antiforgery cookie to be sent over plain HTTP during local dev
        // This is safe because Development environment is not exposed to external traffic
        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None; // lgtm[cs/cookie-secure-policy-none]
        // During local development the frontend often runs on a different origin
        // (Vite dev server). Use SameSite=Lax so the browser will accept the
        // cookie for same-site requests to the Vite dev server while avoiding
        // the requirement to set Secure (which would require HTTPS). In our
        // setup the dev server proxies /api requests, so Lax is sufficient and
        // more compatible with local HTTP development.
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
    });
}

// Persist Data Protection keys to disk so antiforgery tokens/cookies remain valid
// across process restarts and between instances during local development.
// This avoids issues where tokens are protected with an ephemeral key ring and
// cannot be validated later.
{
    var keyDir = Path.Combine(builder.Environment.ContentRootPath, "config", "dataprotection-keys");
    if (!System.IO.Directory.Exists(keyDir)) System.IO.Directory.CreateDirectory(keyDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new System.IO.DirectoryInfo(keyDir))
        .SetApplicationName("Listenarr");
}

var app = builder.Build();

// Ensure database is created and migrations are applied.
// Use the registered `IDbContextFactory<ListenArrDbContext>` so we do not attempt
// to resolve scoped EF option configurators from the root provider during startup.
try
{
    Log.Logger.Information("[Startup] Applying EF Core migrations at startup");
    using var migrateScope = app.Services.CreateScope();
    var factory = migrateScope.ServiceProvider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
    using var ctx = factory.CreateDbContext();
    ctx.Database.Migrate();
    Log.Logger.Information("[Startup] EF Core migrations applied successfully");
}
catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
{
    // Do not fail startup if migrations cannot be applied; surface the error in logs
    // so developers can run `dotnet ef database update` manually if needed.
    Log.Logger.Error(ex, "[Startup] Failed to apply EF Core migrations at startup. You can run 'dotnet ef database update' manually to apply migrations.");
}
// Warn loudly when authentication is disabled. This mode is convenient for trusted LAN use
// but unsafe for direct internet exposure without an external auth layer.
try
{
    using var authWarningScope = app.Services.CreateScope();
    var configurationService = authWarningScope.ServiceProvider.GetService<IConfigurationService>();
    var startupCfg = configurationService != null ? await configurationService.GetStartupConfigAsync() : null;
    var authRaw = startupCfg?.AuthenticationRequired;
    var authEnabled = authRaw?.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "enabled";
    if (!authEnabled)
    {
        Log.Logger.Warning(
            "[Startup] Authentication is DISABLED. Listenarr should only be exposed on a trusted LAN/VPN in this mode. If exposed to the internet, enable Listenarr authentication or enforce authentication at your reverse proxy.");
    }
}
catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
{
    Log.Logger.Debug(ex, "[Startup] Failed to evaluate authentication-enabled startup warning");
}

// Initialize the SignalR sink now that the hub context is available
signalRSink.Initialize(app.Services.GetRequiredService<IHubContext<LogHub>>());

// Ensure ffprobe is available on first launch (best-effort). Installation runs in background via
// the registered hosted service so the app can serve requests immediately.

// If the user passed --query-users, run the helper now that the DB is migrated and exit.
if (args is not null && args.Contains("--query-users"))
{
    QueryUsersProgram.Run();
    return;
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Use forwarded headers middleware (must be early in pipeline)
// This processes X-Forwarded-For and X-Forwarded-Proto headers from the reverse proxy.
// By default, ASP.NET Core will only trust forwarded headers from loopback addresses (127.0.0.1, ::1).
// This is safe for most self-hosted and direct scenarios, and will work behind a reverse proxy if the proxy runs locally or you set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true.
// For advanced scenarios, set trusted proxies via KnownProxies/KnownNetworks if needed.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    // Do not set KnownProxies/KnownNetworks here for maximum compatibility
});

// Note: HTTPS redirection is handled by the reverse proxy, not by this application

// Serve frontend static files from wwwroot (index.html + assets)
// DefaultFiles enables serving index.html when requesting '/'
// Map `/placeholder.svg` to the frontend `fe/public/placeholder.svg` so the API
// serves the exact same placeholder image used by the frontend without
// modifying any frontend files.
var frontendPlaceholderPath = Path.Combine(app.Environment.ContentRootPath, "..", "fe", "public", "placeholder.svg");
app.MapGet("/placeholder.svg", async context =>
{
    try
    {
        if (File.Exists(frontendPlaceholderPath))
        {
            context.Response.ContentType = "image/svg+xml";
            context.Response.Headers["Cache-Control"] = "public, max-age=300";
            await context.Response.SendFileAsync(frontendPlaceholderPath);
            return;
        }

        // Fallback to backend wwwroot placeholder if the frontend file is not present
        var fallback = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "placeholder.svg");
        if (File.Exists(fallback))
        {
            context.Response.ContentType = "image/svg+xml";
            context.Response.Headers["Cache-Control"] = "public, max-age=300";
            await context.Response.SendFileAsync(fallback);
            return;
        }

        context.Response.StatusCode = 404;
    }
    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
    {
        Log.Logger.Debug(ex, "Failed to serve fallback placeholder image");
        context.Response.StatusCode = 500;
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Serve cached images from config/cache/images directory
var cacheImagesPath = Path.Combine(app.Environment.ContentRootPath, "config", "cache", "images");
if (Directory.Exists(cacheImagesPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(cacheImagesPath),
        RequestPath = "/config/cache/images"
    });
}

// Ensure routing middleware is enabled so endpoint routing features (CORS, Authorization)
// can be applied by subsequent middleware. This must run before UseCors()/UseAuthorization().
app.UseRouting();

// Log incoming request bodies for POST/PUT/PATCH to aid debugging of client integrations
app.UseMiddleware<Listenarr.Api.Middleware.RequestBodyLoggingMiddleware>();

// Enable CORS only in development (production should use reverse proxy for CORS)
if (app.Environment.IsDevelopment())
{
    app.UseCors("DevOnly");
}
// Session-based authentication middleware
app.UseMiddleware<Listenarr.Api.Middleware.SessionAuthenticationMiddleware>();
// API key middleware: allows requests with a valid X-Api-Key or Authorization: ApiKey <key>
app.UseMiddleware<Listenarr.Api.Middleware.ApiKeyMiddleware>();
// Enforce authentication based on startup config
app.UseMiddleware<AuthenticationEnforcerMiddleware>();
// Validate antiforgery tokens for unsafe methods
app.UseMiddleware<Listenarr.Api.Middleware.AntiforgeryValidationMiddleware>();
app.UseAuthorization();

app.MapControllers();

// Map SignalR hub for real-time download updates
if (app.Environment.IsDevelopment())
{
    app.MapHub<DownloadHub>("/hubs/downloads").RequireCors("DevOnly");
    // Map SignalR hub for real-time log broadcasting
    app.MapHub<LogHub>("/hubs/logs").RequireCors("DevOnly");
    // Map SignalR hub for real-time settings updates
    app.MapHub<SettingsHub>("/hubs/settings").RequireCors("DevOnly");
}
else
{
    app.MapHub<DownloadHub>("/hubs/downloads");
    app.MapHub<LogHub>("/hubs/logs");
    // Map SignalR hub for real-time settings updates
    app.MapHub<SettingsHub>("/hubs/settings");
}

// SPA fallback: serve index.html for non-API routes so client-side routing works
app.MapFallbackToFile("index.html");

app.Run();
