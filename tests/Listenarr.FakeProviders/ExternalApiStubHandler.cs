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
using System.Net.Http.Headers;
using System.Text;
using Listenarr.FakeProviders.Routes;

namespace Listenarr.FakeProviders;

/// <summary>
/// Answers the metadata providers' requests from the fixtures in <c>Fixtures/{Provider}/</c>, each
/// provider's routes in its own class (<c>Routes/</c>). Anything else is answered 404.
/// </summary>
internal sealed class ExternalApiStubHandler : HttpMessageHandler
{
    /// <summary>
    /// When set, a file each request without a fixture is appended to, one line per request, which
    /// scripts/test-fixtures/record-provider-fixtures.mjs reads to record the missing fixtures.
    /// </summary>
    public const string MissingFixturesFileVariable = "LISTENARR_TEST_MISSING_FIXTURES";

    private const string ResourcePrefix = "Listenarr.FakeProviders.Fixtures/";

    private static readonly ProviderRoutes[] Providers =
    [
        new AudibleRoutes(),
        new AudnexusRoutes(),
        new OpenLibraryRoutes(),
    ];

    // "{Provider}/{file}" -> manifest resource name (the separator of RecursiveDir depends on the OS).
    private static readonly Dictionary<string, string> Fixtures = typeof(ExternalApiStubHandler).Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .ToDictionary(name => name[ResourcePrefix.Length..].Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

    private static readonly object MissingFixturesLock = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestUri = request.RequestUri;
        var provider = requestUri == null ? null : Providers.FirstOrDefault(routes => routes.Handles(requestUri));
        var route = request.Method == HttpMethod.Get ? provider?.Resolve(requestUri!) : null;

        if (route != null)
        {
            foreach (var candidate in route.Candidates)
            {
                if (Fixtures.TryGetValue($"{provider!.Name}/{candidate}", out var resourceName))
                {
                    return Task.FromResult(CreateResponse(resourceName, candidate));
                }
            }
        }

        // No fixture (another host, a route without its file): 404, and a line in the log naming the
        // fixture that would answer, so a suite's author can record it.
        ReportMissingFixture(request, route == null ? null : $"{provider!.Name}/{route.RecordAs}");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No test fixture matches this request.", Encoding.UTF8, "text/plain")
        });
    }

    private static void ReportMissingFixture(HttpRequestMessage request, string? fixture)
    {
        var line = fixture == null
            ? $"External API stub: no fixture for {request.Method} {request.RequestUri}"
            : $"External API stub: no fixture for {request.Method} {request.RequestUri} -> record as {fixture}";
        Console.Error.WriteLine(line);

        var missingFixturesFile = Environment.GetEnvironmentVariable(MissingFixturesFileVariable);
        if (string.IsNullOrWhiteSpace(missingFixturesFile))
        {
            return;
        }

        lock (MissingFixturesLock)
        {
            File.AppendAllText(missingFixturesFile, line + Environment.NewLine);
        }
    }

    private static HttpResponseMessage CreateResponse(string resourceName, string fixtureName)
    {
        using var stream = typeof(ExternalApiStubHandler).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded fixture resource {resourceName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        var content = new ByteArrayContent(buffer.ToArray());
        var mediaType = Path.GetExtension(fixtureName).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/json",
        };
        content.Headers.ContentType = mediaType.StartsWith("image/", StringComparison.Ordinal)
            ? new MediaTypeHeaderValue(mediaType)
            : new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
