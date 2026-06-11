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

using System.Net;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Events;
using Listenarr.Infrastructure.Extensions;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Downloads;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Common;
using Listenarr.Application.Search.Filters;
using Listenarr.Application.Search;
using Listenarr.Application.Metadata;
using Listenarr.Application.Notification;
using Listenarr.Application.Search.Strategies;
using Listenarr.Infrastructure.Services;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Application.Audiobooks;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Api.Middleware;
using Listenarr.Api.Filters;
using System.Text.Json.Serialization;

var contentRootPath = AppContext.BaseDirectory;
var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

if (string.IsNullOrEmpty(environmentName))
{
    environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
}

if (string.IsNullOrEmpty(environmentName))
{
    environmentName = "Production";
}

// dotnet test hosts are typically `testhost` and may not always set
// ASPNETCORE_ENVIRONMENT=Test; detect this explicitly to keep tests isolated.
var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
if (string.Equals(processName, "testhost", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(Environment.GetEnvironmentVariable("LISTENARR_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSTEST_SESSION_ID")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_TEST_RUNNER")))
{
    environmentName = "Test";
}

if (string.Equals("Test", environmentName))
{
    var testContentRootPath = Path.Combine(Path.GetTempPath(), "ListenarrTests");
    Directory.CreateDirectory(testContentRootPath); // Unchecked exception: Tests must fail if we cannot create that directory

    Environment.SetEnvironmentVariable("LISTENARR_CONTENT_ROOT", testContentRootPath);
}

// Allow an explicit override via environment variable (robust for CI and custom installs)
var contentRootOverride = Environment.GetEnvironmentVariable("LISTENARR_CONTENT_ROOT");
if (!string.IsNullOrWhiteSpace(contentRootOverride))
{
    try
    {
        contentRootOverride = Path.GetFullPath(contentRootOverride);

        if (!Directory.Exists(contentRootOverride))
        {
            Console.WriteLine($"[Listenarr] LISTENARR_CONTENT_ROOT '{contentRootOverride}' does not exist; creating it");
            Directory.CreateDirectory(contentRootOverride);
        }

        contentRootPath = contentRootOverride;
    }
    catch (Exception)
    {
        Console.WriteLine($"[Listenarr] Error: LISTENARR_CONTENT_ROOT '{contentRootOverride}' cannot be used or created; ignoring override.");
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRootPath,
    EnvironmentName = environmentName
});

// Configure Serilog for structured logging, file rotation and SignalR broadcasting
var logFilePath = Path.Join(builder.Environment.ContentRootPath, "config", "logs", "listenarr-.log");
var signalRSink = new SignalRLogSink();
// Prefer explicit environment variable (useful for Docker/runtime overrides)
var logLevelEnv = Environment.GetEnvironmentVariable("LISTENARR_LOG_LEVEL");

// Ensure an external config file in 'config/appsettings/appsettings.json' is available and registered.
// If the file does not exist on first startup, create a default one so non-Docker users have a place to customize.
var externalConfigRelative = Path.Join("config", "appsettings", "appsettings.json");
var externalConfigAbsolute = Path.Join(builder.Environment.ContentRootPath, externalConfigRelative);
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

if (builder.Environment.IsEnvironment("Test"))
{
    Log.Logger = Serilog.Core.Logger.None;
}

// Configure URLs to listen on port 4545 by default, while allowing explicit
// command-line/config/environment URL bindings to take precedence.
var hasUrlsArg = args?.Any(arg =>
    arg.Equals("--urls", StringComparison.OrdinalIgnoreCase) ||
    arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase)) ?? false;
var hasUrlsConfig =
    !string.IsNullOrWhiteSpace(builder.Configuration["urls"]) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_URLS")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS"));

if (!hasUrlsArg && !hasUrlsConfig)
{
    builder.WebHost.UseUrls("http://*:4545");
}

// Configure logging is now handled by Serilog above

// Add services to the container.
// If running as an integration test host, allow the test-side partial to apply any
// additional registrations (for example AddListenarrInfrastructure so IDbContextFactory<>
// is available to hosted/background services during tests).
if (builder.Environment.IsEnvironment("Test"))
{
    ApplyTestHostPatches(builder);
}
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Serialize enums as strings instead of integers for better frontend compatibility
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // Only ignore null values (not empty strings or zeros) to reduce payload size while preserving meaningful empty values
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

var defaultApiVersion = new ApiVersion(1, 0);
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = defaultApiVersion;
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddMvc(options =>
    {
        // Apply v1 to all controllers so we get versioned API explorer metadata
        // without having to annotate every controller class.
        foreach (var controllerType in typeof(Program).Assembly.GetTypes()
                     .Where(t =>
                         !t.IsAbstract
                         && t.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
                         && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t)))
        {
            options.Conventions.Controller(controllerType).HasApiVersion(defaultApiVersion);
        }
    })
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });

// Required for [Authorize] / role policies used by controllers.
builder.Services.AddAuthorization();

// *Arr standard proxy trust model:
// trust forwarded headers from RFC1918/RFC4193/link-local proxy networks that are
// common in Docker/Synology/reverse-proxy deployments.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;

    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("fc00::"), 7));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("fe80::"), 10));
});

// Add SignalR for real-time updates
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        // Serialize enums as strings for SignalR messages too
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// RootFolder service
builder.Services.AddScoped<IRootFolderService, RootFolderService>();
// Migrator for legacy single-outputPath -> RootFolder migration
builder.Services.AddScoped<ILegacyOutputPathMigrator, LegacyOutputPathMigrator>();

// Download history service for idempotency and audit trail
builder.Services.AddScoped<IDownloadHistoryService, DownloadHistoryService>();

// Add in-memory cache for metadata prefetch / reuse
builder.Services.AddMemoryCache();

// Add HTTP client for Audible service
builder.Services.AddHttpClient<AudibleService>()
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
builder.Services.AddScoped<IMetadataStrategy, AudibleMetadataStrategy>();
builder.Services.AddScoped<IMetadataStrategy, AudnexusStrategy>();
builder.Services.AddScoped<MetadataStrategyCoordinator>();

// Add ASIN candidate collector
builder.Services.AddScoped<AsinCandidateCollector>();

// Add ASIN enricher
builder.Services.AddScoped<AsinEnricher>();

// Add fallback scraper
// Add search result scorer
builder.Services.AddScoped<SearchResultScorerService>();

// Add ASIN search handler
builder.Services.AddScoped<AsinSearchHandler>();

// Register named HttpClients for each adapter type so adapter implementations can request the appropriately-configured client.
builder.Services.AddListenarrHttpClients(builder.Configuration);

// Register our custom services
// Compute an absolute path for the SQLite file based on the content root so
// the published exe will create/use the intended config/database path even
// when the working directory differs.
// Compute default SQLite DB path (config/database/listenarr.db) relative to content root.
// Allow tests to override the path via configuration to avoid shared DB state in CI.
var sqliteDbPathOverride = builder.Configuration["Listenarr:SqliteDbPath"];
var sqliteDbPath = string.IsNullOrWhiteSpace(sqliteDbPathOverride)
    ? Path.Join(builder.Environment.ContentRootPath, "config", "database", "listenarr.db")
    : (Path.IsPathRooted(sqliteDbPathOverride)
        ? sqliteDbPathOverride
        : Path.Join(builder.Environment.ContentRootPath, sqliteDbPathOverride));

// Safety guard: test hosts must never write to the repository DB path.
if (builder.Environment.IsEnvironment("Test"))
{
    var repoDbPath = Path.GetFullPath(Path.Join(builder.Environment.ContentRootPath, "config", "database", "listenarr.db"));
    var resolvedSqlitePath = Path.GetFullPath(sqliteDbPath);
    if (string.Equals(resolvedSqlitePath, repoDbPath, StringComparison.OrdinalIgnoreCase))
    {
        sqliteDbPath = Path.Join(Path.GetTempPath(), "listenarr-tests", "program-main", $"listenarr-{Guid.NewGuid():N}.db");
        Log.Logger.Warning("[Startup] Test environment attempted to use repo sqlite path; forcing isolated test DB path: {SqliteDbPath}", sqliteDbPath);
    }
}
// Ensure directory exists at startup so EF migrations can create the DB file there
var sqliteDbDir = Path.GetDirectoryName(sqliteDbPath);
if (!string.IsNullOrEmpty(sqliteDbDir) && !Directory.Exists(sqliteDbDir))
{
    Directory.CreateDirectory(sqliteDbDir);
}

// Log the resolved SQLite DB path so developers can verify which file is used at runtime
Log.Logger.Information("[Startup] Resolved SQLite DB path: {SqliteDbPath}", sqliteDbPath);

// Register adapters and related options/validators
builder.Services.AddListenarrAdapters(builder.Configuration);

// Register infrastructure implementations (DB wiring + repositories live in the Infrastructure project)
builder.Services.AddListenarrInfrastructure(options =>
    options.UseSqlite($"Data Source={sqliteDbPath}", sqliteOptions =>
        sqliteOptions.MigrationsAssembly(typeof(QualityProfileRepository).Assembly.GetName().Name)),
    builder.Environment.ContentRootPath);
// Register application-level services (moved from Program.cs to keep startup focused)
builder.Services.AddListenarrAppServices(builder.Configuration);
// Register hosted/background services (moved from Program.cs). Allow tests to disable these.
// Hosted services are ENABLED by default in local development because download monitoring
// and import processing rely on these background workers.
// Use explicit config/env override only when intentionally disabling them.
var disableHostedServices =
    builder.Configuration.GetValue<bool>("Listenarr:DisableHostedServices") ||
    string.Equals(Environment.GetEnvironmentVariable("LISTENARR_DISABLE_HOSTED_SERVICES"), "true", StringComparison.OrdinalIgnoreCase);

if (disableHostedServices)
{
    Log.Logger.Warning("[Startup] Hosted/background services are disabled by configuration override");
}
else
{
    Log.Logger.Information("[Startup] Hosted/background services are enabled");
}
// Register the queue singleton outside the hosted-services guard so controllers
// (e.g. RootFoldersController) can resolve it even when hosted services are disabled (tests).
builder.Services.AddSingleton<IUnmatchedScanQueueService, UnmatchedScanQueueService>();
if (!disableHostedServices)
{
    builder.Services.AddListenarrHostedServices(builder.Configuration);
}

// Startup DB normalizer: run once at startup to idempotently normalize legacy JSON columns
builder.Services.AddHostedService<StartupDbNormalizer>();
// External request options (Prefer US domain / optional US proxy)
builder.Services.Configure<ExternalRequestOptions>(builder.Configuration.GetSection("ExternalRequests"));

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
    var swaggerDescription = string.Join(Environment.NewLine, new[]
    {
        "REST API for Listenarr audiobook management and automation.",
        "Versioning: URL segment format `/api/v{version}/...` (default version: v1).",
        "",
        "Authentication quick start:",
        "1. Click `Authorize` and enter one credential (you do not need all schemes).",
        "2. Browser session flow:",
        "   - Call `POST /api/v{version}/account/login` with `{ \"username\": \"...\", \"password\": \"...\", \"rememberMe\": false }`.",
        "   - The browser stores the `listenarr_session` HttpOnly cookie automatically when `authType` is `session`.",
        "   - Subsequent browser requests authenticate with that cookie.",
        "3. API key flow:",
        "   - API keys are intended for non-browser clients such as scripts, bots, and integrations.",
        "   - Read the current key from `GET /api/v{version}/configuration/apikey` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
        "   - Rotate the key with `POST /api/v{version}/configuration/apikey/regenerate` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
        "   - `POST /api/v{version}/configuration/apikey/generate-initial` is localhost bootstrap only and typically returns 409 after setup.",
        "   - Use `ApiKeyHeader` (`<apiKey>`) or `ApiKeyAuthorization` (`ApiKey <apiKey>`)."
    });

    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Listenarr API",
        Version = "v1",
        Description = swaggerDescription
    });

    options.AddSecurityDefinition("ApiKeyHeader", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = string.Join(Environment.NewLine, new[]
        {
            "Use `X-Api-Key: <apiKey>`.",
            "API keys are auto-generated on first run.",
            "Read the current key from `GET /api/v{version}/configuration/apikey` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
            "Regenerate with `POST /api/v{version}/configuration/apikey/regenerate` (Administrator session required when authentication is enabled; local/private-network access required when disabled)."
        })
    });

    options.AddSecurityDefinition("ApiKeyAuthorization", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "Authorization",
        Description = string.Join(Environment.NewLine, new[]
        {
            "Use `Authorization: ApiKey <apiKey>`.",
            "API keys are auto-generated on first run.",
            "Read the current key from `GET /api/v{version}/configuration/apikey` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
            "Regenerate with `POST /api/v{version}/configuration/apikey/regenerate` (Administrator session required when authentication is enabled; local/private-network access required when disabled)."
        })
    });

    // Try to include XML comments if available
    try
    {
        var xmlFile = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".xml";
        var xmlPath = Path.Join(AppContext.BaseDirectory, xmlFile);
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
    // Use full type names for schema Ids (replace "+" from nested types with ".") to
    // avoid collisions between nested controller DTOs and top-level DTOs that share
    // the same simple type name (e.g. TranslatePathRequest).
    options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace('+', '.'));
    options.OperationFilter<GlobalApiDocumentationOperationFilter>();
    options.DocumentFilter<SwaggerSecurityRequirementDocumentFilter>();
    options.DocumentFilter<SwaggerTagOrderDocumentFilter>();

    // Resolve conflicting actions (ambiguous HTTP method actions) by selecting the first
    // description. This prevents Swagger generation failures when multiple action descriptors
    // map to similar routes. If more complex disambiguation is needed in future, refine here.
    options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
    options.DocInclusionPredicate((docName, apiDescription) =>
    {
        // For now we expose v1 docs; include explicit v1 descriptions plus
        // ungrouped descriptions as fallback to keep docs non-breaking.
        var groupName = apiDescription.GroupName;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return string.Equals(docName, "v1", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(groupName, docName, StringComparison.OrdinalIgnoreCase);
    });
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
// and use a proxy/front-end on a different port. Keep the cookie policy aligned
// with the request so local HTTP continues to work without forcing an insecure
// "always send over HTTP" policy.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAntiforgery(options =>
    {
        options.HeaderName = "X-XSRF-TOKEN";
        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
        // During local development the frontend often runs on a different origin
        // (Vite dev server). Use SameSite=Lax so the browser will accept the
        // cookie for proxied same-site requests to the Vite dev server. In our
        // setup the dev server proxies /api requests, so Lax remains sufficient.
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
    });
}

// Persist Data Protection keys to disk so antiforgery tokens/cookies remain valid
// across process restarts and between instances during local development.
// This avoids issues where tokens are protected with an ephemeral key ring and
// cannot be validated later.
{
    var keyDir = Path.Join(builder.Environment.ContentRootPath, "config", "dataprotection-keys");
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    var apiVersionDescriptionProvider = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        foreach (var description in apiVersionDescriptionProvider.ApiVersionDescriptions)
        {
            options.SwaggerEndpoint(
                $"/swagger/{description.GroupName}/swagger.json",
                $"Listenarr API {description.GroupName.ToUpperInvariant()}");
        }
    });
}

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = exceptionFeature?.Error;
        Log.Logger.Error(exception, "Unhandled API exception for {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Internal server error",
            Detail = app.Environment.IsDevelopment() ? exception?.Message : null,
            Instance = context.Request.Path,
        };

        await context.Response.WriteAsJsonAsync(problem);
    });
});

// Use forwarded headers middleware early in the pipeline.
// Options are configured in DI to trust common private proxy networks.
app.UseForwardedHeaders();

// Note: HTTPS redirection is handled by the reverse proxy, not by this application

// Serve frontend static files from wwwroot (index.html + assets)
// DefaultFiles enables serving index.html when requesting '/'
// Map `/placeholder.svg` to the frontend `fe/public/placeholder.svg` so the API
// serves the exact same placeholder image used by the frontend without
// modifying any frontend files.
var frontendPlaceholderPath = Path.Join(app.Environment.ContentRootPath, "..", "fe", "public", "placeholder.svg");
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
        var fallback = Path.Join(app.Environment.ContentRootPath, "wwwroot", "placeholder.svg");
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
var cacheImagesPath = Path.Join(app.Environment.ContentRootPath, "config", "cache", "images");
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
app.UseMiddleware<RequestBodyLoggingMiddleware>();

// Enable CORS only in development (production should use reverse proxy for CORS)
if (app.Environment.IsDevelopment())
{
    app.UseCors("DevOnly");
}
// Session-based authentication middleware
app.UseMiddleware<SessionAuthenticationMiddleware>();
// API key middleware: allows requests with a valid X-Api-Key or Authorization: ApiKey <key>
app.UseMiddleware<ApiKeyMiddleware>();
// Enforce authentication based on startup config
app.UseMiddleware<AuthenticationEnforcerMiddleware>();
// Validate antiforgery tokens for unsafe methods
app.UseMiddleware<AntiforgeryValidationMiddleware>();
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
