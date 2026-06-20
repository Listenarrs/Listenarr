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

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Listenarr.Api.Startup;

public static class ListenarrBuilderFactory
{
    public static WebApplicationBuilder Create(
        string[] args,
        ILogEventSink realtimeLogSink,
        IFileSystem fileSystem)
    {
        var contentRootPath = ResolveContentRootPath();
        var environmentName = ResolveEnvironmentName();

        if (string.Equals("Test", environmentName, StringComparison.Ordinal))
        {
            var testContentRootPath = Path.Combine(Path.GetTempPath(), "ListenarrTests");
            fileSystem.CreateDirectory(testContentRootPath);
            Environment.SetEnvironmentVariable("LISTENARR_CONTENT_ROOT", testContentRootPath);
            contentRootPath = testContentRootPath;
        }

        contentRootPath = ApplyContentRootOverride(contentRootPath, fileSystem);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRootPath,
            EnvironmentName = environmentName
        });

        EnsureExternalConfiguration(builder.Environment.ContentRootPath, fileSystem);
        builder.Configuration.AddJsonFile(
            Path.Join("config", "appsettings", "appsettings.json"),
            optional: true,
            reloadOnChange: true);

        ConfigureSerilog(builder, realtimeLogSink);
        ConfigureDefaultUrls(builder, args);

        return builder;
    }

    private static string ResolveEnvironmentName()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        if (string.IsNullOrEmpty(environmentName))
        {
            environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        }

        if (string.IsNullOrEmpty(environmentName))
        {
            environmentName = "Production";
        }

        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        if (string.Equals(processName, "testhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("LISTENARR_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSTEST_SESSION_ID")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_TEST_RUNNER")))
        {
            environmentName = "Test";
        }

        return environmentName;
    }

    private static string ResolveContentRootPath()
        => AppContext.BaseDirectory;

    private static string ApplyContentRootOverride(string contentRootPath, IFileSystem fileSystem)
    {
        var contentRootOverride = Environment.GetEnvironmentVariable("LISTENARR_CONTENT_ROOT");
        if (string.IsNullOrWhiteSpace(contentRootOverride))
        {
            return contentRootPath;
        }

        try
        {
            contentRootOverride = Path.GetFullPath(contentRootOverride);

            if (!fileSystem.DirectoryExists(contentRootOverride))
            {
                Console.WriteLine($"[Listenarr] LISTENARR_CONTENT_ROOT '{contentRootOverride}' does not exist; creating it");
                fileSystem.CreateDirectory(contentRootOverride);
            }

            return contentRootOverride;
        }
        catch (Exception)
        {
            Console.WriteLine($"[Listenarr] Error: LISTENARR_CONTENT_ROOT '{contentRootOverride}' cannot be used or created; ignoring override.");
            return contentRootPath;
        }
    }

    internal static void EnsureExternalConfiguration(string contentRootPath, IFileSystem fileSystem)
    {
        var externalConfigRelative = Path.Join("config", "appsettings", "appsettings.json");
        var externalConfigAbsolute = Path.Join(contentRootPath, externalConfigRelative);

        try
        {
            var dir = Path.GetDirectoryName(externalConfigAbsolute) ?? string.Empty;
            if (!fileSystem.DirectoryExists(dir)) fileSystem.CreateDirectory(dir);

            if (!fileSystem.FileExists(externalConfigAbsolute))
            {
                if (!fileSystem.TryValidateMutationTarget(
                        externalConfigAbsolute,
                        [contentRootPath],
                        out var safeExternalConfigAbsolute,
                        out var reason))
                {
                    throw new IOException(
                        $"External config path is outside the resolved content root: {LogRedaction.SanitizeText(reason)}");
                }

                var defaultJson = "{\n  \"Serilog\": {\n    \"MinimumLevel\": {\n      \"Default\": \"Information\",\n      \"Override\": {\n        \"Microsoft\": \"Warning\",\n        \"System\": \"Warning\"\n      }\n    }\n  }\n}";
                fileSystem.WriteAllText(safeExternalConfigAbsolute, defaultJson);
                Console.WriteLine($"[Listenarr] Created default configuration at '{safeExternalConfigAbsolute}'. Edit this file to customize app settings.");
            }
        }
        catch (Exception ex) when (
            ex is IOException
            || ex is UnauthorizedAccessException
            || ex is System.Security.SecurityException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            Console.WriteLine($"[Listenarr] Warning: failed to create default config '{externalConfigRelative}': {ex.Message}");
        }
    }

    private static void ConfigureSerilog(WebApplicationBuilder builder, ILogEventSink realtimeLogSink)
    {
        var logFilePath = Path.Join(builder.Environment.ContentRootPath, "config", "logs", "listenarr-.log");
        var logLevelEnv = Environment.GetEnvironmentVariable("LISTENARR_LOG_LEVEL");
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

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Machine", Environment.MachineName)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .Enrich.WithProperty("Application", "Listenarr.Api")
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 5,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(realtimeLogSink)
            .CreateLogger();

        builder.Host.UseSerilog();

        if (builder.Environment.IsEnvironment("Test"))
        {
            Log.Logger = Serilog.Core.Logger.None;
        }

    }

    private static void ConfigureDefaultUrls(WebApplicationBuilder builder, string[] args)
    {
        var hasUrlsArg = args.Any(arg =>
            arg.Equals("--urls", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase));
        var hasUrlsConfig =
            !string.IsNullOrWhiteSpace(builder.Configuration["urls"]) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_URLS")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS"));

        if (!hasUrlsArg && !hasUrlsConfig)
        {
            builder.WebHost.UseUrls("http://*:4545");
        }
    }
}
