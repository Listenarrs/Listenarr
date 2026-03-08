using System.Linq;
using System.Text.Json;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services;

public static class ApiResponseRedactor
{
    public const string RedactedValue = "REDACTED";

    public static ApiConfiguration RedactApiConfiguration(ApiConfiguration config)
    {
        var clone = Clone(config);
        if (!string.IsNullOrWhiteSpace(clone.ApiKey))
        {
            clone.ApiKey = RedactedValue;
        }

        clone.Headers = RedactStringDictionary(clone.Headers);
        clone.Parameters = RedactStringDictionary(clone.Parameters);
        return clone;
    }

    public static DownloadClientConfiguration RedactDownloadClientConfiguration(DownloadClientConfiguration config)
    {
        var clone = Clone(config);

        if (!string.IsNullOrWhiteSpace(clone.Username))
        {
            clone.Username = RedactedValue;
        }

        if (!string.IsNullOrWhiteSpace(clone.Password))
        {
            clone.Password = RedactedValue;
        }

        clone.Settings = RedactObjectDictionary(clone.Settings);
        return clone;
    }

    public static ApplicationSettings RedactApplicationSettings(ApplicationSettings settings)
    {
        var clone = Clone(settings);
        clone.AdminUsername = null;
        clone.AdminPassword = null;

        if (!string.IsNullOrWhiteSpace(clone.WebhookUrl))
        {
            clone.WebhookUrl = RedactedValue;
        }

        if (!string.IsNullOrWhiteSpace(clone.DiscordBotToken))
        {
            clone.DiscordBotToken = RedactedValue;
        }

        if (clone.Webhooks != null)
        {
            foreach (var webhook in clone.Webhooks.Where(w => !string.IsNullOrWhiteSpace(w.Url)))
            {
                webhook.Url = RedactedValue;
            }
        }

        return clone;
    }

    public static StartupConfig RedactStartupConfig(StartupConfig config, bool redactApiKey = true)
    {
        var clone = Clone(config);
        if (redactApiKey && !string.IsNullOrWhiteSpace(clone.ApiKey))
        {
            clone.ApiKey = RedactedValue;
        }

        if (!string.IsNullOrWhiteSpace(clone.SslCertPassword))
        {
            clone.SslCertPassword = RedactedValue;
        }

        return clone;
    }

    public static Indexer RedactIndexer(Indexer indexer)
    {
        var clone = Clone(indexer);
        if (!string.IsNullOrWhiteSpace(clone.ApiKey))
        {
            clone.ApiKey = RedactedValue;
        }

        if (!string.IsNullOrWhiteSpace(clone.AdditionalSettings))
        {
            clone.AdditionalSettings = RedactedValue;
        }

        return clone;
    }

    public static object ToDownloadClientSummaryResponse(DownloadClientConfiguration config)
    {
        return new
        {
            config.Id,
            config.Name,
            config.Type,
            config.Host,
            config.Port,
            config.Username,
            config.UseSSL,
            config.IsEnabled,
            config.RemoveCompletedDownloads,
            Settings = config.Settings,
            config.CreatedAt
        };
    }

    public static object ToDownloadClientDetailResponse(DownloadClientConfiguration config)
    {
        return new
        {
            config.Id,
            config.Name,
            config.Type,
            config.Host,
            config.Port,
            config.Username,
            config.UseSSL,
            config.IsEnabled,
            config.RemoveCompletedDownloads,
            Settings = config.Settings,
            config.CreatedAt
        };
    }

    private static Dictionary<string, string> RedactStringDictionary(Dictionary<string, string>? input)
    {
        if (input == null) return new Dictionary<string, string>();
        var clone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in input)
        {
            clone[kvp.Key] = IsSensitiveKey(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value)
                ? RedactedValue
                : kvp.Value;
        }
        return clone;
    }

    private static Dictionary<string, object> RedactObjectDictionary(Dictionary<string, object>? input)
    {
        if (input == null) return new Dictionary<string, object>();
        var clone = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in input)
        {
            if (IsSensitiveKey(kvp.Key))
            {
                clone[kvp.Key] = RedactedValue;
                continue;
            }

            clone[kvp.Key] = kvp.Value;
        }
        return clone;
    }

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim().ToLowerInvariant();
        return k.Contains("apikey")
               || k.Contains("api_key")
               || k.Contains("token")
               || k.Contains("secret")
               || k.Contains("password")
               || k == "mam_id"
               || k.Contains("cookie")
               || k.Contains("authorization");
    }

    private static T Clone<T>(T value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException($"Failed to clone {typeof(T).Name}");
    }
}
