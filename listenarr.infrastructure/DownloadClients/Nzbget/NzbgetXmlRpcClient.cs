/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed record NzbgetXmlRpcRequest
    {
        public required DownloadClientConfiguration Client { get; init; }
        public required string MethodName { get; init; }
        public IReadOnlyList<object> Parameters { get; init; } = [];
    }

    internal sealed class NzbgetXmlRpcClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _clientType;

        public NzbgetXmlRpcClient(IHttpClientFactory httpClientFactory, string clientType)
        {
            _httpClientFactory = httpClientFactory;
            _clientType = clientType;
        }

        public Task<XElement> CallAsync(DownloadClientConfiguration client, string methodName, params object[] parameters)
        {
            return CallAsync(
                new NzbgetXmlRpcRequest
                {
                    Client = client,
                    MethodName = methodName,
                    Parameters = parameters
                },
                CancellationToken.None);
        }

        internal async Task<XElement> CallAsync(
            NzbgetXmlRpcRequest request,
            CancellationToken cancellationToken)
        {
            var client = request.Client;
            var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/xmlrpc").ToString();
            var httpClient = _httpClientFactory.CreateClient(_clientType);

            var methodCall = new XElement("methodCall",
                new XElement("methodName", request.MethodName),
                new XElement("params",
                    request.Parameters.Select(p => new XElement("param", new XElement("value", SerializeValue(p))))
                )
            );

            var xmlContent = $"<?xml version=\"1.0\"?>\n{methodCall}";
            var content = new StringContent(xmlContent, Encoding.UTF8, "text/xml");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl) { Content = content };
            var authHeader = BuildAuthHeader(client);
            if (authHeader != null)
            {
                httpRequest.Headers.Authorization = authHeader;
            }

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"NZBGet XML-RPC error: {response.StatusCode} - {responseBody}", null, response.StatusCode);
            }

            var doc = XDocument.Parse(responseBody);
            var fault = doc.Root?.Element("fault");
            if (fault != null)
            {
                var faultStruct = fault.Descendants("member").ToDictionary(
                    m => m.Element("name")?.Value ?? string.Empty,
                    m => m.Element("value")?.Value ?? string.Empty
                );
                var faultString = faultStruct.GetValueOrDefault("faultString", "Unknown error");
                throw new Exception($"NZBGet XML-RPC fault: {faultString}");
            }

            return doc.Root?.Element("params")?.Element("param")?.Element("value")
                ?? throw new Exception("Invalid XML-RPC response");
        }

        private static XElement SerializeValue(object value)
        {
            return value switch
            {
                string s => new XElement("string", s),
                int i => new XElement("i4", i),
                bool b => new XElement("boolean", b ? "1" : "0"),
                double d => new XElement("double", d.ToString(CultureInfo.InvariantCulture)),
                int[] arr => new XElement("array",
                    new XElement("data",
                        arr.Select(item => new XElement("value", new XElement("i4", item)))
                    )
                ),
                object[] arr => new XElement("array",
                    new XElement("data",
                        arr.Select(item => new XElement("value", SerializeValue(item)))
                    )
                ),
                Dictionary<string, object> dict => new XElement("struct",
                    dict.Select(kvp => new XElement("member",
                        new XElement("name", kvp.Key),
                        new XElement("value", SerializeValue(kvp.Value))
                    ))
                ),
                _ => new XElement("string", value.ToString() ?? string.Empty)
            };
        }

        private static AuthenticationHeaderValue? BuildAuthHeader(DownloadClientConfiguration client)
        {
            if (string.IsNullOrWhiteSpace(client.Username))
            {
                return null;
            }

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
            return new AuthenticationHeaderValue("Basic", credentials);
        }
    }
}
